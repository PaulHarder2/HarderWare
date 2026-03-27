using MetarParser.Data.Entities;
using Microsoft.EntityFrameworkCore;
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
    public static async Task FetchAndInsertAsync(
        double lat, double lon, double boxDegrees,
        DbContextOptions<WeatherDataContext> dbOptions,
        HttpClient httpClient)
    {
        var bbox = $"{lat - boxDegrees},{lon - boxDegrees},{lat + boxDegrees},{lon + boxDegrees}";
        var url  = $"{MetarApiBase}?bbox={bbox}&hours=1&format=raw";
        await FetchUrlAndInsertAsync(url, dbOptions, httpClient);
    }

    /// <summary>
    /// Fetches the most recent METAR and SPECI reports for a single station by
    /// ICAO identifier, then parses, deduplicates, and inserts any reports not
    /// already in the database.
    /// </summary>
    public static async Task FetchAndInsertByStationAsync(
        string stationIcao,
        DbContextOptions<WeatherDataContext> dbOptions,
        HttpClient httpClient)
    {
        var url = $"{MetarApiBase}?ids={stationIcao}&hours=1&format=raw";
        await FetchUrlAndInsertAsync(url, dbOptions, httpClient);
    }

    // ── shared fetch/parse/insert logic ──────────────────────────────────────

    private static async Task FetchUrlAndInsertAsync(
        string url,
        DbContextOptions<WeatherDataContext> dbOptions,
        HttpClient httpClient)
    {
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

        var lines = raw.Split('\n',
                         StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries)
                       .Where(l => l.StartsWith("METAR", StringComparison.Ordinal) ||
                                   l.StartsWith("SPECI", StringComparison.Ordinal))
                       .ToList();

        if (lines.Count == 0)
        {
            Console.WriteLine("No METAR/SPECI reports were returned.");
            return;
        }

        Console.WriteLine($"Received {lines.Count} report(s). Parsing...");

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
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"  Parse error: {ex.Message}");
                Console.Error.WriteLine($"    Input: {line}");
                Console.ResetColor();
                parseErrors++;
            }
        }

        if (parsed.Count == 0)
        {
            Console.WriteLine($"No reports parsed successfully ({parseErrors} parse error(s)).");
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
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Error.WriteLine($"Database error during batch insert: {ex.InnerException?.Message ?? ex.Message}");
                Console.ResetColor();
                return;
            }
        }

        Console.WriteLine($"Done.  Inserted: {inserted}  Skipped (already stored): {skipped}  Parse errors: {parseErrors}");
    }
}
