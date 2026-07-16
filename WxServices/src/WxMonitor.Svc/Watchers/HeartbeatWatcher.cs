using System.Text;

using WxServices.Common;
using WxServices.Logging;

namespace WxMonitor.Svc.Watchers;

/// <summary>
/// Checks every registered worker's heartbeat file and raises a finding when a heartbeat is older than
/// that worker's registered maximum age (the worker may be stopped, crashed, or hung). The watch-set is
/// the shared <see cref="WxWorkers.All"/> registry — the same source each worker derives its own
/// heartbeat filename from — so the writer and this reader can never disagree about a name (the WX-106
/// blind-spot class, closed at the worker grain in WX-68 Unit 2). The monitor's OWN service is skipped:
/// a worker cannot report its own death, so wxmonitor self-liveness is left to the container healthcheck.
/// </summary>
public sealed class HeartbeatWatcher : IWatcher
{
    /// <inheritdoc/>
    public string Id => "heartbeat";

    /// <inheritdoc/>
    public Task<IReadOnlyList<Finding>> RunAsync(WatcherContext ctx, CancellationToken ct)
    {
        var findings = new List<Finding>();

        foreach (var worker in WxWorkers.All)
        {
            // The monitor can't meaningfully watch its own heartbeat — if its cycle is dead it isn't
            // running to notice. wxmonitor self-liveness is the container healthcheck's job.
            if (worker.Service == WxServiceToken.WxMonitor)
                continue;

            var path = ctx.Paths.HeartbeatFile(worker);
            var age = HeartbeatChecker.GetAge(path, ctx.UtcNow);

            if (age is null)
            {
                Logger.Warn($"{worker.Token}: heartbeat file not found at '{path}'.");
                continue;
            }

            if (age.Value.TotalMinutes > worker.DefaultMaxAgeMinutes)
            {
                var workerState = ctx.State.GetOrCreate(worker.Token);
                Logger.Warn($"{worker.Token}: heartbeat is {(int)age.Value.TotalMinutes} minute(s) old (max {worker.DefaultMaxAgeMinutes}).");
                findings.Add(new Finding(
                    Id,
                    $"[WxMonitor] {worker.Token} — worker may be stopped",
                    BuildBody(worker.Token, age.Value, worker.DefaultMaxAgeMinutes, ctx.UtcNow),
                    ctx.NewCooldownSlot(
                        () => workerState.LastHeartbeatAlertSentUtc,
                        v => workerState.LastHeartbeatAlertSentUtc = v)));
            }
        }

        return Task.FromResult<IReadOnlyList<Finding>>(findings);
    }

    /// <summary>Builds the plain-text alert body explaining that a worker's heartbeat has gone stale.</summary>
    private static string BuildBody(string workerToken, TimeSpan age, int maxAgeMinutes, DateTime now)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"WxMonitor detected a stale heartbeat for {workerToken}.");
        sb.AppendLine();
        sb.AppendLine($"  Last heartbeat:  {(int)age.TotalMinutes} minute(s) ago");
        sb.AppendLine($"  Maximum allowed: {maxAgeMinutes} minute(s)");
        sb.AppendLine();
        sb.AppendLine("This may indicate the worker has stopped, crashed, or is hung.");
        sb.AppendLine("Check the container status (docker ps) and the service log for details.");
        sb.AppendLine();
        AlertBody.AppendFooter(sb, now);
        return sb.ToString();
    }
}