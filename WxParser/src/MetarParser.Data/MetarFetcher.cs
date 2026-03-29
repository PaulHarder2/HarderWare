using MetarParser.Data.Entities;
using Microsoft.EntityFrameworkCore;
using WxParser.Logging;
using static MetarParser.MetarParser;

namespace MetarParser.Data;

/// <summary>
/// Downloads a batch of METAR/SPECI reports from the Aviation Weather Center
/// API for a geographic bounding box, parses them, filters out reports that
/// are already stored in the database, and inserts the new records in a single
/// database round-trip.
/// </summary>
public static class MetarFetcher
{
    private const string MetarApiBase = "https://aviationweather.gov/api/data/metar";

    /// <summary>
    /// Fetches all METAR and SPECI reports within the bounding box centred on
    /// (<paramref name="lat"/>, <paramref name="lon"/>) with a half-width of
    /// <paramref name="boxDegrees"/> degrees, then parses, deduplicates, and
    /// inserts any reports not already in the database.
    /// </summary>
    /// <param name="lat">Centre latitude of the bounding box in decimal degrees.</param>
    /// <param name="lon">Centre longitude of the bounding box in decimal degrees.</param>
    /// <param name="boxDegrees">Half-width of the bounding box in degrees (applied in all four directions). Must be &gt; 0.</param>
    /// <param name="dbOptions">EF Core options for deduplication queries and insertion.</param>
    /// <param name="httpClient">HTTP client for the Aviation Weather Center API request.</param>
    /// <sideeffects>Inserts new <see cref="MetarRecord"/> rows into the database. Writes progress and error log entries.</sideeffects>
    public static async Task FetchAndInsertAsync(
        double lat, double lon, double boxDegrees,
        DbContextOptions<WeatherDataContext> dbOptions,
        HttpClient httpClient)
    {
        if (boxDegrees <= 0)
        {
            Logger.Error($"MetarFetcher: boxDegrees must be > 0 (got {boxDegrees}) — skipping fetch.");
            return;
        }

        var bbox = $"{lat - boxDegrees},{lon - boxDegrees},{lat + boxDegrees},{lon + boxDegrees}";
        var url  = $"{MetarApiBase}?bbox={bbox}&hours=1&format=raw";
        await FetchUrlAndInsertAsync(url, dbOptions, httpClient);
    }

    /// <summary>
    /// Fetches the most recent METAR and SPECI reports for a single station by
    /// ICAO identifier, then parses, deduplicates, and inserts any reports not
    /// already in the database.
    /// </summary>
    /// <param name="stationIcao">ICAO identifier of the station to fetch (e.g. <c>"KDWH"</c>).</param>
    /// <param name="dbOptions">EF Core options for deduplication queries and insertion.</param>
    /// <param name="httpClient">HTTP client for the Aviation Weather Center API request.</param>
    /// <sideeffects>Inserts new <see cref="MetarRecord"/> rows into the database. Writes progress and error log entries.</sideeffects>
    public static async Task FetchAndInsertByStationAsync(
        string stationIcao,
        DbContextOptions<WeatherDataContext> dbOptions,
        HttpClient httpClient)
    {
        var url = $"{MetarApiBase}?ids={stationIcao}&hours=1&format=raw";
        await FetchUrlAndInsertAsync(url, dbOptions, httpClient);
    }

    // ── shared fetch/parse/insert logic ──────────────────────────────────────

    /// <summary>
    /// Downloads METAR/SPECI data from <paramref name="url"/>, parses each line,
    /// deduplicates against the database, and inserts new records in a single
    /// <see cref="WeatherDataContext.SaveChanges"/> call.
    /// Logs parse errors per line but continues processing remaining lines.
    /// </summary>
    /// <param name="url">Fully qualified Aviation Weather Center API URL to fetch.</param>
    /// <param name="dbOptions">EF Core options for the deduplication query and batch insert.</param>
    /// <param name="httpClient">HTTP client for the GET request.</param>
    /// <sideeffects>Inserts new <see cref="MetarRecord"/> rows into the database. Writes progress and error log entries.</sideeffects>
    private static async Task FetchUrlAndInsertAsync(
        string url,
        DbContextOptions<WeatherDataContext> dbOptions,
        HttpClient httpClient)
    {
        Logger.Info($"Fetching: {url}");

        string raw;
        try
        {
            raw = await httpClient.GetStringAsync(url);
        }
        catch (Exception ex)
        {
            Logger.Error($"METAR fetch failed: {ex.Message}");
            return;
        }

        var lines = raw.Split('\n',
                         StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries)
                       .Where(l => l.StartsWith("METAR", StringComparison.Ordinal) ||
                                   l.StartsWith("SPECI", StringComparison.Ordinal))
                       .ToList();

        if (lines.Count == 0)
        {
            Logger.Warn("No METAR/SPECI reports were returned.");
            return;
        }

        Logger.Info($"Received {lines.Count} METAR/SPECI report(s). Parsing...");

        var parsed     = new List<(MetarReport Report, MetarRecord Entity)>();
        int parseErrors = 0;

        foreach (var line in lines)
        {
            try
            {
                var report = Parse(line);
                var entity = MetarRecordMapper.ToEntity(report);
                parsed.Add((report, entity));
            }
            catch (MetarParseException ex)
            {
                Logger.Error($"METAR parse error: {ex.Message} — input: {line}");
                parseErrors++;
            }
        }

        if (parsed.Count == 0)
        {
            Logger.Warn($"No METAR reports parsed successfully ({parseErrors} parse error(s)).");
            return;
        }

        using var ctx = new WeatherDataContext(dbOptions);

        var stations = parsed.Select(p => p.Entity.StationIcao).Distinct().ToList();
        var minTime  = parsed.Min(p => p.Entity.ObservationUtc);

        var existingKeys = ctx.Metars
            .Where(m => stations.Contains(m.StationIcao) && m.ObservationUtc >= minTime)
            .Select(m => new { m.StationIcao, m.ObservationUtc, m.ReportType })
            .AsEnumerable()
            .Select(m => (m.StationIcao, m.ObservationUtc, m.ReportType))
            .ToHashSet();

        int inserted = 0, skipped = 0;

        foreach (var (_, entity) in parsed)
        {
            var key = (entity.StationIcao, entity.ObservationUtc, entity.ReportType);
            if (existingKeys.Contains(key)) { skipped++; continue; }
            ctx.Metars.Add(entity);
            inserted++;
        }

        if (inserted > 0)
        {
            try { ctx.SaveChanges(); }
            catch (DbUpdateException ex)
            {
                Logger.Error($"Database error during METAR batch insert: {ex.InnerException?.Message ?? ex.Message}");
                return;
            }
        }

        Logger.Info($"METAR fetch done. Inserted: {inserted}  Skipped: {skipped}  Parse errors: {parseErrors}");
    }
}
