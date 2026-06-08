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
    /// Report count at which a bbox response is suspiciously large — see
    /// <see cref="MetarFetcher.AwcCapSuspicionThreshold"/> (WX-140); the AWC
    /// API truncates oversized TAF bbox responses the same silent way.
    /// </summary>
    internal const int AwcCapSuspicionThreshold = MetarFetcher.AwcCapSuspicionThreshold;

    /// <summary>
    /// Fetches all TAF reports within the given geographic region,
    /// then parses, deduplicates, and inserts any reports not already in the database.
    /// </summary>
    /// <param name="region">Geographic bounding box for the API query.</param>
    /// <param name="dbOptions">EF Core options for deduplication queries and insertion.</param>
    /// <param name="httpClient">HTTP client for the Aviation Weather Center API request.</param>
    /// <sideeffects>Inserts new <see cref="TafRecord"/> rows into the database. Writes progress and error log entries.</sideeffects>
    public static Task<int> FetchAndInsertAsync(
        WxServices.Common.FetchRegion region,
        DbContextOptions<WeatherDataContext> dbOptions,
        HttpClient httpClient)
        => FetchUrlAndInsertAsync($"{TafApiBase}?bbox={region.ToAwcBbox()}&hours=24&format=raw", dbOptions, httpClient);

    /// <summary>
    /// Fetches the current TAF for a single station by ICAO identifier (WX-140:
    /// the by-station rescue path TAFs never had — a locality whose TAF station
    /// falls outside every fetch box, or is dropped by an AWC cap, still gets
    /// its forecast).
    /// </summary>
    /// <param name="stationIcao">ICAO identifier of the TAF station (e.g. <c>"KEND"</c>).</param>
    /// <param name="dbOptions">EF Core options for deduplication queries and insertion.</param>
    /// <param name="httpClient">HTTP client for the Aviation Weather Center API request.</param>
    /// <sideeffects>Inserts new <see cref="TafRecord"/> rows into the database. Writes progress and error log entries.</sideeffects>
    public static Task<int> FetchAndInsertByStationAsync(
        string stationIcao,
        DbContextOptions<WeatherDataContext> dbOptions,
        HttpClient httpClient)
        => FetchUrlAndInsertAsync($"{TafApiBase}?ids={stationIcao}&hours=24&format=raw", dbOptions, httpClient);

    /// <summary>
    /// Fetches current TAFs for a batch of stations in chunked <c>ids=</c>
    /// requests — one call per ~20 stations instead of one per station, so the
    /// per-cycle call count stays O(N/20) as the direct list grows (WX-140 review).
    /// </summary>
    /// <param name="stationIcaos">ICAO identifiers of the TAF stations.</param>
    /// <param name="dbOptions">EF Core options for deduplication queries and insertion.</param>
    /// <param name="httpClient">HTTP client for the Aviation Weather Center API requests.</param>
    /// <sideeffects>Inserts new <see cref="TafRecord"/> rows into the database. Writes progress and error log entries.</sideeffects>
    public static async Task FetchAndInsertByStationsAsync(
        IReadOnlyList<string> stationIcaos,
        DbContextOptions<WeatherDataContext> dbOptions,
        HttpClient httpClient)
    {
        const int chunkSize = 20;
        foreach (var chunk in stationIcaos.Chunk(chunkSize))
            await FetchUrlAndInsertAsync($"{TafApiBase}?ids={string.Join(',', chunk)}&hours=24&format=raw", dbOptions, httpClient);
    }

    // ── shared fetch/parse/insert logic ──────────────────────────────────────

    /// <returns>The number of TAF reports the response carried (the WX-140 adaptive split keys on this); 0 on failure.</returns>
    private static async Task<int> FetchUrlAndInsertAsync(
        string url,
        DbContextOptions<WeatherDataContext> dbOptions,
        HttpClient httpClient)
    {
        Logger.Info($"Fetching: {url}");

        string raw;
        try
        {
            raw = await httpClient.GetStringWithRetryAsync(url, "TAF");
        }
        catch (Exception ex)
        {
            Logger.Error($"TAF fetch failed after retries: {ex.Message}");
            return 0;
        }

        var lines = GroupTafLines(raw);

        if (lines.Count == 0)
        {
            // ids= requests for known-quiet stations legitimately come back
            // empty every cycle; only a bbox with nothing in it is surprising.
            if (url.Contains("bbox=", StringComparison.Ordinal))
                Logger.Warn($"No TAF reports were returned for: {url}");
            else
                Logger.Debug($"No TAF reports were returned for: {url}");
            return 0;
        }

        Logger.Info($"Received {lines.Count} TAF(s). Parsing...");

        // WX-140 truncation tripwire — see MetarFetcher for the rationale.
        if (lines.Count >= AwcCapSuspicionThreshold)
            Logger.Warn($"TAF response returned {lines.Count} report(s) — at or near the AWC response cap; "
                + $"the result may be silently truncated and stations silently missing. Shrink the bbox: {url}");

        var parsed = new List<(TafReport Report, TafRecord Entity)>();
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
                Logger.Warn($"TAF parse error: {ex.Message} — input: {line}");
                parseErrors++;
            }
        }

        if (parsed.Count == 0)
        {
            Logger.Warn($"No TAFs parsed successfully ({parseErrors} parse error(s)).");
            return 0;
        }

        using var ctx = new WeatherDataContext(dbOptions);

        var stations = parsed.Select(p => p.Entity.StationIcao).Distinct().ToList();
        var minTime = parsed.Min(p => p.Entity.IssuanceUtc);

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
                return lines.Count;
            }
        }

        Logger.Info($"TAF fetch done. Inserted: {inserted}  Skipped: {skipped}  Parse errors: {parseErrors}");
        return lines.Count;
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