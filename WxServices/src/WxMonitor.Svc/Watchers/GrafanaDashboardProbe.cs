using System.Net.Http.Headers;

using WxServices.Logging;

namespace WxMonitor.Svc.Watchers;

/// <summary>
/// Production <see cref="IGrafanaDashboardProbe"/>: calls grafana's <c>GET /api/search?type=dash-db</c>
/// with a bearer service-account token and returns the length of the returned dashboard array. Any
/// failure — non-success status, transport error, timeout, or malformed body — is logged and returned
/// as <see langword="null"/> (an inconclusive reading), never as a zero count.
/// </summary>
public sealed class GrafanaDashboardProbe : IGrafanaDashboardProbe
{
    // Shared across cycles; each probe is a single lightweight GET with a bounded timeout.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <inheritdoc/>
    public async Task<int?> GetDashboardCountAsync(string baseUrl, string token, CancellationToken ct)
    {
        var uri = $"{baseUrl.TrimEnd('/')}/api/search?type=dash-db";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn($"Grafana dashboard probe: HTTP {(int)response.StatusCode} from {uri}.");
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var count = GrafanaDashboardWatcher.ParseDashboardCount(body);
            if (count is null)
                Logger.Warn($"Grafana dashboard probe: could not parse a dashboard array from {uri}.");
            return count;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // genuine service shutdown — propagate, don't swallow it as a probe failure
        }
        catch (Exception ex)
        {
            // Includes HttpRequestException and the timeout TaskCanceledException (ct not signalled).
            Logger.Warn($"Grafana dashboard probe failed: {ex.Message}");
            return null;
        }
    }
}