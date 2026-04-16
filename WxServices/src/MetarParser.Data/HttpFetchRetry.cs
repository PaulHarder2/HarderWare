using System.IO;
using System.Net;
using System.Net.Http;
using WxServices.Logging;

namespace MetarParser.Data;

/// <summary>
/// Retry wrapper around <see cref="HttpClient.GetStringAsync(string)"/> for the
/// upstream METAR / TAF / GFS fetchers.  Retries transient failures (5xx
/// responses, 429, SSL/TLS errors, network-level exceptions) with exponential
/// backoff; surfaces permanent failures (4xx other than 429) immediately so
/// callers with their own specific-status handling (e.g. GfsFetcher's 404 →
/// "forecast hour not yet published") continue to work unchanged.
/// </summary>
public static class HttpFetchRetry
{
    /// <summary>
    /// Issues an HTTP GET for <paramref name="url"/>, retrying transient
    /// failures up to <paramref name="maxAttempts"/> times with exponential
    /// backoff starting at 2 seconds and doubling per attempt (2 s, 4 s, 8 s
    /// for the default 3 attempts).  Logs each retry at <c>WARN</c>; the final
    /// failure throws and callers' existing catch blocks continue to log
    /// <c>ERROR</c> as they do today.
    /// </summary>
    /// <param name="http">The <see cref="HttpClient"/> to use for the request.</param>
    /// <param name="url">The absolute URL to fetch.</param>
    /// <param name="sourceLabel">
    /// Short human-readable label identifying what is being fetched
    /// (e.g. <c>"METAR"</c>, <c>"TAF"</c>, <c>"GFS f077 index"</c>).  Used in
    /// retry log lines so the operator can tell which fetcher retried.
    /// </param>
    /// <param name="maxAttempts">
    /// Total number of attempts (not extra retries).  Default 3 — first try
    /// plus two retries.  Must be at least 1.
    /// </param>
    /// <param name="ct">Cancellation token propagated to the HTTP and delay calls.</param>
    /// <returns>The response body as a string, on the first successful attempt.</returns>
    /// <exception cref="HttpRequestException">
    /// Rethrown from the final failed attempt, or thrown immediately for
    /// non-transient responses (4xx other than 429) so caller-specific
    /// handling still applies.
    /// </exception>
    public static async Task<string> GetStringWithRetryAsync(
        this HttpClient http,
        string url,
        string sourceLabel,
        int maxAttempts = 3,
        CancellationToken ct = default)
    {
        if (maxAttempts < 1) maxAttempts = 1;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await http.GetStringAsync(url, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Honour genuine cancellation — do not retry.
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex))
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 2 s, 4 s, 8 s
                Logger.Warn(
                    $"{sourceLabel}: transient fetch failure (attempt {attempt}/{maxAttempts}): " +
                    $"{ex.Message} — retrying in {delay.TotalSeconds:F0}s.");
                await Task.Delay(delay, ct);
            }
            // Non-transient or final-attempt failures fall through — the caller
            // keeps its own catch block (logging ERROR, breaking the cycle).
        }
    }

    /// <summary>
    /// Classifies whether an exception from an HTTP GET represents a transient
    /// condition worth retrying.  5xx responses and 429 are transient; 4xx
    /// (other than 429) are permanent and must not be retried — callers rely
    /// on them (e.g. GfsFetcher treats 404 as "forecast hour not yet
    /// published").  SSL/TLS handshake failures surface as
    /// <see cref="HttpRequestException"/> with <c>StatusCode == null</c> and
    /// are treated as transient.  Network-level <see cref="IOException"/>
    /// and request-timeout <see cref="TaskCanceledException"/> are transient.
    /// </summary>
    private static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException hre => hre.StatusCode is null
            || (int)hre.StatusCode.Value >= 500
            || hre.StatusCode == HttpStatusCode.TooManyRequests,

        // HttpClient raises TaskCanceledException for request timeouts (vs.
        // explicit cancellation, which is caught above via ct.IsCancellationRequested).
        TaskCanceledException => true,

        IOException => true,

        _ => false,
    };
}
