using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;

using MetarParser.Data;

using Microsoft.EntityFrameworkCore;

using WxServices.Common;
using WxServices.Logging;

namespace WxParser.Svc;

/// <summary>
/// Background worker that periodically fetches METAR and TAF reports from the
/// Aviation Weather Center API and stores new records in the database.
/// </summary>
public sealed class FetchWorker : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly DbContextOptions<WeatherDataContext> _dbOptions;
    private readonly HttpClient _http = new();
    private DateTime _lastPurgeUtc = DateTime.MinValue;
    private DateTime _lastAirportRefreshUtc = DateTime.MinValue;

    private readonly Meter _meter = new("WxParser.Svc", "1.0.0");
    private readonly Counter<long> _fetchCycles;
    private readonly Histogram<double> _fetchDuration;

    /// <summary>Initializes a new instance of <see cref="FetchWorker"/> with the given dependencies.</summary>
    /// <param name="config">Application configuration used to read <c>Fetch:*</c> settings each cycle.</param>
    /// <param name="dbOptions">EF Core options for opening a <see cref="WeatherDataContext"/> during fetch cycles.</param>
    public FetchWorker(
        IConfiguration config,
        DbContextOptions<WeatherDataContext> dbOptions)
    {
        _config = config;
        _dbOptions = dbOptions;
        _fetchCycles = _meter.CreateCounter<long>("wxparser.fetch.cycles.total", description: "Number of completed METAR/TAF fetch cycles.");
        _fetchDuration = _meter.CreateHistogram<double>("wxparser.fetch.cycle.duration.seconds", unit: "s", description: "Duration of each METAR/TAF fetch cycle.");
    }

    /// <summary>
    /// Entry point called by the .NET hosted-service infrastructure.
    /// Runs <see cref="FetchCycleAsync"/> immediately on start, then sleeps for
    /// <c>Fetch:IntervalMinutes</c> between iterations until the host requests shutdown.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token signalled when the host is shutting down.</param>
    /// <sideeffects>Writes log entries on start, after each cycle, and on stop.</sideeffects>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.Info("FetchWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            await FetchCycleAsync(stoppingToken);
            await PurgeCycleAsync(stoppingToken);
            await AirportRefreshCycleAsync(stoppingToken);

            var intervalMinutes = int.TryParse(_config["Fetch:IntervalMinutes"], out var m) ? m : 10;
            if (intervalMinutes <= 0)
            {
                Logger.Warn($"Fetch:IntervalMinutes is {intervalMinutes} — must be > 0. Using 1 minute.");
                intervalMinutes = 1;
            }
            Logger.Info($"Next fetch in {intervalMinutes} minute(s).");

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Logger.Info("FetchWorker stopped.");
    }

    /// <summary>
    /// Executes one METAR/TAF fetch cycle: resolves home coordinates if not yet
    /// cached, then calls <see cref="MetarFetcher"/> and <see cref="TafFetcher"/>
    /// to download and store reports for the configured bounding box.
    /// Skips the cycle and logs an error if coordinates cannot be determined.
    /// </summary>
    /// <param name="cancellationToken">
    /// Token checked before waiting on delays; cycle is not interrupted mid-fetch.
    /// </param>
    /// <sideeffects>
    /// Inserts new METAR and TAF records into the database.
    /// May write resolved coordinates and home ICAO to <c>appsettings.local.json</c> on first run.
    /// Writes the heartbeat file on success.
    /// Makes HTTP calls to the Aviation Weather Center API.
    /// Writes log entries throughout.
    /// </sideeffects>
    // Config doubles parse invariant-culture (matching FetchRegion.FromConfig)
    // so "2.0" means 2.0 on any host culture (WX-140 review).
    private static double? ParseInvariant(string? value)
        => double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

    private async Task FetchCycleAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var homeIcao = _config["Fetch:HomeIcao"];

            double? homeLat = double.TryParse(_config["Fetch:HomeLatitude"], out var la) ? la : null;
            double? homeLon = double.TryParse(_config["Fetch:HomeLongitude"], out var lo) ? lo : null;

            if (homeLat is null || homeLon is null)
            {
                if (string.IsNullOrWhiteSpace(homeIcao))
                {
                    Logger.Error("Fetch:HomeLatitude/HomeLongitude not set and no HomeIcao to fall back on. Skipping fetch cycle.");
                    return;
                }

                Logger.Info($"Coordinates not configured. Looking up {homeIcao}...");
                var coords = await AirportLocator.LookupAsync(homeIcao, _http);
                if (coords is null)
                {
                    Logger.Error($"Airport lookup for '{homeIcao}' failed. Skipping fetch cycle.");
                    return;
                }
                (homeLat, homeLon) = coords.Value;
                Logger.Info($"Resolved {homeIcao}: lat={homeLat:F4}  lon={homeLon:F4}");
                SaveLocalCoordinates(homeLat.Value, homeLon.Value);
            }

            if (string.IsNullOrWhiteSpace(homeIcao))
            {
                Logger.Info("HomeIcao not configured. Finding nearest METAR station...");
                homeIcao = await AirportLocator.FindNearestStationAsync(homeLat.Value, homeLon.Value, _http);
                if (homeIcao is not null)
                {
                    Logger.Info($"Nearest METAR station: {homeIcao}");
                    SaveLocalHomeIcao(homeIcao);
                }
                else
                {
                    Logger.Warn("Could not determine nearest METAR station. Home station fetch will be skipped.");
                }
            }

            // WX-140: the AWC data API silently truncates an oversized
            // METAR/TAF bbox at roughly 550-600 reports — stations for any
            // locality outside the arbitrary surviving subset were never
            // ingested (the Watonga/KEND incident). Observation fetching keeps
            // the configured wide region (the WxVis synoptic analysis map is
            // METAR-driven over the full CONUS extent, so coverage must NOT
            // shrink to locality neighborhoods) and defeats the cap by
            // ADAPTIVE SPLITTING: any box whose response reaches the cap
            // threshold is split into quadrants and refetched, recursively, so
            // the region self-tiles to whatever density AWC's cap demands.
            // Per-locality boxes are appended for when the configured region
            // is small; the gap fill + AlwaysFetchDirect handle per-station
            // omissions. Fetch:ObsRegion* overrides Fetch:Region* (which the
            // GFS fetch also reads) when the two need to diverge.
            double pointBoxDegrees = ParseInvariant(_config["Fetch:LocalityBoxDegrees"]) ?? 2.0;
            double homeBoxDegrees = ParseInvariant(_config["Fetch:BoundingBoxDegrees"]) ?? 9.0;

            var obsRegion = FetchRegion.Resolve(
                regionSouth: ParseInvariant(_config["Fetch:ObsRegionSouth"]) ?? ParseInvariant(_config["Fetch:RegionSouth"]),
                regionNorth: ParseInvariant(_config["Fetch:ObsRegionNorth"]) ?? ParseInvariant(_config["Fetch:RegionNorth"]),
                regionWest: ParseInvariant(_config["Fetch:ObsRegionWest"]) ?? ParseInvariant(_config["Fetch:RegionWest"]),
                regionEast: ParseInvariant(_config["Fetch:ObsRegionEast"]) ?? ParseInvariant(_config["Fetch:RegionEast"]),
                homeLat: homeLat, homeLon: homeLon,
                boxDegrees: homeBoxDegrees);

            var targets = await ObsFetchTargets.LoadAsync(_dbOptions, cancellationToken);

            List<string> directIcaos;
            await using (var db = new WeatherDataContext(_dbOptions))
            {
                // Stations flagged as unreliable in bbox results (METAR direct fetch).
                directIcaos = await db.WxStations
                    .Where(s => s.AlwaysFetchDirect == true && s.IcaoId != homeIcao)
                    .Select(s => s.IcaoId)
                    .ToListAsync(cancellationToken);
            }

            var seedBoxes = new List<FetchRegion>();
            if (obsRegion is not null) seedBoxes.Add(obsRegion);
            foreach (var box in ObsFetchPlanner.Plan(homeLat, homeLon, homeBoxDegrees, targets.Points, pointBoxDegrees))
                if (!seedBoxes.Any(existing => existing.Contains(box)))
                    seedBoxes.Add(box);

            if (seedBoxes.Count == 0)
            {
                Logger.Error("No observation fetch geometry could be resolved (no region config, home coordinates, localities, or located recipients). Skipping fetch cycle.");
                return;
            }

            Logger.Info($"Starting fetch cycle for {homeIcao ?? "unknown"}: {seedBoxes.Count} seed box(es), "
                + $"{directIcaos.Count} direct METAR station(s), {targets.TafIcaos.Count} direct TAF station(s).");

            // Adaptive split: a cap-sized response means silent truncation —
            // split into quadrants and refetch until every tile is under the
            // cap (bounded depth; a depth-4 split of CONUS is a 256-tile
            // worst case that real station density never approaches).
            static async Task FetchSplitAsync(FetchRegion box, Func<FetchRegion, Task<int>> fetch, int depth)
            {
                int count = await fetch(box);
                if (count < MetarFetcher.AwcCapSuspicionThreshold || depth >= 4)
                    return;
                Logger.Info($"Adaptive split: box {box.ToAwcBbox()} returned {count} report(s) (>= cap threshold) — splitting into quadrants.");
                double midLat = (box.South + box.North) / 2;
                double midLon = (box.West + box.East) / 2;
                foreach (var quadrant in new[]
                {
                    new FetchRegion(box.South, midLat, box.West, midLon),
                    new FetchRegion(box.South, midLat, midLon, box.East),
                    new FetchRegion(midLat, box.North, box.West, midLon),
                    new FetchRegion(midLat, box.North, midLon, box.East),
                })
                    await FetchSplitAsync(quadrant, fetch, depth + 1);
            }

            foreach (var box in seedBoxes)
                await FetchSplitAsync(box, b => MetarFetcher.FetchAndInsertAsync(b, _dbOptions, _http), 0);

            // Fetch the home station explicitly in case it is omitted from the bounding box results.
            if (!string.IsNullOrWhiteSpace(homeIcao))
                await MetarFetcher.FetchAndInsertByStationAsync(homeIcao, _dbOptions, _http);

            // Direct list, batched (ids= takes comma lists); auto-promotion
            // grows this list, so per-station calls would grow without bound.
            await MetarFetcher.FetchAndInsertByStationsAsync(directIcaos, _dbOptions, _http);

            // WX-140 gap fill: AWC omits individual stations from bbox results
            // even when the box is small (the long-standing reason
            // AlwaysFetchDirect exists). Every defined station near a locality
            // centroid or locality-less recipient — plus every explicitly
            // named station — is freshness-checked; gaps get a batched direct
            // fetch and evidence-confirmed bbox-unreliable stations are
            // promoted to AlwaysFetchDirect automatically.
            await StationGapFiller.FillAsync(targets.Points, targets.MetarIcaos, homeIcao, _dbOptions, _http, cancellationToken);

            foreach (var box in seedBoxes)
                await FetchSplitAsync(box, b => TafFetcher.FetchAndInsertAsync(b, _dbOptions, _http), 0);

            // TAF stations named by localities/recipients — the by-station
            // rescue path TAFs never had — batched.
            await TafFetcher.FetchAndInsertByStationsAsync(targets.TafIcaos, _dbOptions, _http);

            Logger.Info("Fetch cycle complete.");
            WriteHeartbeat(_config["Fetch:HeartbeatFile"]
                ?? new WxPaths(_config["InstallRoot"]).HeartbeatFile("wxparser"));
            _fetchCycles.Add(1);
            _fetchDuration.Record(sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Logger.Error("Unhandled exception in fetch cycle.", ex);
        }
    }

    /// <summary>
    /// Writes the current UTC timestamp to the heartbeat file so that WxMonitor
    /// can confirm this service is still running.  Does nothing if
    /// <paramref name="path"/> is null or whitespace.
    /// </summary>
    /// <param name="path">Absolute path to the heartbeat file, or <see langword="null"/> to skip.</param>
    /// <sideeffects>Creates or overwrites the file at <paramref name="path"/> with an ISO 8601 UTC timestamp.</sideeffects>
    private static void WriteHeartbeat(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.WriteAllText(path, DateTime.UtcNow.ToString("o")); }
        catch (Exception ex) { Logger.Warn($"Could not write heartbeat to '{path}': {ex.Message}"); }
    }

    /// <summary>
    /// Persists the resolved home station ICAO to <c>appsettings.local.json</c> in
    /// the install root so subsequent restarts skip the nearest-station API call.
    /// </summary>
    private void SaveLocalHomeIcao(string homeIcao)
    {
        var path = new WxPaths(_config["InstallRoot"]).LocalConfigPath;
        JsonNode root = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path)) ?? new JsonObject()
            : new JsonObject();

        if (root["Fetch"] is not JsonObject fetch)
        {
            fetch = new JsonObject();
            root["Fetch"] = fetch;
        }

        fetch["HomeIcao"] = homeIcao;

        File.WriteAllText(path, root.ToJsonString(
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        Logger.Info($"HomeIcao '{homeIcao}' saved to local settings.");
    }

    /// <summary>
    /// Persists resolved home coordinates to <c>appsettings.local.json</c> in
    /// the install root so subsequent restarts skip the airport lookup API call.
    /// </summary>
    private void SaveLocalCoordinates(double lat, double lon)
    {
        var path = new WxPaths(_config["InstallRoot"]).LocalConfigPath;

        JsonNode root = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path)) ?? new JsonObject()
            : new JsonObject();

        if (root["Fetch"] is not JsonObject fetch)
        {
            fetch = new JsonObject();
            root["Fetch"] = fetch;
        }

        fetch["HomeLatitude"] = lat;
        fetch["HomeLongitude"] = lon;

        File.WriteAllText(path, root.ToJsonString(
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        Logger.Info($"Coordinates saved to {path}");
    }

    /// <summary>
    /// Runs stale-data purge once per day.  Reads retention periods from
    /// <c>Fetch:MetarRetentionDays</c> and <c>Fetch:TafRetentionDays</c>;
    /// defaults to 14 days each if not configured.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <sideeffects>Deletes old METAR and TAF records from the database. Writes log entries.</sideeffects>
    private async Task PurgeCycleAsync(CancellationToken ct)
    {
        if ((DateTime.UtcNow - _lastPurgeUtc).TotalHours < 24) return;

        try
        {
            var metatRetention = int.TryParse(_config["Fetch:MetarRetentionDays"], out var mr) ? mr : 14;
            var tafRetention = int.TryParse(_config["Fetch:TafRetentionDays"], out var tr) ? tr : 14;

            await DataPurger.PurgeOldMetarsAsync(_dbOptions, metatRetention, ct);
            await DataPurger.PurgeOldTafsAsync(_dbOptions, tafRetention, ct);

            _lastPurgeUtc = DateTime.UtcNow;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Logger.Error("Unhandled exception in purge cycle.", ex);
        }
    }

    /// <summary>
    /// Refreshes WxStation metadata from OurAirports once per week (and on first
    /// startup).  Errors are logged but do not affect the METAR/TAF fetch cycle.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    private async Task AirportRefreshCycleAsync(CancellationToken ct)
    {
        if ((DateTime.UtcNow - _lastAirportRefreshUtc).TotalDays < 7) return;

        try
        {
            Logger.Info("AirportRefreshCycle: refreshing WxStation metadata from OurAirports...");
            await AirportDataImporter.RefreshAsync(_dbOptions, _http, ct);
            _lastAirportRefreshUtc = DateTime.UtcNow;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Logger.Error("AirportRefreshCycle: unhandled exception.", ex);
        }
    }

    public override void Dispose()
    {
        _http.Dispose();
        _meter.Dispose();
        base.Dispose();
    }
}