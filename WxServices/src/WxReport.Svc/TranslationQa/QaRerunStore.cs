using MetarParser.Data;
using MetarParser.Data.Entities;

using Microsoft.EntityFrameworkCore;

namespace WxReport.Svc.TranslationQa;

/// <summary>
/// WX-235 — the pure DB operations behind the <see cref="QaRerunWorker"/>, factored out so the
/// correctness-critical claim and recovery logic can be unit-tested without live Claude/Gemini calls.
/// All three operate on a caller-supplied <see cref="WeatherDataContext"/> and a caller-supplied
/// timestamp (so tests are deterministic).
/// </summary>
public static class QaRerunStore
{
    /// <summary>A row claimed by the worker for execution. <see cref="ClaimedAtUtc"/> is the claim's
    /// <see cref="QaRerunRequest.StartedAtUtc"/> stamp, used to guard the terminal write against a
    /// row that was swept or re-queued mid-run.</summary>
    public sealed record ClaimedRun(long Id, string IsoCode, DateTime ClaimedAtUtc);

    /// <summary>
    /// Find the oldest unclaimed <see cref="QaRerunStatus.Running"/> row and atomically claim it by setting
    /// <see cref="QaRerunRequest.StartedAtUtc"/> — the single UPDATE succeeds only if the row is still
    /// Running-and-unclaimed at the DB, so two workers can never both run the same request. Returns
    /// <see langword="null"/> when there is nothing to claim (or it was claimed concurrently).
    /// </summary>
    public static async Task<ClaimedRun?> TryClaimNextAsync(WeatherDataContext ctx, DateTime nowUtc, CancellationToken ct)
    {
        var candidate = await ctx.QaRerunRequests
            .Where(r => r.Status == QaRerunStatus.Running && r.StartedAtUtc == null)
            .OrderBy(r => r.RequestedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (candidate is null)
            return null;

        var claimed = await ctx.QaRerunRequests
            .Where(r => r.Id == candidate.Id && r.Status == QaRerunStatus.Running && r.StartedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.StartedAtUtc, nowUtc), ct);
        return claimed == 0 ? null : new ClaimedRun(candidate.Id, candidate.IsoCode, nowUtc);
    }

    /// <summary>
    /// Mark any run left <see cref="QaRerunStatus.Running"/> with a claim (<see cref="QaRerunRequest.StartedAtUtc"/>)
    /// older than <paramref name="cutoff"/> as <see cref="QaRerunStatus.Failed"/> — a run orphaned by a service
    /// crash mid-execution. Returns the number recovered. Unclaimed Running rows (StartedAtUtc null) are left
    /// alone for the worker to pick up.
    /// </summary>
    public static async Task<int> SweepStuckAsync(WeatherDataContext ctx, DateTime cutoff, DateTime nowUtc, string reason, CancellationToken ct)
    {
        var stuck = await ctx.QaRerunRequests
            .Where(r => r.Status == QaRerunStatus.Running && r.StartedAtUtc != null && r.StartedAtUtc < cutoff)
            .ToListAsync(ct);
        if (stuck.Count == 0)
            return 0;
        foreach (var r in stuck)
        {
            r.Status = QaRerunStatus.Failed;
            r.Error = reason;
            r.CompletedAtUtc = nowUtc;
        }
        await ctx.SaveChangesAsync(ct);
        return stuck.Count;
    }

    /// <summary>
    /// Record the terminal state (<see cref="QaRerunStatus.Succeeded"/> + stamp, or <see cref="QaRerunStatus.Failed"/>
    /// + error) on a claimed row — but only if it is still the run we claimed: still
    /// <see cref="QaRerunStatus.Running"/> with the same <see cref="QaRerunRequest.StartedAtUtc"/> as
    /// <paramref name="claimedAtUtc"/>. If the row was swept to Failed or re-queued by the operator while the
    /// run was in flight, the stale completion is a no-op rather than clobbering the newer state.
    /// </summary>
    public static async Task CompleteAsync(WeatherDataContext ctx, long id, DateTime claimedAtUtc, QaRerunStatus status, string? resultStamp, string? error, DateTime nowUtc, CancellationToken ct)
    {
        var row = await ctx.QaRerunRequests
            .FirstOrDefaultAsync(r => r.Id == id && r.Status == QaRerunStatus.Running && r.StartedAtUtc == claimedAtUtc, ct);
        if (row is null)
            return;
        row.Status = status;
        row.ResultStamp = resultStamp;
        row.Error = error;
        row.CompletedAtUtc = nowUtc;
        await ctx.SaveChangesAsync(ct);
    }
}