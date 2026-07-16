using System.Diagnostics.Metrics;

using MetarParser.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

using WxMonitor.Svc.Watchers;

using WxServices.Common;
using WxServices.Logging;

namespace WxMonitor.Svc;

/// <summary>
/// Background service that periodically runs the WxMonitor watcher family — one scheduling loop
/// that builds a per-cycle <see cref="WatcherContext"/>, runs each <see cref="IWatcher"/>
/// (log-scan, heartbeat, METAR-staleness, report-error, grafana-dashboards), and routes the findings
/// each produces to their sink(s).
/// Today every finding is delivered by email via <see cref="EmailSink"/>, which rate-limits repeats
/// per category via <see cref="MonitorConfig.AlertCooldownMinutes"/>. The watchers detect and
/// advance their own watermarks; this class owns only scheduling, config/secret loading, routing,
/// and state persistence.
/// </summary>
public sealed class MonitorWorker : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly DbContextOptions<WeatherDataContext> _dbOptions;
    private readonly Func<SmtpConfig, IEmailer> _emailerFactory;
    private readonly IMonitorStateStore _stateStore;
    private readonly Func<DateTime> _utcNow;

    // The watcher family, run in order each cycle. Stateless — detection state lives in
    // MonitorState via the WatcherContext. New watchers are added to this list.
    private readonly IReadOnlyList<IWatcher> _watchers =
    [
        new LogScanWatcher(),
        new HeartbeatWatcher(),
        new MetarStalenessWatcher(),
        new ReportErrorWatcher(),
        new GrafanaDashboardWatcher(),
    ];

    private readonly Meter _meter = new("WxMonitor.Svc", "1.0.0");
    private readonly Counter<long> _monitorCycles;
    private readonly Counter<long> _alertsSent;

    /// <summary>Initializes a new instance of <see cref="MonitorWorker"/> with the given dependencies.</summary>
    /// <param name="config">Application configuration used to load the <c>Monitor</c> config section each cycle.</param>
    /// <param name="dbOptions">EF Core options used to open a <see cref="WeatherDataContext"/> to read SMTP secrets from <c>GlobalSettings</c>.</param>
    /// <param name="emailerFactory">Builds an <see cref="IEmailer"/> from the per-cycle-resolved <see cref="SmtpConfig"/>. Injected so tests can capture alerts instead of sending mail.</param>
    /// <param name="stateStore">Persists <see cref="MonitorState"/> between cycles. Injected so tests can use an in-memory store.</param>
    /// <param name="utcNow">Supplies the current UTC time. Injected so tests can pin "now" for cooldown and staleness checks.</param>
    public MonitorWorker(
        IConfiguration config,
        DbContextOptions<WeatherDataContext> dbOptions,
        Func<SmtpConfig, IEmailer> emailerFactory,
        IMonitorStateStore stateStore,
        Func<DateTime> utcNow)
    {
        _config = config;
        _dbOptions = dbOptions;
        _emailerFactory = emailerFactory;
        _stateStore = stateStore;
        _utcNow = utcNow;
        _monitorCycles = _meter.CreateCounter<long>("wxmonitor.cycles.total", description: "Number of completed monitor cycles.");
        _alertsSent = _meter.CreateCounter<long>("wxmonitor.alerts.total", description: "Number of alert emails sent.");
    }

    /// <summary>
    /// Entry point called by the .NET hosted-service infrastructure.
    /// Runs <see cref="RunCycleAsync"/> in a loop, sleeping for
    /// <c>Monitor:IntervalMinutes</c> between iterations, until the host requests shutdown.
    /// </summary>
    /// <param name="stoppingToken">Cancellation token signalled when the host is shutting down.</param>
    /// <sideeffects>Writes log entries on start, after each cycle, and on stop.</sideeffects>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.Info("MonitorWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                Logger.Error("Unhandled exception in monitor cycle.", ex);
            }

            // Beat after every cycle (success OR handled fault) — the monitor is a heartbeat READER for the
            // other services, but it needs its OWN heartbeat too: as a diagnostic breadcrumb (last-alive
            // stamp if it dies) and as the input to its own container healthcheck. It does not watch this
            // file itself — a worker cannot report its own death (WxWorkers.All is filtered to exclude it).
            Heartbeat.Write(new WxPaths(_config["InstallRoot"]).HeartbeatFile(WxWorkers.Monitor));

            var intervalMinutes = _config.GetValue<int>("Monitor:IntervalMinutes", 5);
            if (intervalMinutes <= 0)
            {
                Logger.Warn($"Monitor:IntervalMinutes is {intervalMinutes} — must be > 0. Using 1 minute.");
                intervalMinutes = 1;
            }
            Logger.Info($"Next monitor check in {intervalMinutes} minute(s).");
            try { await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken); }
            catch (OperationCanceledException) { }
        }

        Logger.Info("MonitorWorker stopped.");
    }

    // ── cycle ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Executes one monitor cycle: builds the per-cycle <see cref="WatcherContext"/>, runs each
    /// watcher, and delivers every finding to its sink. Persists <see cref="MonitorState"/> if any
    /// watcher advanced a watermark or recorded a delivery.
    /// </summary>
    /// <sideeffects>
    /// Reads log/heartbeat files and queries the Metars table (via the watchers).
    /// Sends alert emails via SMTP for qualifying findings.
    /// Updates and saves <see cref="MonitorState"/> to <c>wxmonitor-state.json</c> if any state changed.
    /// Writes log entries throughout.
    /// </sideeffects>
    internal async Task RunCycleAsync(CancellationToken ct)
    {
        var (cfg, smtp) = await LoadConfigsAsync(ct);

        // Email alerts can't send without a destination, but the report-error watcher writes to a
        // JSONL sink and needs no email — so warn and carry on rather than skipping the whole cycle.
        if (string.IsNullOrWhiteSpace(cfg.AlertEmail))
            Logger.Warn("Monitor.AlertEmail is not set — email alerts are disabled; non-email watchers still run.");

        // WX-276: no early-return on an empty watched-services list. The per-service watchers
        // (log-scan, heartbeat) no-op over it, and the METAR-staleness watcher — which does not
        // depend on watched services — runs regardless, as it should.
        var state = _stateStore.Load();
        var now = _utcNow();
        var paths = new WxPaths(_config["InstallRoot"]);

        var ctx = new WatcherContext
        {
            Config = cfg,
            UtcNow = now,
            DbOptions = _dbOptions,
            Paths = paths,
            State = state,
        };

        var cooldown = TimeSpan.FromMinutes(cfg.AlertCooldownMinutes);
        var emailSink = new EmailSink(_emailerFactory(smtp), cfg.AlertEmail, cooldown, now, () => _alertsSent.Add(1));
        var jsonlSink = new JsonlSink(Path.Combine(paths.LogsDir, "findings.jsonl"), now);

        foreach (var watcher in _watchers)
        {
            var findings = await watcher.RunAsync(ctx, ct);
            var sinks = SinksFor(watcher.Id, emailSink, jsonlSink);
            foreach (var finding in findings)
            {
                foreach (var sink in sinks)
                    await sink.EmitAsync(finding, ct);
            }
        }

        if (ctx.StateDirty)
            _stateStore.Save(state);

        _monitorCycles.Add(1);
        Logger.Info("Monitor cycle complete.");
    }

    /// <summary>
    /// Resolves the sink(s) a watcher's findings are delivered to. The operational watchers
    /// (log-scan, heartbeat, METAR-staleness, grafana-dashboards) alert a human by email; the
    /// report-error watcher writes durable, review-oriented JSONL records with no email and no
    /// rate-limiting.
    /// </summary>
    private static IReadOnlyList<ISink> SinksFor(string watcherId, EmailSink email, JsonlSink jsonl)
        => watcherId == ReportErrorWatcher.WatcherId ? [jsonl] : [email];

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads and returns the current configuration.  Non-secret settings come from
    /// config files.  SMTP secrets are read exclusively from <see cref="GlobalSettings"/>
    /// (Id = 1) in the database.
    /// </summary>
    private async Task<(MonitorConfig monitor, SmtpConfig smtp)> LoadConfigsAsync(CancellationToken ct)
    {
        var monitor = new MonitorConfig();
        _config.GetSection("Monitor").Bind(monitor);

        var paths = new WxPaths(_config["InstallRoot"]);
        foreach (var svc in monitor.WatchedServices)
        {
            // WX-290/WX-106: resolve the ONE canonical token and derive the LOG path from it, so the
            // log-scan watcher reads exactly the file the service writes. An explicit LogFile in config
            // still wins — the escape hatch for a non-standard watched target. Heartbeats are no longer
            // derived here: HeartbeatWatcher watches them per-worker from the WxWorkers registry (WX-68
            // Unit 2), independent of this per-service log-scan list.
            var token = WxServiceToken.FromConfigName(svc.Name);
            if (string.IsNullOrEmpty(svc.LogFile)) svc.LogFile = paths.ServiceLogFile(token);
        }

        var smtp = new SmtpConfig();
        _config.GetSection("Smtp").Bind(smtp);

        await using var ctx = new WeatherDataContext(_dbOptions);
        var gs = await ctx.GlobalSettings.FirstOrDefaultAsync(x => x.Id == 1, ct);
        smtp.Username = gs?.SmtpUsername ?? "";
        smtp.Password = gs?.SmtpPassword ?? "";
        smtp.FromAddress = gs?.SmtpFromAddress ?? "";

        return (monitor, smtp);
    }
}