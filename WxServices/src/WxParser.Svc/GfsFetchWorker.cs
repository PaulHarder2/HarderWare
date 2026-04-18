using System.Diagnostics;
using System.Diagnostics.Metrics;
using MetarParser.Data;
using Microsoft.EntityFrameworkCore;
using WxServices.Common;
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

    private readonly Meter _meter = new("WxParser.Svc.Gfs", "1.0.0");
    private readonly Counter<long> _gfsCycles;
    private readonly Counter<long> _gfsFailures;
    private readonly Histogram<double> _gfsDuration;

    /// <summary>
    /// Initialises a new instance of <see cref="GfsFetchWorker"/> with the given dependencies.
    /// </summary>
    /// <param name="config">Application configuration used to read <c>Fetch:*</c> and <c>Gfs:*</c> settings each cycle.</param>
    /// <param name="dbOptions">EF Core options for opening a <see cref="WeatherDataContext"/> during fetch cycles.</param>
    public GfsFetchWorker(
        IConfiguration config,
        DbContextOptions<WeatherDataContext> dbOptions)
    {
        _config      = config;
        _dbOptions   = dbOptions;
        _gfsCycles   = _meter.CreateCounter<long>("wxparser.gfs.cycles.total", description: "Number of completed GFS fetch cycles.");
        _gfsFailures = _meter.CreateCounter<long>("wxparser.gfs.failures.total", description: "Number of failed GFS fetch cycles.");
        _gfsDuration = _meter.CreateHistogram<double>("wxparser.gfs.cycle.duration.seconds", unit: "s", description: "Duration of each GFS fetch cycle.");
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
        var sw = Stopwatch.StartNew();
        try
        {
            var region = FetchRegion.FromConfig(key => _config[key]);
            if (region is null)
            {
                Logger.Warn("GfsFetchWorker: no fetch region configured — skipping GFS cycle.");
                return;
            }

            var cfg   = LoadConfig();
            var paths = new WxPaths(_config["InstallRoot"]);

            var tempPath = string.IsNullOrEmpty(cfg.TempPath)
                ? paths.TempDir
                : cfg.TempPath;

            var wgrib2Path = string.IsNullOrWhiteSpace(cfg.Wgrib2Path)
                ? paths.Wgrib2DefaultPath
                : cfg.Wgrib2Path;

            await GfsFetcher.FetchAndInsertAsync(
                region,
                _dbOptions,
                _http,
                wgrib2Path,
                tempPath,
                cfg.MaxForecastHours,
                cfg.RetainModelRuns,
                cfg.DelayHours,
                ct);

            _gfsCycles.Add(1);
            _gfsDuration.Record(sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _gfsFailures.Add(1);
            _gfsDuration.Record(sw.Elapsed.TotalSeconds);
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
        _meter.Dispose();
        base.Dispose();
    }
}
