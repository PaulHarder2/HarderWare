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

        // Collapse same-key duplicates within this one response before touching
        // the DB. AWC re-serves byte-identical lines for some stations (KJXI
        // comes back ~3x every cycle); without this, the repeats each pass the
        // not-yet-in-DB check, both Add, and violate UX_Metars_Station_Time_Type
        // — and because SaveChanges is a single transaction, that one bad row
        // would roll back every co-batched station's inserts. A correction (COR)
        // wins over a non-COR for the same key, regardless of feed order (WX-210).
        var collapsed = CollapseByKey(parsed.Select(p => p.Entity));

        var stations = collapsed.Select(e => e.StationIcao).Distinct().ToList();
        var minTime = collapsed.Min(e => e.ObservationUtc);

        // Snapshot the rows already stored for these keys. Id + IsCorrection +
        // RawReport are all the reconcile rules need to decide insert / skip /
        // overwrite — a later-arriving COR must replace a stored uncorrected obs.
        var existing = ctx.Metars
            .Where(m => stations.Contains(m.StationIcao) && m.ObservationUtc >= minTime)
            .Select(m => new { m.Id, m.StationIcao, m.ObservationUtc, m.ReportType, m.IsCorrection, m.RawReport })
            .AsEnumerable()
            .ToDictionary(
                m => (m.StationIcao, m.ObservationUtc, m.ReportType),
                m => new PriorMetar(m.Id, m.IsCorrection, m.RawReport));

        var plan = Reconcile(collapsed, existing);
        int inserted = plan.Inserts.Count, corrected = plan.Overwrites.Count, skipped = plan.Skipped;

        if (inserted > 0 || corrected > 0)
        {
            try
            {
                ApplyPlan(ctx, plan);
                ctx.SaveChanges();
            }
            catch (DbUpdateException ex)
            {
                // The within-response collapse removes the known duplicate cause,
                // so a row-level fault here is unexpected — but it must not sink the
                // co-batched work. Detach the failed batch and re-apply each insert
                // AND each correction in its own context, so one bad row can't
                // discard the rest. The rolled-back SaveChanges leaves the entities'
                // store-generated keys unset (still default), so reusing the same
                // instances re-inserts cleanly — pinned by the
                // Fallback_BatchSaveFailsThenClearAndReuse regression test.
                Logger.Error($"Database error during METAR batch insert: {ex.InnerException?.Message ?? ex.Message}. Retrying row-by-row.");
                ctx.ChangeTracker.Clear();
                inserted = InsertPerRow(plan.Inserts, dbOptions);
                corrected = ApplyOverwritesPerRow(plan.Overwrites, dbOptions);
            }
        }

        Logger.Info($"METAR fetch done. Inserted: {inserted}  Corrected: {corrected}  Skipped: {skipped}  Parse errors: {parseErrors}");

        // ── Upsert any station ICAOs not yet in WxStations ───────────────────
        await UpsertNewStationsAsync(stations, dbOptions, httpClient);

        return new FetchResult(
            lines.Count,
            parsed
                .GroupBy(p => p.Entity.StationIcao, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Icao: g.Key, NewestObsUtc: g.Max(p => p.Entity.ObservationUtc)))
                .ToList());
    }

    /// <summary>Minimal snapshot of an already-stored METAR row — enough for the reconcile rules.</summary>
    internal readonly record struct PriorMetar(int Id, bool IsCorrection, string RawReport);

    /// <summary>The insert / overwrite / skip decisions for one fetch batch, reconciled against the DB.</summary>
    internal sealed record MetarReconcilePlan(
        IReadOnlyList<MetarRecord> Inserts,
        IReadOnlyList<(int ExistingId, MetarRecord Corrected)> Overwrites,
        int Skipped);

    /// <summary>Correction rank: a COR (corrected report) outranks a non-COR for the same key (WX-210).</summary>
    private static int Rank(bool isCorrection) => isCorrection ? 1 : 0;

    /// <summary>
    /// Collapses observations that share a <c>(station, observation-time, type)</c>
    /// key within a single fetch response down to one survivor. A correction
    /// (<c>COR</c>) beats a non-correction regardless of feed order; identical
    /// re-served copies (the KJXI case) collapse with no loss. Two genuinely
    /// different corrections for one key in the same response are the only
    /// ambiguous case (METAR carries no amendment sequence) — one is kept and the
    /// conflict is logged rather than silently picked.
    /// </summary>
    internal static IReadOnlyList<MetarRecord> CollapseByKey(IEnumerable<MetarRecord> parsed)
    {
        var survivors = new List<MetarRecord>();

        foreach (var group in parsed.GroupBy(e => (e.StationIcao, e.ObservationUtc, e.ReportType)))
        {
            var winner = group.First();
            foreach (var e in group)
            {
                if (Rank(e.IsCorrection) > Rank(winner.IsCorrection))
                    winner = e;
            }

            if (winner.IsCorrection)
            {
                var distinctCors = group
                    .Where(e => e.IsCorrection)
                    .Select(e => e.RawReport)
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                if (distinctCors > 1)
                    Logger.Warn($"Conflicting corrected (COR) reports for {winner.StationIcao} "
                        + $"{winner.ObservationUtc:u} {winner.ReportType} in one response; keeping one.");
            }

            survivors.Add(winner);
        }

        return survivors;
    }

    /// <summary>
    /// Decides, for each collapsed observation, whether to insert it, overwrite a
    /// stored row, or skip it — by comparing correction rank against what is
    /// already in the DB. A <c>COR</c> overwrites a stored non-<c>COR</c>; a stored
    /// <c>COR</c> is never clobbered by a later non-<c>COR</c>; an identical
    /// re-arrival is skipped; two differing <c>COR</c>s resolve to the later (this
    /// fetch) with a logged warning. Pure — no DB access — so the rules are unit-tested directly.
    /// </summary>
    internal static MetarReconcilePlan Reconcile(
        IEnumerable<MetarRecord> collapsed,
        IReadOnlyDictionary<(string StationIcao, DateTime ObservationUtc, string ReportType), PriorMetar> existing)
    {
        var inserts = new List<MetarRecord>();
        var overwrites = new List<(int ExistingId, MetarRecord Corrected)>();
        int skipped = 0;

        foreach (var e in collapsed)
        {
            var key = (e.StationIcao, e.ObservationUtc, e.ReportType);
            if (!existing.TryGetValue(key, out var prior))
            {
                inserts.Add(e);
                continue;
            }

            int newRank = Rank(e.IsCorrection);
            int priorRank = Rank(prior.IsCorrection);

            if (newRank > priorRank)
            {
                overwrites.Add((prior.Id, e));                  // stored non-COR <- incoming COR
            }
            else if (newRank == priorRank && newRank == 1
                     && !string.Equals(prior.RawReport, e.RawReport, StringComparison.Ordinal))
            {
                // Two differing corrections for one key across cycles: the later
                // arrival (this fetch) wins. Rare; log rather than silently pick.
                Logger.Warn($"Conflicting corrected (COR) reports for {e.StationIcao} {e.ObservationUtc:u} "
                    + $"{e.ReportType}; overwriting the stored correction with the newly fetched one.");
                overwrites.Add((prior.Id, e));
            }
            else if (newRank == priorRank && newRank == 0
                     && !string.Equals(prior.RawReport, e.RawReport, StringComparison.Ordinal))
            {
                // Same key, neither a correction, but the content differs — a data
                // conflict, not a benign re-send. Keep the stored row (no COR
                // authorizes a replacement) but surface it rather than silently skip.
                Logger.Warn($"Conflicting non-correction reports for {e.StationIcao} {e.ObservationUtc:u} "
                    + $"{e.ReportType}; keeping the stored report.");
                skipped++;
            }
            else
            {
                skipped++;                                      // identical re-arrival, or stored COR vs late non-COR
            }
        }

        return new MetarReconcilePlan(inserts, overwrites, skipped);
    }

    /// <summary>
    /// Applies a reconcile plan to <paramref name="ctx"/> without saving: queues the
    /// inserts, and overwrites each correction in place — replacing the cascade
    /// child rows and copying every scalar column via <c>SetValues</c> (so it stays
    /// correct as columns are added), preserving the existing PK and unique key.
    /// </summary>
    internal static void ApplyPlan(WeatherDataContext ctx, MetarReconcilePlan plan)
    {
        foreach (var e in plan.Inserts)
            ctx.Metars.Add(e);

        if (plan.Overwrites.Count == 0)
            return;

        var ids = plan.Overwrites.Select(o => o.ExistingId).ToHashSet();
        var tracked = ctx.Metars
            .Include(m => m.SkyConditions)
            .Include(m => m.WeatherPhenomena)
            .Include(m => m.RunwayVisualRanges)
            .Where(m => ids.Contains(m.Id))
            .ToDictionary(m => m.Id);

        foreach (var (existingId, corrected) in plan.Overwrites)
        {
            if (!tracked.TryGetValue(existingId, out var row))
            {
                // The stored row vanished between snapshot and load (a concurrent
                // cycle) — fall back to a plain insert of the correction.
                corrected.Id = 0;
                ctx.Metars.Add(corrected);
                continue;
            }

            // Defense-in-depth at the point of mutation: Reconcile already never
            // plans this, but the one transition we must never make is replacing a
            // stored correction with an uncorrected observation — that silently
            // destroys good data. Guard it here so the invariant holds even if a
            // future caller hands ApplyPlan a bad plan.
            if (row.IsCorrection && !corrected.IsCorrection)
            {
                Logger.Warn($"Refusing to overwrite a stored correction for {row.StationIcao} "
                    + $"{row.ObservationUtc:u} {row.ReportType} with a non-correction; keeping the correction.");
                continue;
            }

            ctx.RemoveRange(row.SkyConditions);
            ctx.RemoveRange(row.WeatherPhenomena);
            ctx.RemoveRange(row.RunwayVisualRanges);

            corrected.Id = row.Id;
            ctx.Entry(row).CurrentValues.SetValues(corrected);
            row.SkyConditions = corrected.SkyConditions;
            row.WeatherPhenomena = corrected.WeatherPhenomena;
            row.RunwayVisualRanges = corrected.RunwayVisualRanges;
        }
    }

    /// <summary>
    /// Inserts each record in its own context so one bad row cannot roll back the
    /// rest — the recovery path after a batch <see cref="DbUpdateException"/>.
    /// Returns the number that landed.
    /// </summary>
    internal static int InsertPerRow(IReadOnlyList<MetarRecord> inserts, DbContextOptions<WeatherDataContext> dbOptions)
    {
        int inserted = 0;

        foreach (var e in inserts)
        {
            try
            {
                using var ctx = new WeatherDataContext(dbOptions);
                ctx.Metars.Add(e);
                ctx.SaveChanges();
                inserted++;
            }
            catch (DbUpdateException ex)
            {
                Logger.Error($"METAR insert failed for {e.StationIcao} {e.ObservationUtc:u} {e.ReportType}: "
                    + $"{ex.InnerException?.Message ?? ex.Message}");
            }
        }

        return inserted;
    }

    /// <summary>
    /// Applies each correction in its own context so one bad overwrite cannot roll
    /// back the rest — the recovery path after a batch <see cref="DbUpdateException"/>
    /// that carried overwrites alongside inserts. Returns the number applied.
    /// </summary>
    internal static int ApplyOverwritesPerRow(
        IReadOnlyList<(int ExistingId, MetarRecord Corrected)> overwrites,
        DbContextOptions<WeatherDataContext> dbOptions)
    {
        int corrected = 0;

        foreach (var overwrite in overwrites)
        {
            try
            {
                using var ctx = new WeatherDataContext(dbOptions);
                ApplyPlan(ctx, new MetarReconcilePlan([], [overwrite], 0));
                ctx.SaveChanges();
                corrected++;
            }
            catch (DbUpdateException ex)
            {
                Logger.Error($"METAR correction failed for {overwrite.Corrected.StationIcao} "
                    + $"{overwrite.Corrected.ObservationUtc:u} {overwrite.Corrected.ReportType}: "
                    + $"{ex.InnerException?.Message ?? ex.Message}");
            }
        }

        return corrected;
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