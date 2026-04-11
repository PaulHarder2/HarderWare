using MetarParser.Data.Entities;
using Microsoft.EntityFrameworkCore;
using TafParser;
using WxServices.Logging;

namespace MetarParser.Data;

/// <summary>
/// Downloads a batch of TAF (Terminal Aerodrome Forecast) reports from the
/// Aviation Weather Center API for a geographic bounding box, parses them,
/// filters out forecasts already stored in the database, and inserts the new
/// records in a single database round-trip.
/// </summary>
public static class TafFetcher
{
    private const string TafApiBase = "https://aviationweather.gov/api/data/taf";

    /// <summary>
    /// Fetches all TAF reports within the given geographic region,
    /// then parses, deduplicates, and inserts any reports not already in the database.
    /// </summary>
    /// <param name="region">Geographic bounding box for the API query.</param>
    /// <param name="dbOptions">EF Core options for deduplication queries and insertion.</param>
    /// <param name="httpClient">HTTP client for the Aviation Weather Center API request.</param>
    /// <sideeffects>Inserts new <see cref="TafRecord"/> rows into the database. Writes progress and error log entries.</sideeffects>
    public static async Task FetchAndInsertAsync(
        WxServices.Common.FetchRegion region,
        DbContextOptions<WeatherDataContext> dbOptions,
        HttpClient httpClient)
    {
        var url = $"{TafApiBase}?bbox={region.ToAwcBbox()}&hours=24&format=raw";

        Logger.Info($"Fetching: {url}");

        string raw;
        try
        {
            raw = await httpClient.GetStringAsync(url);
        }
        catch (Exception ex)
        {
            Logger.Error($"TAF fetch failed: {ex.Message}");
            return;
        }

        var lines = GroupTafLines(raw);

        if (lines.Count == 0)
        {
            Logger.Warn("No TAF reports were returned.");
            return;
        }

        Logger.Info($"Received {lines.Count} TAF(s). Parsing...");

        var parsed     = new List<(TafReport Report, TafRecord Entity)>();
        int parseErrors = 0;

        foreach (var line in lines)
        {
            try
            {
                var report = TafParser.TafParser.Parse(line);
                var entity = TafRecordMapper.ToEntity(report);
                parsed.Add((report, entity));
            }
            catch (TafParseException ex)
            {
                Logger.Error($"TAF parse error: {ex.Message} — input: {line}");
                parseErrors++;
            }
        }

        if (parsed.Count == 0)
        {
            Logger.Warn($"No TAFs parsed successfully ({parseErrors} parse error(s)).");
            return;
        }

        using var ctx = new WeatherDataContext(dbOptions);

        var stations = parsed.Select(p => p.Entity.StationIcao).Distinct().ToList();
        var minTime  = parsed.Min(p => p.Entity.IssuanceUtc);

        var existingKeys = ctx.Tafs
            .Where(t => stations.Contains(t.StationIcao) && t.IssuanceUtc >= minTime)
            .Select(t => new { t.StationIcao, t.IssuanceUtc, t.ReportType })
            .AsEnumerable()
            .Select(t => (t.StationIcao, t.IssuanceUtc, t.ReportType))
            .ToHashSet();

        int inserted = 0, skipped = 0;

        foreach (var (_, entity) in parsed)
        {
            var key = (entity.StationIcao, entity.IssuanceUtc, entity.ReportType);
            if (existingKeys.Contains(key)) { skipped++; continue; }
            ctx.Tafs.Add(entity);
            inserted++;
        }

        if (inserted > 0)
        {
            try { ctx.SaveChanges(); }
            catch (DbUpdateException ex)
            {
                Logger.Error($"Database error during TAF batch insert: {ex.InnerException?.Message ?? ex.Message}");
                return;
            }
        }

        Logger.Info($"TAF fetch done. Inserted: {inserted}  Skipped: {skipped}  Parse errors: {parseErrors}");
    }

    /// <summary>
    /// Groups raw API response lines into one string per TAF report.
    /// A new report begins on any line that starts with "TAF".
    /// Continuation lines (TEMPO, BECMG, FM, PROB, or any indented line)
    /// are appended to the current report on a new indented line, preserving
    /// the original multi-line layout for storage in the database.
    /// Trailing "=" end-of-message markers are stripped.
    /// The parser normalises all internal whitespace, so newlines are harmless.
    /// </summary>
    /// <param name="raw">Raw text response from the Aviation Weather Center TAF endpoint.</param>
    /// <returns>
    /// A list of strings, each representing one complete TAF in a normalised
    /// multi-line format (continuation lines indented with two spaces).
    /// </returns>
    public static List<string> GroupTafLines(string raw)
    {
        var reports = new List<string>();
        string? current = null;

        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim().TrimEnd('=');
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            if (trimmed.StartsWith("TAF", StringComparison.Ordinal))
            {
                if (current is not null) reports.Add(current);
                current = trimmed;
            }
            else if (current is not null)
            {
                current += "\n  " + trimmed;
            }
        }

        if (current is not null) reports.Add(current);
        return reports;
    }
}
