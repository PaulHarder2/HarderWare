namespace WxMonitor.Svc.Watchers;

/// <summary>
/// Delivers a <see cref="Finding"/> somewhere — email today, a JSONL findings file in a future
/// watcher. The scheduler routes each finding to its watcher's configured <b>set</b> of sinks, so
/// a finding can go to zero, one, or several sinks (the and/or seam; wired one-per-watcher today).
/// </summary>
public interface ISink
{
    /// <summary>Delivers a single finding.</summary>
    Task EmitAsync(Finding finding, CancellationToken ct);
}