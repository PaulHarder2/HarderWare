using MetarParser.Data;

using Microsoft.EntityFrameworkCore;

using WxServices.Common;

namespace WxMonitor.Svc.Watchers;

/// <summary>
/// Per-cycle context handed to each <see cref="IWatcher"/>. Carries the shared services a watcher
/// may need. <see cref="UtcNow"/> is stamped once per cycle so every watcher sees a consistent
/// "now" (cooldown windows, staleness ages, and alert-body footers all key off it). <see cref="State"/>
/// is the loaded <see cref="MonitorState"/> — a watcher that advances a watermark or records a
/// delivery mutates it and calls <see cref="MarkStateDirty"/> so the scheduler persists it.
/// </summary>
public sealed class WatcherContext
{
    /// <summary>The parsed monitor configuration for this cycle (watched services, thresholds, severity).</summary>
    public required MonitorConfig Config { get; init; }

    /// <summary>The current UTC time, stamped once at the start of the cycle.</summary>
    public required DateTime UtcNow { get; init; }

    /// <summary>EF options for opening a <see cref="WeatherDataContext"/> (used by DB-backed watchers).</summary>
    public required DbContextOptions<WeatherDataContext> DbOptions { get; init; }

    /// <summary>InstallRoot-derived paths (logs dir, config locations) for watchers that read files.</summary>
    public required WxPaths Paths { get; init; }

    /// <summary>The persisted monitor state (watermarks and last-delivered timestamps).</summary>
    public required MonitorState State { get; init; }

    /// <summary>True once any watcher has mutated <see cref="State"/> this cycle.</summary>
    public bool StateDirty { get; private set; }

    /// <summary>Marks <see cref="State"/> as changed so the scheduler persists it at cycle end.</summary>
    public void MarkStateDirty() => StateDirty = true;

    /// <summary>
    /// Builds a <see cref="CooldownSlot"/> over a <see cref="MonitorState"/> field. The setter is
    /// wrapped so writing the timestamp also marks state dirty — a watcher cannot record a delivery
    /// without persisting it, removing the "forgot to call <see cref="MarkStateDirty"/>" footgun.
    /// </summary>
    public CooldownSlot NewCooldownSlot(Func<DateTime?> lastSent, Action<DateTime> setLastSent)
        => new(lastSent, v => { setLastSent(v); MarkStateDirty(); });
}