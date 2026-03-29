namespace WxMonitor.Svc;

/// <summary>
/// Persisted state for the monitor — survives service restarts.
/// One <see cref="ServiceState"/> entry per watched service, keyed by service name.
/// </summary>
public class MonitorState
{
    public Dictionary<string, ServiceState> Services { get; set; } = new();

    /// <summary>
    /// Returns the <see cref="ServiceState"/> for the given service name,
    /// creating and registering a new empty entry if one does not yet exist.
    /// </summary>
    /// <param name="serviceName">The service's display name, used as the dictionary key.</param>
    /// <returns>The existing or newly created <see cref="ServiceState"/> for the service.</returns>
    /// <sideeffects>May add a new entry to <see cref="Services"/> if the name is not already present.</sideeffects>
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
