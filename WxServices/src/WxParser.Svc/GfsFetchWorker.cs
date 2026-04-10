using MetarParser.Data;
using Microsoft.EntityFrameworkCore;
using WxServices.Logging;

namespace WxParser.Svc;

/// <summary>
/// Background worker that periodically downloads GFS model-forecast data from
/// NOAA NOMADS, extracts the configured variables for the bounding box defined
/// in the <c>Fetch</c> config section, and stores the results in the
/// <c>GfsGrid</c> database table.
/// </summary>
/// <remarks>
/// Runs concurrently with <see cref="FetchWorker"/> inside the same Windows service.
/// The two workers are independent: a failure in one does not affect the other.
/// </remarks>
public sealed class GfsFetchWorker : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly DbContextOptions<WeatherDataContext> _dbOptions;
    private readonly HttpClient _http = new() { DefaultRequestHeaders = { { "User-Agent", "GfsFetcher/1.0 (WxServices)" } } };

    /// <summary>
    /// Initialises a new instance of <see cref="GfsFetchWorker"/> with the given dependencies.
    /// </summary>
    /// <param name="config">Application configuration used to read <c>Fetch:*</c> and <c>Gfs:*</c> settings each cycle.</param>
    /// <param name="dbOptions">EF Core options for opening a <see cref="WeatherDataContext"/> during fetch cycles.</param>
    public GfsFetchWorker(
        IConfiguration config,
        DbContextOptions<WeatherDataContext> dbOptions)
    {
        _config    = config;
        _dbOptions = dbOptions;
    }

    /// <summary>
    /// Entry point called by the .NET hosted-service infrastructure.
    /// Runs <see cref="GfsCycleAsync"/> immediately on start, then sleeps for
    /// <c>Gfs:IntervalMinutes</c> between iterations until the host requests shutdown.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token signalled when the host is shutting down.</param>
    /// <sideeffects>Writes log entries on start, after each cycle, and on stop.</sideeffects>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.Info("GfsFetchWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            await GfsCycleAsync(stoppingToken);

            var cfg = LoadConfig();
            var intervalMinutes = cfg.IntervalMinutes;
            if (intervalMinutes <= 0)
            {
                Logger.Warn($"Gfs:IntervalMinutes is {intervalMinutes} — must be > 0. Using 60 minutes.");
                intervalMinutes = 60;
            }

            Logger.Info($"Next GFS fetch check in {intervalMinutes} minute(s).");
            try { await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        Logger.Info("GfsFetchWorker stopped.");
    }

    /// <summary>
    /// Executes one GFS fetch cycle by delegating to <see cref="GfsFetcher.FetchAndInsertAsync"/>.
    /// Reads bounding-box coordinates from the <c>Fetch</c> config section and
    /// GFS operational parameters from the <c>Gfs</c> config section.
    /// Skips the cycle and logs an error if home coordinates are not configured.
    /// </summary>
    /// <param name="ct">
    /// Token checked before waiting on delays; the cycle is not interrupted mid-fetch.
    /// </param>
    /// <sideeffects>
    /// Calls <see cref="GfsFetcher.FetchAndInsertAsync"/> which makes HTTP requests
    /// to NOMADS, invokes wgrib2 subprocesses, and inserts/deletes database rows.
    /// Writes log entries throughout.
    /// </sideeffects>
    private async Task GfsCycleAsync(CancellationToken ct)
    {
        try
        {
            if (!double.TryParse(_config["Fetch:HomeLatitude"],  out var homeLat) ||
                !double.TryParse(_config["Fetch:HomeLongitude"], out var homeLon))
            {
                Logger.Warn("GfsFetchWorker: Fetch:HomeLatitude/HomeLongitude not set — skipping GFS cycle.");
                return;
            }

            var boxDeg = double.TryParse(_config["Fetch:BoundingBoxDegrees"], out var bd) ? bd : 5.0;
            var cfg    = LoadConfig();

            await GfsFetcher.FetchAndInsertAsync(
                homeLat, homeLon, boxDeg,
                _dbOptions,
                _http,
                cfg.Wgrib2WslPath,
                cfg.MaxForecastHours,
                cfg.RetainModelRuns,
                cfg.TempPath,
                cfg.DelayHours,
                ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Logger.Error("GfsFetchWorker: unhandled exception in GFS fetch cycle.", ex);
        }
    }

    /// <summary>
    /// Loads and returns the current <see cref="GfsConfig"/> by binding the
    /// <c>Gfs</c> configuration section.  Called at the start of each interval
    /// so that config changes take effect without restarting the service.
    /// </summary>
    /// <returns>A freshly bound <see cref="GfsConfig"/> instance.</returns>
    private GfsConfig LoadConfig()
    {
        var cfg = new GfsConfig();
        _config.GetSection("Gfs").Bind(cfg);
        return cfg;
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
