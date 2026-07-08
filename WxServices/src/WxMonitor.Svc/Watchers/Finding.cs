namespace WxMonitor.Svc.Watchers;

/// <summary>
/// A rate-limit slot for a finding: reads and writes the "last delivered" timestamp for the
/// finding's category, backed by the relevant <see cref="MonitorState"/> field. The setter also
/// marks the monitor state dirty so the scheduler persists it. Findings that should never be
/// rate-limited (e.g. a durable JSONL record) carry no slot.
/// </summary>
public sealed class CooldownSlot(Func<DateTime?> lastSent, Action<DateTime> markSent)
{
    /// <summary>When this finding's category last delivered, or <see langword="null"/> if never.</summary>
    public DateTime? LastSentUtc => lastSent();

    /// <summary>Records that this finding's category delivered at <paramref name="nowUtc"/>.</summary>
    public void MarkSent(DateTime nowUtc) => markSent(nowUtc);
}

/// <summary>
/// One thing a watcher detected and wants delivered. The watcher renders <see cref="Subject"/>
/// and <see cref="Body"/> itself (it knows best how to phrase its own alert); each
/// <see cref="ISink"/> takes what it needs. <see cref="Cooldown"/> carries the per-category
/// rate-limit slot for sinks that rate-limit (e.g. email); it is <see langword="null"/> for
/// findings that should always be delivered.
/// </summary>
public sealed record Finding(
    string WatcherId,
    string Subject,
    string Body,
    CooldownSlot? Cooldown = null);