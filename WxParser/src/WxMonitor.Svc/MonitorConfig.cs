// Configuration model for WxMonitor.Svc.
// Populated from the "Monitor" section of appsettings files.
// Secrets (Smtp.Password) must be provided in appsettings.local.json.

namespace WxMonitor.Svc;

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
    public MonitorSmtpConfig          Smtp            { get; set; } = new();
}

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

public class MonitorSmtpConfig
{
    public string  Host        { get; set; } = "smtp.gmail.com";
    public int     Port        { get; set; } = 587;
    public string? Username    { get; set; }
    public string? Password    { get; set; }
    public string? FromAddress { get; set; }
    public string  FromName    { get; set; } = "WxMonitor";
}
