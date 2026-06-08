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
    /// Report count at which a bbox response is suspiciously large (WX-140):
    /// the AWC data API silently truncates continent-sized bbox queries at
    /// roughly 550–600 reports — no error, no marker, just a capped subset.
    /// A response at or above this threshold is logged as a WARN so silent
    /// coverage loss is loud; the WX-140 per-locality fetch plan keeps healthy
    /// responses far below it.
    /// </summary>
    public const int AwcCapSuspicionThreshold = 500;

    // WX-140 inline cleanup: identical per-line parse failures (e.g. the MYGF
    // automated METAR that omits its date/time group at the source) recur on
    // every 10-minute cycle and were WARN-spamming the log. First occurrence
    // of each (station, message) signature stays WARN; repeats drop to DEBUG.
    // Bounded: one entry per distinct failing station+message for the process
    // lifetime.
    private static readonly HashSet<string> _seenParseFailures = [];
    private static readonly object _seenParseFailuresLock = new();

    /// <summary>
    /// Fetches all METAR and SPECI reports within the given geographic region,
    /// then parses, deduplicates, and inserts any reports not already in the database.
    /// </summary>
    /// <param name="region">Geographic bounding box for the API query.</param>
    /// <param name="dbOptions">EF Core options for deduplication queries and insertion.</param>
    /// <param name="httpClient">HTTP client for the Aviation Weather Center API request.</param>
    /// <sideeffects>Inserts new <see cref="MetarRecord"/> rows into the database. Writes progress and error log entries.</sideeffects>
    /// <returns>The number of reports the response carried (the WX-140 adaptive split keys on this); 0 on failure.</returns>
    public static async Task<int> FetchAndInsertAsync(
        WxServices.Common.FetchRegion region,
        DbContextOptions<WeatherDataContext> dbOptions,
        HttpClient httpClient)
    {
        var url = $"{MetarApiBase}?bbox={region.ToAwcBbox()}&hours=1&format=raw";
        return (await FetchUrlAndInsertAsync(url, dbOptions, httpClient)).LineCount;
    }

    /// <summary>
    /// Fetches recent METAR/SPECI reports for a batch of stations in chunked
    /// <c>ids=</c> requests (WX-140 gap fill / direct list), and reports each
    /// productive station with its newest observation time — the gap filler
    /// uses the age to distinguish genuinely bbox-unreliable stations from
    /// slow-cadence or just-published ones before promoting to
    /// <c>AlwaysFetchDirect</c>.
    /// </summary>
    /// <param name="stationIcaos">ICAO identifiers to fetch.</param>
    /// <param name="dbOptions">EF Core options for deduplication queries and insertion.</param>
    /// <param name="httpClient">HTTP client for the Aviation Weather Center API requests.</param>
    /// <param name="hours">
    /// AWC lookback window.  The gap filler passes 3 to match the freshness
    /// window that defines a gap — fetching 1 hour against a 3-hour freshness
    /// test left slow-cadence stations as permanent phantom gaps (WX-140 review).
    /// </param>
    /// <returns>One entry per station that returned a parseable report, with its newest observation time.</returns>
    /// <sideeffects>Inserts new <see cref="MetarRecord"/> rows into the database. Writes progress and error log entries.</sideeffects>
    public static async Task<IReadOnlyList<(string Icao, DateTime NewestObsUtc)>> FetchAndInsertByStationsAsync(
        IReadOnlyList<string> stationIcaos,
        DbContextOptions<WeatherDataContext> dbOptions,
        HttpClient httpClient,
        int hours = 1)
    {
        const int chunkSize = 20;
        var productive = new List<(string Icao, DateTime NewestObsUtc)>();
        foreach (var chunk in stationIcaos.Chunk(chunkSize))
        {
            var url = $"{MetarApiBase}?ids={string.Join(',', chunk)}&hours={hours}&format=raw";
            productive.AddRange((await FetchUrlAndInsertAsync(url, dbOptions, httpClient)).Stations);
        }
        return productive;
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
    /// <returns>The raw response report count plus the distinct stations that produced a parseable report (each with its newest observation time); empty on fetch failure or an empty response.</returns>
    private static async Task<FetchResult> FetchUrlAndInsertAsync(
        string url,
        DbContextOptions<WeatherDataContext> dbOptions,
        HttpClient httpClient)
    {
        Logger.Info($"Fetching: {url}");

        string raw;
        try
        {
            raw = await httpClient.GetStringWithRetryAsync(url, "METAR");
        }
        catch (Exception ex)
        {
            Logger.Error($"METAR fetch failed after retries: {ex.Message}");
            return FetchResult.Empty;
        }

        var lines = raw.Split('\n',
                         StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries)
                       .Where(l => l.StartsWith("METAR", StringComparison.Ordinal) ||
                                   l.StartsWith("SPECI", StringComparison.Ordinal))
                       .ToList();

        if (lines.Count == 0)
        {
            // ids= requests for defined-but-silent airfields legitimately come
            // back empty; only an empty bbox is surprising.
            if (url.Contains("bbox=", StringComparison.Ordinal))
                Logger.Warn($"No METAR/SPECI reports were returned for: {url}");
            else
                Logger.Debug($"No METAR/SPECI reports were returned for: {url}");
            return FetchResult.Empty;
        }

        Logger.Info($"Received {lines.Count} METAR/SPECI report(s). Parsing...");

        // WX-140 truncation tripwire: the AWC API caps oversized bbox
        // responses silently, so a "successful" fetch can be missing most of
        // its stations. Make the cap loud instead of invisible.
        if (lines.Count >= AwcCapSuspicionThreshold)
            Logger.Warn($"METAR response returned {lines.Count} report(s) — at or near the AWC response cap; "
                + $"the result may be silently truncated and stations silently missing. Shrink the bbox: {url}");

        var parsed = new List<(MetarReport Report, MetarRecord Entity)>();
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
                LogParseFailure(ex.Message, line);
                parseErrors++;
            }
        }

        if (parsed.Count == 0)
        {
            Logger.Warn($"No METAR reports parsed successfully ({parseErrors} parse error(s)).");
            return new FetchResult(lines.Count, []);
        }

        using var ctx = new WeatherDataContext(dbOptions);

        var stations = parsed.Select(p => p.Entity.StationIcao).Distinct().ToList();
        var minTime = parsed.Min(p => p.Entity.ObservationUtc);

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
                return new FetchResult(lines.Count, []);
            }
        }

        Logger.Info($"METAR fetch done. Inserted: {inserted}  Skipped: {skipped}  Parse errors: {parseErrors}");

        // ── Upsert any station ICAOs not yet in WxStations ───────────────────
        await UpsertNewStationsAsync(stations, dbOptions, httpClient);

        return new FetchResult(
            lines.Count,
            parsed
                .GroupBy(p => p.Entity.StationIcao, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Icao: g.Key, NewestObsUtc: g.Max(p => p.Entity.ObservationUtc)))
                .ToList());
    }

    /// <summary>Raw response size plus per-station newest-observation results for one fetch URL.</summary>
    private sealed record FetchResult(int LineCount, IReadOnlyList<(string Icao, DateTime NewestObsUtc)> Stations)
    {
        internal static readonly FetchResult Empty = new(0, []);
    }

    // WARN the first time a (station, message) parse failure is seen, DEBUG on
    // repeats — a structurally malformed feed (MYGF) fails identically forever
    // and one WARN carries all the information the next thousand would.
    private static void LogParseFailure(string message, string line)
    {
        // The dedupe key must be stable across cycles: only accept tokens[1]
        // as the station when it looks like one — for malformed lines whose
        // second token is a date/time group or COR marker, collapse to "?" so
        // the set stays bounded and repeats still dedupe (WX-140 review).
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var station = tokens.Length > 1
            && tokens[1].Length == 4
            && tokens[1].All(char.IsAsciiLetterOrDigit)
            && !tokens[1].All(char.IsAsciiDigit)
                ? tokens[1]
                : "?";
        bool firstSeen;
        lock (_seenParseFailuresLock)
        {
            firstSeen = _seenParseFailures.Add($"{station}|{message}");
        }

        var text = $"METAR parse error: {message} — input: {line}";
        if (firstSeen)
            Logger.Warn($"{text} (repeats of this station+error log at DEBUG)");
        else
            Logger.Debug(text);
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