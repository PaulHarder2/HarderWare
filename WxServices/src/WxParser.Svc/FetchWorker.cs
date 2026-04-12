using MetarParser.Data;
using WxServices.Common;
using WxServices.Logging;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;

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
        _config        = config;
        _dbOptions     = dbOptions;
        _fetchCycles   = _meter.CreateCounter<long>("wxparser.fetch.cycles.total", description: "Number of completed METAR/TAF fetch cycles.");
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
    private async Task FetchCycleAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var homeIcao = _config["Fetch:HomeIcao"];

            double? homeLat = double.TryParse(_config["Fetch:HomeLatitude"],  out var la) ? la : null;
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

            var region = FetchRegion.FromConfig(key => _config[key]);
            if (region is null)
            {
                Logger.Error("No fetch region could be resolved from configuration. Skipping fetch cycle.");
                return;
            }

            Logger.Info($"Starting fetch cycle for {homeIcao ?? "unknown"} (region {region.South:F1}–{region.North:F1}°N, {region.West:F1}–{region.East:F1}°E).");

            await MetarFetcher.FetchAndInsertAsync(region, _dbOptions, _http);

            // Fetch the home station explicitly in case it is omitted from the bounding box results.
            if (!string.IsNullOrWhiteSpace(homeIcao))
                await MetarFetcher.FetchAndInsertByStationAsync(homeIcao, _dbOptions, _http);

            // Fetch any additional stations flagged as unreliable in bbox results.
            await using (var db = new WeatherDataContext(_dbOptions))
            {
                var directIcaos = await db.WxStations
                    .Where(s => s.AlwaysFetchDirect == true && s.IcaoId != homeIcao)
                    .Select(s => s.IcaoId)
                    .ToListAsync();

                foreach (var icao in directIcaos)
                    await MetarFetcher.FetchAndInsertByStationAsync(icao, _dbOptions, _http);
            }

            await TafFetcher.FetchAndInsertAsync(region, _dbOptions, _http);

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
        try   { File.WriteAllText(path, DateTime.UtcNow.ToString("o")); }
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

        fetch["HomeLatitude"]  = lat;
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
            var tafRetention   = int.TryParse(_config["Fetch:TafRetentionDays"],   out var tr) ? tr : 14;

            await DataPurger.PurgeOldMetarsAsync(_dbOptions, metatRetention, ct);
            await DataPurger.PurgeOldTafsAsync(  _dbOptions, tafRetention,   ct);

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
