using System.Diagnostics.Metrics;
using MetarParser.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using WxServices.Common;
using WxServices.Logging;

namespace WxMonitor.Svc;

/// <summary>
/// Background service that periodically:
/// <list type="bullet">
///   <item>Scans each watched service's log file for new entries at or above
///   the configured severity threshold and sends an alert email if any are found.</item>
///   <item>Checks each watched service's heartbeat file and sends an alert if
///   the heartbeat is stale (service may be stopped or hung).</item>
///   <item>Queries the database to confirm that recent METAR observations exist;
///   sends an alert if the most recent observation is older than
///   <see cref="MonitorConfig.MetarStalenessThresholdMinutes"/>.</item>
/// </list>
/// Alerts are rate-limited per service per alert type via <see cref="MonitorConfig.AlertCooldownMinutes"/>.
/// </summary>
public sealed class MonitorWorker : BackgroundService
{
    private readonly IConfiguration                        _config;
    private readonly DbContextOptions<WeatherDataContext>  _dbOptions;

    private readonly Meter _meter = new("WxMonitor.Svc", "1.0.0");
    private readonly Counter<long> _monitorCycles;
    private readonly Counter<long> _alertsSent;

    /// <summary>Initializes a new instance of <see cref="MonitorWorker"/> with the given dependencies.</summary>
    /// <param name="config">Application configuration used to load the <c>Monitor</c> config section each cycle.</param>
    /// <param name="dbOptions">EF Core options used to open a <see cref="WeatherDataContext"/> to read SMTP secrets from <c>GlobalSettings</c>.</param>
    public MonitorWorker(IConfiguration config, DbContextOptions<WeatherDataContext> dbOptions)
    {
        _config        = config;
        _dbOptions     = dbOptions;
        _monitorCycles = _meter.CreateCounter<long>("wxmonitor.cycles.total", description: "Number of completed monitor cycles.");
        _alertsSent    = _meter.CreateCounter<long>("wxmonitor.alerts.total", description: "Number of alert emails sent.");
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
    /// Executes one monitor cycle: scans each watched service's log file for new
    /// high-severity entries, checks each heartbeat file for staleness, and queries
    /// the database to verify that recent METAR observations exist.
    /// Sends alert emails for any findings that are not on cooldown.
    /// </summary>
    /// <sideeffects>
    /// Reads log files and heartbeat files from disk.
    /// Queries the Metars table for the most recent observation timestamp.
    /// Sends alert emails via SMTP for qualifying findings.
    /// Updates and saves <see cref="MonitorState"/> to <c>wxmonitor-state.json</c> if any state changed.
    /// Writes log entries throughout.
    /// </sideeffects>
    private async Task RunCycleAsync(CancellationToken ct)
    {
        var (cfg, smtp) = await LoadConfigsAsync(ct);

        if (string.IsNullOrWhiteSpace(cfg.AlertEmail))
        {
            Logger.Warn("Monitor.AlertEmail is not set — skipping monitor cycle.");
            return;
        }

        if (cfg.WatchedServices.Count == 0)
        {
            Logger.Debug("No watched services configured.");
            return;
        }

        var state   = MonitorStateStore.Load();
        var emailer = new SmtpSender(smtp, "WxMonitor");
        var cooldown = TimeSpan.FromMinutes(cfg.AlertCooldownMinutes);
        var now      = DateTime.UtcNow;
        var dirty    = false;

        foreach (var svc in cfg.WatchedServices)
        {
            if (string.IsNullOrWhiteSpace(svc.Name))
            {
                Logger.Warn("Watched service entry has no Name — skipping.");
                continue;
            }

            var svcState = state.GetOrCreate(svc.Name);

            // ── Log scan ──────────────────────────────────────────────────────

            if (!string.IsNullOrWhiteSpace(svc.LogFile))
            {
                var newEntries = LogScanner.Scan(
                    svc.LogFile,
                    svcState.LastSeenLogTimestamp,
                    cfg.AlertOnSeverity,
                    out var latestTs);

                if (latestTs != svcState.LastSeenLogTimestamp)
                {
                    svcState.LastSeenLogTimestamp = latestTs;
                    dirty = true;
                }

                if (newEntries.Count > 0)
                {
                    var onCooldown = svcState.LastLogAlertSentUtc.HasValue
                        && (now - svcState.LastLogAlertSentUtc.Value) < cooldown;

                    if (!onCooldown)
                    {
                        Logger.Info($"{svc.Name}: {newEntries.Count} new {cfg.AlertOnSeverity}+ log entry/entries — sending alert.");
                        var subject = $"[WxMonitor] {svc.Name} — {newEntries.Count} new log error(s)";
                        var body    = BuildLogAlertBody(svc.Name, newEntries);

                        if (await emailer.SendAsync(cfg.AlertEmail, subject, body))
                        {
                            _alertsSent.Add(1);
                            svcState.LastLogAlertSentUtc = now;
                            dirty = true;
                        }
                    }
                    else
                    {
                        Logger.Debug($"{svc.Name}: {newEntries.Count} new error(s) found but alert is on cooldown.");
                    }
                }
            }

            // ── Heartbeat check ───────────────────────────────────────────────

            if (!string.IsNullOrWhiteSpace(svc.HeartbeatFile))
            {
                var age = HeartbeatChecker.GetAge(svc.HeartbeatFile);

                if (age is null)
                {
                    Logger.Warn($"{svc.Name}: heartbeat file not found at '{svc.HeartbeatFile}'.");
                }
                else if (age.Value.TotalMinutes > svc.HeartbeatMaxAgeMinutes)
                {
                    var onCooldown = svcState.LastHeartbeatAlertSentUtc.HasValue
                        && (now - svcState.LastHeartbeatAlertSentUtc.Value) < cooldown;

                    if (!onCooldown)
                    {
                        var ageMin = (int)age.Value.TotalMinutes;
                        Logger.Warn($"{svc.Name}: heartbeat is {ageMin} minute(s) old (max {svc.HeartbeatMaxAgeMinutes}) — sending alert.");
                        var subject = $"[WxMonitor] {svc.Name} — service may be stopped";
                        var body    = BuildHeartbeatAlertBody(svc.Name, age.Value, svc.HeartbeatMaxAgeMinutes);

                        if (await emailer.SendAsync(cfg.AlertEmail, subject, body))
                        {
                            _alertsSent.Add(1);
                            svcState.LastHeartbeatAlertSentUtc = now;
                            dirty = true;
                        }
                    }
                    else
                    {
                        Logger.Debug($"{svc.Name}: heartbeat stale but alert is on cooldown.");
                    }
                }
            }
        }

        // ── METAR staleness check ─────────────────────────────────────────────
        if (cfg.MetarStalenessThresholdMinutes > 0)
        {
            await using var metarCtx = new WeatherDataContext(_dbOptions);
            var mostRecentObsUtc = await metarCtx.Metars
                .MaxAsync(m => (DateTime?)m.ObservationUtc, ct);

            var ageMinutes = mostRecentObsUtc.HasValue
                ? (now - mostRecentObsUtc.Value).TotalMinutes
                : double.MaxValue;

            if (ageMinutes > cfg.MetarStalenessThresholdMinutes)
            {
                var onCooldown = state.LastMetarStalenessAlertSentUtc.HasValue
                    && (now - state.LastMetarStalenessAlertSentUtc.Value) < cooldown;

                if (!onCooldown)
                {
                    var ageMin = ageMinutes == double.MaxValue ? "∞" : ((int)ageMinutes).ToString();
                    Logger.Warn($"Most recent METAR observation is {ageMin} minute(s) old " +
                                $"(threshold {cfg.MetarStalenessThresholdMinutes}) — sending alert.");
                    var subject = "[WxMonitor] METAR data is stale — no recent observations";
                    var body    = BuildMetarStalenessAlertBody(mostRecentObsUtc, cfg.MetarStalenessThresholdMinutes);

                    if (await emailer.SendAsync(cfg.AlertEmail, subject, body))
                    {
                        _alertsSent.Add(1);
                        state.LastMetarStalenessAlertSentUtc = now;
                        dirty = true;
                    }
                }
                else
                {
                    Logger.Debug("METAR data is stale but staleness alert is on cooldown.");
                }
            }
        }

        if (dirty)
            MonitorStateStore.Save(state);

        _monitorCycles.Add(1);
        Logger.Info("Monitor cycle complete.");
    }

    // ── email body builders ───────────────────────────────────────────────────

    /// <summary>
    /// Builds a plain-text email body listing the new high-severity log entries
    /// detected for a watched service.
    /// </summary>
    /// <param name="serviceName">Display name of the service, used in the introductory line.</param>
    /// <param name="entries">The new log entries to include, in chronological order.</param>
    /// <returns>A formatted plain-text alert body ready to send as an email.</returns>
    private static string BuildLogAlertBody(string serviceName, IReadOnlyList<LogEntry> entries)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"WxMonitor detected {entries.Count} new log entry/entries at ERROR level or above in {serviceName}.");
        sb.AppendLine();
        sb.AppendLine("─────────────────────────────────────────────");
        foreach (var e in entries)
        {
            sb.AppendLine(e.Text);
            sb.AppendLine();
        }
        sb.AppendLine("─────────────────────────────────────────────");
        sb.AppendLine($"Generated by WxMonitor at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        return sb.ToString();
    }

