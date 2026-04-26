using System.Diagnostics;
using System.Diagnostics.Metrics;

using MetarParser.Data;

using Microsoft.EntityFrameworkCore;

using WxServices.Common;
using WxServices.Logging;

namespace WxVis.Svc;

/// <summary>
/// Background worker that renders GFS forecast maps as each forecast hour's data
/// becomes available in the database.
/// </summary>
/// <remarks>
/// Polls <c>GfsGrid</c> every <c>WxVis:ForecastPollIntervalSeconds</c> seconds.
/// For the most recent model run, any forecast hour that has grid data but no
/// corresponding PNG file (or whose PNG predates the model run) is rendered.
/// Rendered hours are tracked in memory; on restart the output directory is
/// scanned to avoid re-rendering files that are already current.
/// </remarks>
public sealed class ForecastMapWorker : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly DbContextOptions<WeatherDataContext> _dbOptions;
    private Dictionary<string, string> _pythonEnv = new();

    // Key: model run UTC.  Value: set of forecast hours already rendered for that run.
    private readonly Dictionary<DateTime, HashSet<int>> _rendered = new();

    private static readonly Meter _meter = new("WxVis.Svc", "1.0.0");
    private static readonly Counter<long> _forecastRenders = _meter.CreateCounter<long>("wxvis.forecast.renders.total", description: "Number of completed forecast frame renders.");
    private static readonly Counter<long> _forecastFailures = _meter.CreateCounter<long>("wxvis.forecast.failures.total", description: "Number of failed forecast frame renders.");

    /// <summary>Initialises a new instance with the application configuration and DB options.</summary>
    /// <param name="config">Application configuration used to read <c>WxVis:*</c> settings each cycle.</param>
    /// <param name="dbOptions">EF Core options for opening a <see cref="WeatherDataContext"/>.</param>
    public ForecastMapWorker(
        IConfiguration config,
        DbContextOptions<WeatherDataContext> dbOptions)
    {
        _config = config;
        _dbOptions = dbOptions;
    }

    /// <summary>
    /// Polls the database for new GFS forecast hours and renders any that do not
    /// yet have a current PNG file.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token signalled when the host shuts down.</param>
    /// <sideeffects>Shells out to Python via <see cref="MapRenderer.RunAsync"/>. Writes log entries.</sideeffects>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.Info("ForecastMapWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cfg = LoadConfig();
                await RenderPendingAsync(cfg, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                Logger.Error("ForecastMapWorker: unhandled exception.", ex);
            }

            var cfg2 = LoadConfig();
            try { await Task.Delay(TimeSpan.FromSeconds(cfg2.ForecastPollIntervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        Logger.Info("ForecastMapWorker stopped.");
    }

    /// <summary>
    /// Queries the database for the most recent model run, determines which
    /// forecast hours still need rendering, and renders them one by one.
    /// </summary>
    /// <param name="cfg">Current <see cref="WxVisConfig"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task RenderPendingAsync(WxVisConfig cfg, CancellationToken ct)
    {
        DateTime latestRun;
        List<int> availableHours;

        using (var ctx = new WeatherDataContext(_dbOptions))
        {
            // Find the most recent model run (complete or still ingesting).
            var runUtc = await ctx.GfsModelRuns
                .OrderByDescending(r => r.ModelRunUtc)
                .Select(r => r.ModelRunUtc)
                .FirstOrDefaultAsync(ct);

            if (runUtc == default) return;
            latestRun = runUtc;

            // Find which forecast hours are available for that run.
            availableHours = await ctx.GfsGrid
                .Where(g => g.ModelRunUtc == latestRun)
                .Select(g => g.ForecastHour)
                .Distinct()
                .OrderBy(fh => fh)
                .ToListAsync(ct);
        }

        if (availableHours.Count == 0) return;

        // If this is a run we haven't seen before, initialise tracking and
        // pre-populate from existing PNG files so we don't re-render current maps.
        if (!_rendered.TryGetValue(latestRun, out var rendered))
        {
            rendered = new HashSet<int>();
            _rendered[latestRun] = rendered;

            foreach (var fh in availableHours)
            {
                var png = PngPath(cfg.OutputDir, latestRun, fh);
                if (File.Exists(png) && File.GetLastWriteTimeUtc(png) > latestRun)
                    rendered.Add(fh);
            }

            // Remove tracking state for any older runs to keep memory tidy.
            foreach (var oldRun in _rendered.Keys.Where(r => r < latestRun).ToList())
                _rendered.Remove(oldRun);

            Logger.Info($"ForecastMapWorker: tracking run {latestRun:yyyy-MM-dd HH}Z — " +
                        $"{rendered.Count}/{availableHours.Count} hour(s) already rendered.");
        }

        var pending = availableHours.Where(fh => !rendered.Contains(fh)).ToList();
        if (pending.Count == 0) return;

        Logger.Info($"ForecastMapWorker: {pending.Count} forecast hour(s) to render for run {latestRun:yyyy-MM-dd HH}Z.");

        var extentSuffix = string.IsNullOrEmpty(cfg.MapExtent) ? "" : $" --extent {cfg.MapExtent}";

        foreach (var fh in pending)
        {
            if (ct.IsCancellationRequested) break;

            Logger.Info($"ForecastMapWorker: rendering f{fh:D3} ({cfg.ZoomLevels} zoom level(s))...");
            var allOk = true;
            for (int z = 1; z <= cfg.ZoomLevels; z++)
            {
                var ok = await MapRenderer.RunAsync(
                    cfg.CondaPythonExe, cfg.ScriptDir,
                    "forecast_map.py", $"--fh {fh} --run {latestRun:yyyyMMdd_HH} --zoom-level {z}{extentSuffix}",
                    ct,
                    _pythonEnv);

                if (!ok) { allOk = false; break; }
            }

            if (allOk)
            {
                _forecastRenders.Add(1);
                rendered.Add(fh);
            }
            else
            {
                _forecastFailures.Add(1);
                Logger.Error($"ForecastMapWorker: render failed for f{fh:D3} — will retry next poll.");
            }
        }
    }

    /// <summary>Returns the expected z1 PNG output path for a given model run and forecast hour.</summary>
    private static string PngPath(string outputDir, DateTime modelRun, int forecastHour) =>
        Path.Combine(outputDir, $"forecast_{modelRun:yyyyMMdd_HH}_f{forecastHour:D3}_z1.png");

    /// <summary>
    /// Loads and returns the current <see cref="WxVisConfig"/> from the
    /// <c>WxVis</c> configuration section.
    /// </summary>
    private WxVisConfig LoadConfig()
    {
        var cfg = new WxVisConfig();
        _config.GetSection("WxVis").Bind(cfg);
        var paths = new WxPaths(_config["InstallRoot"]);
        cfg.ApplyPaths(paths);
        _pythonEnv = cfg.BuildPythonEnv(
            _config.GetConnectionString("WeatherData") ?? "", paths.LogsDir);
        return cfg;
    }
}