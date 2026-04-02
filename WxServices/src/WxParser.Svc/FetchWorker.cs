using MetarParser.Data;
using WxServices.Logging;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>Initializes a new instance of <see cref="FetchWorker"/> with the given dependencies.</summary>
    /// <param name="config">Application configuration used to read <c>Fetch:*</c> settings each cycle.</param>
    /// <param name="dbOptions">EF Core options for opening a <see cref="WeatherDataContext"/> during fetch cycles.</param>
    public FetchWorker(
        IConfiguration config,
        DbContextOptions<WeatherDataContext> dbOptions)
    {
        _config    = config;
        _dbOptions = dbOptions;
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
        try
        {
            var homeIcao = _config["Fetch:HomeIcao"];
            var boxDeg   = double.TryParse(_config["Fetch:BoundingBoxDegrees"], out var bd) ? bd : 5.0;

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

            Logger.Info($"Starting fetch cycle for {homeIcao ?? "unknown"} (bbox ±{boxDeg}°).");

            await MetarFetcher.FetchAndInsertAsync(homeLat.Value, homeLon.Value, boxDeg, _dbOptions, _http);
            // Fetch the home station explicitly in case it is omitted from the bounding box results.
            if (!string.IsNullOrWhiteSpace(homeIcao))
                await MetarFetcher.FetchAndInsertByStationAsync(homeIcao, _dbOptions, _http);
            await TafFetcher.FetchAndInsertAsync(homeLat.Value, homeLon.Value, boxDeg, _dbOptions, _http);

            Logger.Info("Fetch cycle complete.");
            WriteHeartbeat(_config["Fetch:HeartbeatFile"]);
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
    /// Persists the resolved home station ICAO to <c>appsettings.local.json</c> so
    /// subsequent service restarts do not need to call the nearest-station API again.
    /// </summary>
    /// <param name="homeIcao">The ICAO identifier to save (e.g. <c>"KDWH"</c>).</param>
    /// <sideeffects>Creates or updates the <c>Fetch.HomeIcao</c> key in <c>appsettings.local.json</c> alongside the executable.</sideeffects>
    private void SaveLocalHomeIcao(string homeIcao)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.local.json");
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
    /// Persists resolved home coordinates to <c>appsettings.local.json</c> so
    /// subsequent service restarts do not need to call the airport lookup API again.
    /// </summary>
    /// <param name="lat">Resolved latitude to cache.</param>
    /// <param name="lon">Resolved longitude to cache.</param>
    /// <sideeffects>Creates or updates the <c>Fetch.HomeLatitude</c> and <c>Fetch.HomeLongitude</c> keys in <c>appsettings.local.json</c> alongside the executable.</sideeffects>
    private void SaveLocalCoordinates(double lat, double lon)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.local.json");

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

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
