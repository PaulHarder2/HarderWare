namespace WxMonitor.Svc;

/// <summary>
/// Persisted state for the monitor — survives service restarts.
/// One <see cref="ServiceState"/> entry per watched service, keyed by service name.
/// </summary>
public class MonitorState
{
    public Dictionary<string, ServiceState> Services { get; set; } = new();

    /// <summary>UTC time the most recent METAR-staleness alert was sent.</summary>
    public DateTime? LastMetarStalenessAlertSentUtc { get; set; }

    /// <summary>
    /// Highest <c>CommittedSend.SentAtUtc</c> the report-error watcher has scanned (WX-273). Keyed on
    /// send-time rather than <c>Id</c> because <c>Id</c> is assigned at provisional insert while
    /// <c>SentAtUtc</c> is set at send completion — so <c>SentAtUtc</c> is monotonic with the order sends
    /// actually ship, and a lower-<c>Id</c> send that ships later is not skipped. Null until first
    /// baselined; the watcher then only scans sends past this time (forward monitoring).
    /// </summary>
    public DateTime? LastReportScanUtc { get; set; }

    /// <summary>UTC time the most recent grafana too-few-dashboards alert was sent (WX-279 cooldown).</summary>
    public DateTime? LastGrafanaDashboardAlertSentUtc { get; set; }

    /// <summary>
    /// Consecutive sub-floor grafana dashboard-count readings (WX-279 debounce). Reset to 0 on a healthy
    /// reading; a probe failure leaves it unchanged (inconclusive). Alerting begins once it reaches
    /// <see cref="Watchers.GrafanaDashboardWatcher.AlertThreshold"/>, which suppresses false positives
    /// during grafana's boot/provisioning window.
    /// </summary>
    public int GrafanaSubFloorStreak { get; set; }

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
    public DateTime? LastSeenLogTimestamp { get; set; }

    /// <summary>UTC time the most recent log-error alert was sent for this service.</summary>
    public DateTime? LastLogAlertSentUtc { get; set; }

    /// <summary>UTC time the most recent heartbeat-stale alert was sent for this service.</summary>
    public DateTime? LastHeartbeatAlertSentUtc { get; set; }
}