    /// <summary>
    /// Builds a plain-text email body explaining that a watched service's heartbeat
    /// has gone stale beyond the configured maximum age.
    /// </summary>
    /// <param name="serviceName">Display name of the service whose heartbeat is stale.</param>
    /// <param name="age">How long ago the last heartbeat was written.</param>
    /// <param name="maxAgeMinutes">The configured maximum allowed heartbeat age in minutes.</param>
    /// <returns>A formatted plain-text alert body ready to send as an email.</returns>
    private static string BuildHeartbeatAlertBody(string serviceName, TimeSpan age, int maxAgeMinutes)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"WxMonitor detected a stale heartbeat for {serviceName}.");
        sb.AppendLine();
        sb.AppendLine($"  Last heartbeat:  {(int)age.TotalMinutes} minute(s) ago");
        sb.AppendLine($"  Maximum allowed: {maxAgeMinutes} minute(s)");
        sb.AppendLine();
        sb.AppendLine("This may indicate the service has stopped, crashed, or is hung.");
        sb.AppendLine("Check the Windows Service Manager and the service log for details.");
        sb.AppendLine();
        sb.AppendLine($"Generated by WxMonitor at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        return sb.ToString();
    }

    /// <summary>
    /// Builds a plain-text email body explaining that no recent METAR observations
    /// were found in the database.
    /// </summary>
    /// <param name="mostRecentUtc">Timestamp of the most recent observation, or null if the table is empty.</param>
    /// <param name="thresholdMinutes">The configured staleness threshold in minutes.</param>
    /// <returns>A formatted plain-text alert body ready to send as an email.</returns>
    private static string BuildMetarStalenessAlertBody(DateTime? mostRecentUtc, int thresholdMinutes)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("WxMonitor detected stale METAR data in the database.");
        sb.AppendLine();
        if (mostRecentUtc.HasValue)
        {
            var age = DateTime.UtcNow - mostRecentUtc.Value;
            sb.AppendLine($"  Most recent observation: {mostRecentUtc.Value:yyyy-MM-dd HH:mm} UTC  ({(int)age.TotalMinutes} minute(s) ago)");
        }
        else
        {
            sb.AppendLine("  No METAR observations found in the database at all.");
        }
        sb.AppendLine($"  Staleness threshold:     {thresholdMinutes} minute(s)");
        sb.AppendLine();
        sb.AppendLine("This may indicate WxParser.Svc is not running, or the AWC METAR API is unreachable.");
        sb.AppendLine("Check the WxParser.Svc log and Windows Service Manager for details.");
        sb.AppendLine();
        sb.AppendLine($"Generated by WxMonitor at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        return sb.ToString();
    }

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
            var svcTag = svc.Name.Replace(".", "-", StringComparison.Ordinal).ToLowerInvariant();
            if (string.IsNullOrEmpty(svc.LogFile))       svc.LogFile       = paths.LogFile(svcTag);
            if (string.IsNullOrEmpty(svc.HeartbeatFile)) svc.HeartbeatFile = paths.HeartbeatFile(svcTag);
        }

        var smtp = new SmtpConfig();
        _config.GetSection("Smtp").Bind(smtp);

        await using var ctx = new WeatherDataContext(_dbOptions);
        var gs = await ctx.GlobalSettings.FirstOrDefaultAsync(x => x.Id == 1, ct);
        smtp.Username    = gs?.SmtpUsername    ?? "";
        smtp.Password    = gs?.SmtpPassword    ?? "";
        smtp.FromAddress = gs?.SmtpFromAddress ?? "";

        return (monitor, smtp);
    }
}
