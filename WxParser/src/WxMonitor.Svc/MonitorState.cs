namespace WxMonitor.Svc;

/// <summary>
/// Persisted state for the monitor — survives service restarts.
/// One <see cref="ServiceState"/> entry per watched service, keyed by service name.
/// </summary>
public class MonitorState
{
    public Dictionary<string, ServiceState> Services { get; set; } = new();

    public ServiceState GetOrCreate(string serviceName)
    {
        if (!Services.TryGetValue(serviceName, out var state))
        {
            state = new ServiceState();
            Services[serviceName] = state;
        }
        return state;
    }
}

public class ServiceState
{
    /// <summary>
    /// Timestamp of the most-recent log entry the monitor has already processed.
    /// Null on first run — the monitor will baseline to the current log tail
    /// without sending alerts, to avoid flooding on first install.
    /// </summary>
    public DateTime? LastSeenLogTimestamp      { get; set; }

    /// <summary>UTC time the most recent log-error alert was sent for this service.</summary>
    public DateTime? LastLogAlertSentUtc       { get; set; }

    /// <summary>UTC time the most recent heartbeat-stale alert was sent for this service.</summary>
    public DateTime? LastHeartbeatAlertSentUtc { get; set; }
}
