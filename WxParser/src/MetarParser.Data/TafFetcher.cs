using MetarParser.Data.Entities;
using Microsoft.EntityFrameworkCore;
using TafParser;

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
    /// Fetches all TAF reports within the bounding box centred on
    /// (<paramref name="lat"/>, <paramref name="lon"/>) with a half-width of
    /// <paramref name="boxDegrees"/> degrees, then parses, deduplicates, and
    /// inserts any reports not already in the database.
    /// </summary>
    public static async Task FetchAndInsertAsync(
        double lat, double lon, double boxDegrees,
        DbContextOptions<WeatherDataContext> dbOptions,
        HttpClient httpClient)
    {
        var bbox = $"{lat - boxDegrees},{lon - boxDegrees},{lat + boxDegrees},{lon + boxDegrees}";
        var url  = $"{TafApiBase}?bbox={bbox}&hours=24&format=raw";

        Console.WriteLine($"Fetching: {url}");

        string raw;
        try
        {
            raw = await httpClient.GetStringAsync(url);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fetch failed: {ex.Message}");
            return;
        }

        var lines = GroupTafLines(raw);

        if (lines.Count == 0)
        {
            Console.WriteLine("No TAF reports were returned.");
            return;
        }

        Console.WriteLine($"Received {lines.Count} TAF(s). Parsing...");

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
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"  Parse error: {ex.Message}");
                Console.Error.WriteLine($"    Input: {line}");
                Console.ResetColor();
                parseErrors++;
            }
        }

        if (parsed.Count == 0)
        {
            Console.WriteLine($"No TAFs parsed successfully ({parseErrors} parse error(s)).");
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
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Error.WriteLine($"Database error during batch insert: {ex.InnerException?.Message ?? ex.Message}");
                Console.ResetColor();
                return;
            }
        }

        Console.WriteLine($"Done.  Inserted: {inserted}  Skipped (already stored): {skipped}  Parse errors: {parseErrors}");
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
