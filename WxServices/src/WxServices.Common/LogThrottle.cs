namespace WxServices.Common;

/// <summary>
/// A minimal time-based emit gate: <see cref="ShouldLog"/> returns true the first time and then at
/// most once per <c>window</c>, so a high-frequency repeating condition — e.g. an OTLP export failing
/// every 10 s while the collector is down — is logged as an onset plus a periodic heartbeat rather
/// than a flood. Thread-safe: <see cref="ShouldLog"/> is guarded, because the diagnostics listener's
/// <c>OnEventWritten</c> can be invoked concurrently from several exporter/SDK threads.
/// </summary>
public sealed class LogThrottle
{
    private readonly TimeSpan _window;
    private readonly object _gate = new();
    private DateTime _lastUtc = DateTime.MinValue;

    /// <param name="window">Minimum spacing between two consecutive true results.</param>
    public LogThrottle(TimeSpan window) => _window = window;

    /// <summary>
    /// True if <paramref name="nowUtc"/> is at least <c>window</c> past the previous true result
    /// (and the first call is always true); records the time on each true result.
    /// </summary>
    public bool ShouldLog(DateTime nowUtc)
    {
        lock (_gate)
        {
            if (nowUtc - _lastUtc < _window)
                return false;
            _lastUtc = nowUtc;
            return true;
        }
    }
}