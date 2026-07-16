// Configuration model for WxMonitor.Svc.
// Non-secret settings are populated from the "Monitor" section of appsettings files.
// SMTP secrets are stored in the GlobalSettings database row.

using WxServices.Common;

namespace WxMonitor.Svc;

/// <summary>
/// Root configuration model for WxMonitor.Svc.
/// Bound from the <c>Monitor</c> section of appsettings files at runtime.
/// SMTP credentials are bound separately from the top-level <c>Smtp</c> section.
/// </summary>
public class MonitorConfig
{
    /// <summary>How often the monitor checks logs and heartbeats (minutes).</summary>
    public int IntervalMinutes { get; set; } = 5;

    /// <summary>Email address that receives alert messages.</summary>
    public string AlertEmail { get; set; } = "";

    /// <summary>Minimum log4net level that triggers an alert: DEBUG, INFO, WARN, ERROR, or FATAL.</summary>
    public string AlertOnSeverity { get; set; } = "ERROR";

    /// <summary>Minimum minutes between repeat alerts of the same type for the same service.</summary>
    public int AlertCooldownMinutes { get; set; } = 60;

    /// <summary>
    /// Age in minutes beyond which the most recent METAR observation in the database is
    /// considered stale and triggers an alert.  Default 120 (2 hours).
    /// Set to 0 to disable the staleness check.
    /// </summary>
    public int MetarStalenessThresholdMinutes { get; set; } = 120;

    /// <summary>
    /// Base URL of the local grafana to probe for its served-dashboard count, e.g.
    /// <c>http://localhost:3000</c>.  Blank (the default) disables the grafana dashboard-count check.
    /// </summary>
    public string GrafanaUrl { get; set; } = "";

    /// <summary>
    /// Grafana Viewer service-account token used to authenticate the dashboard-count probe (bearer auth).
    /// A secret — set it only in the host-only <c>appsettings.local.json</c> under InstallRoot, never in
    /// the committed <c>appsettings.shared.json</c>.
    /// </summary>
    public string GrafanaServiceAccountToken { get; set; } = "";

    /// <summary>
    /// Minimum number of dashboards grafana is expected to serve.  A reading below this floor — after
    /// the watcher's consecutive-reading debounce — raises an alert.  Default 4; set to 0 to disable.
    /// </summary>
    public int GrafanaDashboardFloor { get; set; } = 4;

    public List<WatchedServiceConfig> WatchedServices { get; set; } = [];
}

/// <summary>
/// Configuration for a single service whose LOG the monitor scans (<see cref="LogScanWatcher"/>).
/// Heartbeats are no longer configured per watched service — they're watched per-worker from the
/// shared <see cref="WxWorkers"/> registry (WX-68 Unit 2), so this carries only the log-scan target.
/// </summary>
public class WatchedServiceConfig
{
    /// <summary>Display name used in alert subjects and log messages.</summary>
    public string Name { get; set; } = "";

    /// <summary>Absolute path to the service's log4net log file (derived from the canonical token when blank).</summary>
    public string LogFile { get; set; } = "";
}