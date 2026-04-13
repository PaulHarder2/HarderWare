using System.Diagnostics;
using System.Diagnostics.Metrics;
using WxServices.Common;
using WxServices.Logging;

namespace WxVis.Svc;

/// <summary>
/// Background worker that generates a synoptic analysis map once per UTC hour,
/// triggered at a configurable number of minutes past the hour.
/// </summary>
/// <remarks>
/// METAR stations issue routine observations at :53–:58 of each hour.
/// <see cref="FetchWorker"/> (in WxParser.Svc) ingests them within its next
/// 10-minute cycle.  Triggering this worker at <c>WxVis:AnalysisMapMinutePastHour</c>
/// (default 10) ensures the hourly observations are in the database before rendering.
/// </remarks>
public sealed class AnalysisMapWorker : BackgroundService
{
    private readonly IConfiguration _config;
    private Dictionary<string, string> _pythonEnv = new();
    private DateTime _lastCleanupUtc = DateTime.MinValue;

    private static readonly Meter _meter = new("WxVis.Svc", "1.0.0");
    private static readonly Counter<long> _analysisRenders = _meter.CreateCounter<long>("wxvis.analysis.renders.total", description: "Number of completed analysis map renders.");
    private static readonly Counter<long> _analysisFailures = _meter.CreateCounter<long>("wxvis.analysis.failures.total", description: "Number of failed analysis map renders.");
    private static readonly Histogram<double> _analysisRenderDuration = _meter.CreateHistogram<double>("wxvis.render.duration.seconds", unit: "s", description: "Duration of each map render.");

    /// <summary>Initialises a new instance with the application configuration.</summary>
    /// <param name="config">Application configuration used to read <c>WxVis:*</c> settings each cycle.</param>
    public AnalysisMapWorker(IConfiguration config)
    {
        _config = config;
    }

    /// <summary>
    /// Wakes up every minute and renders the synoptic analysis map once per UTC hour
    /// as soon as the configured trigger minute is reached.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token signalled when the host shuts down.</param>
    /// <sideeffects>Shells out to Python via <see cref="MapRenderer.RunAsync"/>. Writes log entries.</sideeffects>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.Info("AnalysisMapWorker started.");

        var cfg             = LoadConfig();
        int? lastRenderedHour = null;

        // On startup: if the trigger minute has already passed this hour and a
        // timestamped synoptic map for this UTC hour already exists, skip the first render.
        var nowUtc = DateTime.UtcNow;
        if (nowUtc.Minute >= cfg.AnalysisMapMinutePastHour)
        {
            var hourStart  = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, 0, 0, DateTimeKind.Utc);
            var hourTag    = nowUtc.ToString("yyyyMMdd_HH");
            var alreadyDone = Directory.EnumerateFiles(cfg.OutputDir, $"synoptic_*_{hourTag}_z1.png").Any();
            if (alreadyDone)
            {
                lastRenderedHour = nowUtc.Hour;
                Logger.Info($"AnalysisMapWorker: synoptic map already current for {nowUtc:yyyy-MM-dd HH}Z — skipping first render.");
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            cfg    = LoadConfig();
            nowUtc = DateTime.UtcNow;

            if (nowUtc.Minute >= cfg.AnalysisMapMinutePastHour && lastRenderedHour != nowUtc.Hour)
            {
                Logger.Info($"AnalysisMapWorker: rendering synoptic analysis map for {nowUtc:yyyy-MM-dd HH}Z ({cfg.ZoomLevels} zoom level(s)).");

                var renderSw = Stopwatch.StartNew();
                var extentArg = string.IsNullOrEmpty(cfg.MapExtent) ? "" : $"--extent {cfg.MapExtent}";
                var allOk = true;
                for (int z = 1; z <= cfg.ZoomLevels; z++)
                {
                    var ok = await MapRenderer.RunAsync(
                        cfg.CondaPythonExe,
                        cfg.ScriptDir,
                        "synoptic_map.py",
                        $"{extentArg} --zoom-level {z}",
                        stoppingToken,
                        _pythonEnv);

                    if (!ok) { allOk = false; break; }
                }

                _analysisRenderDuration.Record(renderSw.Elapsed.TotalSeconds, new KeyValuePair<string, object?>("map_type", "analysis"));
                if (allOk)
                {
                    _analysisRenders.Add(1);
                    lastRenderedHour = nowUtc.Hour;
                    Logger.Info("AnalysisMapWorker: synoptic analysis map rendered successfully.");
                }
                else
                {
                    _analysisFailures.Add(1);
                    Logger.Error("AnalysisMapWorker: synoptic map render failed — will retry next minute.");
                }
            }

            PurgeStalePlots(cfg);

            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        Logger.Info("AnalysisMapWorker stopped.");
    }

    /// <summary>
    /// Deletes PNG files in <see cref="WxVisConfig.OutputDir"/> older than
    /// <see cref="WxVisConfig.PlotRetentionDays"/> days.  Runs at most once per 24 hours.
    /// </summary>
    /// <param name="cfg">Current configuration.</param>
    /// <sideeffects>Deletes files from <see cref="WxVisConfig.OutputDir"/>. Writes log entries.</sideeffects>
    private void PurgeStalePlots(WxVisConfig cfg)
    {
        if ((DateTime.UtcNow - _lastCleanupUtc).TotalHours < 24) return;

        try
        {
            var cutoff  = DateTime.UtcNow - TimeSpan.FromDays(cfg.PlotRetentionDays);
            int deleted = 0;

            foreach (var pattern in new[] { "*.png", "*.json" })
            {
                foreach (var file in Directory.EnumerateFiles(cfg.OutputDir, pattern))
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                        deleted++;
                    }
                }
            }

            if (deleted > 0)
                Logger.Info($"AnalysisMapWorker: deleted {deleted} file(s) older than {cfg.PlotRetentionDays} days.");

            _lastCleanupUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Logger.Error("AnalysisMapWorker: error during plot cleanup.", ex);
        }
    }

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
