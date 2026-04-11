using MetarParser.Data.Entities;
using Microsoft.EntityFrameworkCore;
using WxServices.Logging;
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
    /// Fetches all METAR and SPECI reports within the given geographic region,
    /// then parses, deduplicates, and inserts any reports not already in the database.
    /// </summary>
    /// <param name="region">Geographic bounding box for the API query.</param>
    /// <param name="dbOptions">EF Core options for deduplication queries and insertion.</param>
    /// <param name="httpClient">HTTP client for the Aviation Weather Center API request.</param>
    /// <sideeffects>Inserts new <see cref="MetarRecord"/> rows into the database. Writes progress and error log entries.</sideeffects>
    public static async Task FetchAndInsertAsync(
        WxServices.Common.FetchRegion region,
        DbContextOptions<WeatherDataContext> dbOptions,
        HttpClient httpClient)
    {
        var url = $"{MetarApiBase}?bbox={region.ToAwcBbox()}&hours=1&format=raw";
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
    /// deduplicates against the database, inserts new records in a single
    /// <see cref="WeatherDataContext.SaveChanges"/> call, and then upserts any
    /// station ICAOs not yet present in the <c>WxStations</c> table.
    /// Logs parse errors per line but continues processing remaining lines.
    /// </summary>
    /// <param name="url">Fully qualified Aviation Weather Center API URL to fetch.</param>
    /// <param name="dbOptions">EF Core options for the deduplication query and batch insert.</param>
    /// <param name="httpClient">HTTP client for the METAR GET request and AWC airport lookups.</param>
    /// <sideeffects>
    /// Inserts new <see cref="MetarRecord"/> rows into the database.
    /// Inserts new <see cref="WxStation"/> rows for any station ICAOs not yet in <c>WxStations</c>.
    /// Writes progress and error log entries.
    /// </sideeffects>
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
            Logger.Warn($"No METAR/SPECI reports were returned for: {url}");
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

        // ── Upsert any station ICAOs not yet in WxStations ───────────────────
        await UpsertNewStationsAsync(stations, dbOptions, httpClient);
    }

    /// <summary>
    /// For each ICAO in <paramref name="icaos"/> that is not already present in the
    /// <c>WxStations</c> table, queries the Aviation Weather Center airport API and
    /// inserts a new <see cref="WxStation"/> row.  ICAOs that cannot be resolved are
    /// skipped without failing the overall cycle.
    /// </summary>
    /// <param name="icaos">Station identifiers seen in the current METAR batch.</param>
    /// <param name="dbOptions">EF Core options for database access.</param>
    /// <param name="httpClient">HTTP client for AWC airport API lookups.</param>
    /// <sideeffects>
    /// Makes one HTTP GET request to the AWC airport API per unknown station.
    /// Inserts new <see cref="WxStation"/> rows.
    /// Writes info and warning log entries.
    /// </sideeffects>
    private static async Task UpsertNewStationsAsync(
        IReadOnlyList<string> icaos,
        DbContextOptions<WeatherDataContext> dbOptions,
        HttpClient httpClient)
    {
        using var ctx = new WeatherDataContext(dbOptions);

        var knownIds = ctx.WxStations
            .Where(s => icaos.Contains(s.IcaoId))
            .Select(s => s.IcaoId)
            .ToHashSet();

        var newIcaos = icaos.Where(id => !knownIds.Contains(id)).ToList();
        if (newIcaos.Count == 0) return;

        Logger.Info($"MetarFetcher: {newIcaos.Count} new station(s) to register: {string.Join(", ", newIcaos)}");

        foreach (var icao in newIcaos)
        {
            var station = await AirportLocator.LookupStationAsync(icao, httpClient);
            if (station is null)
            {
                // Insert a stub row with no coordinates so we don't retry on
                // every subsequent cycle for stations the AWC airport API
                // does not know about.
                Logger.Info($"MetarFetcher: '{icao}' not found in AWC airport database — registering as unresolved.");
                station = new Entities.WxStation { IcaoId = icao };
            }

            ctx.WxStations.Add(station);
            try
            {
                await ctx.SaveChangesAsync();
                var coords = station.Lat.HasValue ? $"{station.Lat:F3}/{station.Lon:F3}" : "unresolved";
                Logger.Info($"MetarFetcher: registered station '{icao}' ({station.Name ?? "no name"}, {coords}, elev {station.ElevationFt?.ToString("F0") ?? "unknown"} ft).");
            }
            catch (DbUpdateException ex)
            {
                // Another process may have inserted concurrently — not fatal.
                Logger.Warn($"MetarFetcher: DB error inserting station '{icao}': {ex.InnerException?.Message ?? ex.Message}");
                ctx.Entry(station).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            }
        }
    }
}
