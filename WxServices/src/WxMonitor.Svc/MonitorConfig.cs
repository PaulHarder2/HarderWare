// Configuration model for WxMonitor.Svc.
// Populated from the "Monitor" section of appsettings files.
// SMTP secrets (Username, Password, FromAddress) come from the top-level "Smtp" section
// in appsettings.local.json, shared with other services.
//
// SmtpConfig is defined in WxServices.Common and bound from the top-level
// "Smtp" section, shared with other services.

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
    public int    IntervalMinutes      { get; set; } = 5;

    /// <summary>Email address that receives alert messages.</summary>
    public string AlertEmail           { get; set; } = "";

    /// <summary>Minimum log4net level that triggers an alert: DEBUG, INFO, WARN, ERROR, or FATAL.</summary>
    public string AlertOnSeverity      { get; set; } = "ERROR";

    /// <summary>Minimum minutes between repeat alerts of the same type for the same service.</summary>
    public int    AlertCooldownMinutes { get; set; } = 60;

    public List<WatchedServiceConfig> WatchedServices { get; set; } = [];
}

/// <summary>Configuration for a single service watched by WxMonitor.</summary>
public class WatchedServiceConfig
{
    /// <summary>Display name used in alert subjects and log messages.</summary>
    public string Name                   { get; set; } = "";

    /// <summary>Absolute path to the service's log4net log file.</summary>
    public string LogFile                { get; set; } = "";

    /// <summary>Absolute path to the service's heartbeat file.</summary>
    public string HeartbeatFile          { get; set; } = "";

    /// <summary>Age in minutes beyond which a heartbeat is considered stale.</summary>
    public int    HeartbeatMaxAgeMinutes { get; set; } = 30;
}

