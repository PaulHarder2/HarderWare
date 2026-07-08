namespace WxMonitor.Svc.Watchers;

/// <summary>
/// A monitoring check that runs once per cycle. Each watcher owns its own cardinality
/// (per watched service, global, ...), its detection logic, and its watermark advancement,
/// and returns the findings to deliver this cycle. It does <b>not</b> deliver them — the
/// scheduler routes each finding to the watcher's configured sink(s). This is the seam the
/// WxMonitor watcher family (log-scan, heartbeat, METAR-staleness, and future report watchers)
/// is built on.
/// </summary>
public interface IWatcher
{
    /// <summary>Stable identifier, e.g. <c>"log-scan"</c>. Used in log narration and metric tags.</summary>
    string Id { get; }

    /// <summary>Human-readable name for operator-facing text.</summary>
    string DisplayName { get; }

    /// <summary>Runs one detection pass and returns the findings to deliver this cycle.</summary>
    Task<IReadOnlyList<Finding>> RunAsync(WatcherContext ctx, CancellationToken ct);
}