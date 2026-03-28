using Microsoft.Extensions.Configuration;
using WxParser.Logging;

namespace WxMonitor.Svc;

/// <summary>
/// Background service that periodically:
/// <list type="bullet">
///   <item>Scans each watched service's log file for new entries at or above
///   the configured severity threshold and sends an alert email if any are found.</item>
///   <item>Checks each watched service's heartbeat file and sends an alert if
///   the heartbeat is stale (service may be stopped or hung).</item>
/// </list>
/// Alerts are rate-limited per service per alert type via <see cref="MonitorConfig.AlertCooldownMinutes"/>.
/// </summary>
public sealed class MonitorWorker : BackgroundService
{
    private readonly IConfiguration _config;

    public MonitorWorker(IConfiguration config) => _config = config;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.Info("MonitorWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync();
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                Logger.Error("Unhandled exception in monitor cycle.", ex);
            }

            var cfg = LoadConfig();
            Logger.Info($"Next monitor check in {cfg.IntervalMinutes} minute(s).");
            try { await Task.Delay(TimeSpan.FromMinutes(cfg.IntervalMinutes), stoppingToken); }
            catch (OperationCanceledException) { }
        }

        Logger.Info("MonitorWorker stopped.");
    }

    // ── cycle ─────────────────────────────────────────────────────────────────

    private async Task RunCycleAsync()
    {
        var cfg = LoadConfig();

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
        var emailer = new AlertEmailSender(cfg.Smtp);
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

        if (dirty)
            MonitorStateStore.Save(state);

        Logger.Info("Monitor cycle complete.");
    }

    // ── email body builders ───────────────────────────────────────────────────

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

    // ── helpers ───────────────────────────────────────────────────────────────

    private MonitorConfig LoadConfig()
    {
        var cfg = new MonitorConfig();
        _config.GetSection("Monitor").Bind(cfg);
        return cfg;
    }
}
