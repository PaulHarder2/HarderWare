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

        // On startup: if the trigger minute has already passed this hour and the
        // synoptic map was written after the start of this UTC hour, treat it as
        // already rendered so we don't re-render unnecessarily.
        var nowUtc = DateTime.UtcNow;
        if (nowUtc.Minute >= cfg.AnalysisMapMinutePastHour)
        {
            var synopticPath = Path.Combine(cfg.OutputDir, "synoptic_south_central.png");
            var hourStart    = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, 0, 0, DateTimeKind.Utc);
            if (File.Exists(synopticPath) && File.GetLastWriteTimeUtc(synopticPath) >= hourStart)
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
                Logger.Info($"AnalysisMapWorker: rendering synoptic analysis map for {nowUtc:yyyy-MM-dd HH}Z.");

                var ok = await MapRenderer.RunAsync(
                    cfg.CondaPythonExe,
                    cfg.ScriptDir,
                    "synoptic_map.py",
                    cfg.SynopticMapArgs,
                    stoppingToken);

                if (ok)
                {
                    lastRenderedHour = nowUtc.Hour;
                    Logger.Info("AnalysisMapWorker: synoptic analysis map rendered successfully.");
                }
                else
                {
                    Logger.Error("AnalysisMapWorker: synoptic map render failed — will retry next minute.");
                }
            }

            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        Logger.Info("AnalysisMapWorker stopped.");
    }

    /// <summary>
    /// Loads and returns the current <see cref="WxVisConfig"/> from the
    /// <c>WxVis</c> configuration section.
    /// </summary>
    private WxVisConfig LoadConfig()
    {
        var cfg = new WxVisConfig();
        _config.GetSection("WxVis").Bind(cfg);
        return cfg;
    }
}
