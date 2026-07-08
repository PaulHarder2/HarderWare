namespace WxMonitor.Svc.Watchers;

/// <summary>
/// Delivers a <see cref="Finding"/> somewhere. Today the scheduler delivers every finding to a
/// single <see cref="EmailSink"/>; the interface is the seam for routing a finding to a <b>set</b>
/// of sinks (e.g. a JSONL findings file alongside email), which WX-273 wires in.
/// </summary>
public interface ISink
{
    /// <summary>Delivers a single finding.</summary>
    Task EmitAsync(Finding finding, CancellationToken ct);
}