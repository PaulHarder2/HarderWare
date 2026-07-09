namespace WxMonitor.Svc.Watchers;

/// <summary>
/// Probes a grafana instance for the number of dashboards it currently serves. Returns the count,
/// or <see langword="null"/> when the probe itself could not obtain a trustworthy answer — grafana
/// unreachable, auth rejected, a non-success status, a timeout, or a malformed response. A probe
/// failure is deliberately distinct from a confirmed zero: the watcher must never fire the
/// too-few-dashboards alert on a grafana that is merely down, booting, or misconfigured for auth.
/// Injected into <see cref="GrafanaDashboardWatcher"/> so it is unit-testable without live HTTP.
/// </summary>
public interface IGrafanaDashboardProbe
{
    /// <summary>
    /// Returns the number of dashboards <paramref name="baseUrl"/> currently serves, or
    /// <see langword="null"/> if the count could not be trustworthily determined.
    /// </summary>
    /// <param name="baseUrl">Grafana base URL, e.g. <c>http://localhost:3000</c>.</param>
    /// <param name="token">Grafana Viewer service-account token used for bearer auth.</param>
    /// <param name="ct">Cancellation token, signalled on service shutdown.</param>
    Task<int?> GetDashboardCountAsync(string baseUrl, string token, CancellationToken ct);
}