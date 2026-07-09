using System.Text;
using System.Text.Json;

using WxServices.Logging;

namespace WxMonitor.Svc.Watchers;

/// <summary>
/// Probes the local grafana each cycle and raises a (global) finding when it serves fewer dashboards
/// than the configured floor. The WX-278 boot race left grafana reporting <c>/api/health</c> healthy
/// while provisioning zero dashboards — a silent failure that went unnoticed for hours; a count-based
/// check is the signal health cannot give. The count is read via <see cref="IGrafanaDashboardProbe"/>
/// (<c>GET /api/search?type=dash-db</c>). A shortfall must persist for <see cref="AlertThreshold"/>
/// consecutive readings before it alerts, which suppresses a false positive during grafana's own
/// boot/provisioning window without tracking grafana's start time. Disabled unless
/// <see cref="MonitorConfig.GrafanaUrl"/> is set and <see cref="MonitorConfig.GrafanaDashboardFloor"/>
/// is positive.
/// </summary>
public sealed class GrafanaDashboardWatcher : IWatcher
{
    /// <summary>The watcher's stable id.</summary>
    public const string WatcherId = "grafana-dashboards";

    /// <summary>
    /// Consecutive sub-floor readings required before the first alert. Two readings one cycle apart
    /// ride over grafana's provisioning window — a transient empty read on the cycle that catches boot
    /// mid-provision does not alert — at the cost of ~one cycle of extra latency on a real failure.
    /// </summary>
    internal const int AlertThreshold = 2;

    private readonly IGrafanaDashboardProbe _probe;

    /// <summary>Production constructor — probes grafana over HTTP.</summary>
    public GrafanaDashboardWatcher() : this(new GrafanaDashboardProbe()) { }

    /// <summary>Testable constructor — takes an injected probe.</summary>
    public GrafanaDashboardWatcher(IGrafanaDashboardProbe probe) => _probe = probe;

    /// <inheritdoc/>
    public string Id => WatcherId;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Finding>> RunAsync(WatcherContext ctx, CancellationToken ct)
    {
        var url = ctx.Config.GrafanaUrl;
        var floor = ctx.Config.GrafanaDashboardFloor;
        if (string.IsNullOrWhiteSpace(url) || floor <= 0)
            return [];   // inactive until a grafana URL and a positive floor are configured

        var count = await _probe.GetDashboardCountAsync(url, ctx.Config.GrafanaServiceAccountToken, ct);
        if (count is null)
            return [];   // inconclusive probe (down / booting / auth) — not a confirmed shortfall; leave the streak

        if (count.Value >= floor)
        {
            // Healthy — clear any accumulated shortfall streak so a future blip starts fresh.
            if (ctx.State.GrafanaSubFloorStreak != 0)
            {
                ctx.State.GrafanaSubFloorStreak = 0;
                ctx.MarkStateDirty();
            }
            return [];
        }

        // Sub-floor: advance the debounce streak (capped — once alerting, it holds at the threshold).
        // Persist only when it actually changes, so a sustained outage doesn't rewrite state every cycle
        // (mirrors the healthy branch's guarded reset above).
        var streak = Math.Min(ctx.State.GrafanaSubFloorStreak + 1, AlertThreshold);
        if (streak != ctx.State.GrafanaSubFloorStreak)
        {
            ctx.State.GrafanaSubFloorStreak = streak;
            ctx.MarkStateDirty();
        }

        Logger.Warn($"Grafana is serving {count.Value} dashboard(s), below the floor of {floor} " +
                    $"(consecutive reading {streak} of {AlertThreshold}).");

        if (streak < AlertThreshold)
            return [];   // first shortfall reading — wait one cycle to rule out the provisioning window

        return
        [
            new Finding(
                Id,
                "[WxMonitor] Grafana is serving too few dashboards",
                BuildBody(count.Value, floor, url, ctx.UtcNow),
                ctx.NewCooldownSlot(
                    () => ctx.State.LastGrafanaDashboardAlertSentUtc,
                    v => ctx.State.LastGrafanaDashboardAlertSentUtc = v)),
        ];
    }

    /// <summary>
    /// Parses the dashboard count from a grafana <c>/api/search</c> response body: the length of the
    /// top-level JSON array. Returns <see langword="null"/> for a non-array or malformed body — an
    /// unparseable response is an inconclusive reading, not a zero count.
    /// </summary>
    internal static int? ParseDashboardCount(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.GetArrayLength()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Builds the plain-text alert body naming the shortfall and where to look.</summary>
    private static string BuildBody(int count, int floor, string url, DateTime now)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WxMonitor detected that grafana is serving fewer dashboards than expected.");
        sb.AppendLine();
        sb.AppendLine($"  Dashboards currently served: {count}");
        sb.AppendLine($"  Expected floor:              {floor}");
        sb.AppendLine($"  Grafana:                     {url}");
        sb.AppendLine();
        sb.AppendLine("A count below the floor — especially zero — usually means grafana came up without its");
        sb.AppendLine("provisioned dashboards (an empty provisioning mount, a corrupted grafana-data volume, or");
        sb.AppendLine("a bad dashboard JSON). Grafana can still report /api/health as healthy in this state, so");
        sb.AppendLine("this count-based check is the signal, not health.");
        sb.AppendLine();
        sb.AppendLine("Check the observability stack: docker compose ps, then grafana's provisioning and logs.");
        sb.AppendLine("Rebuild with: docker compose up -d --build grafana");
        sb.AppendLine();
        AlertBody.AppendFooter(sb, now);
        return sb.ToString();
    }
}