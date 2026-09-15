// WX-114 deterministic significance gate.
//
// A cost pre-filter that runs between the WX-80 input-identity pre-filter and the
// Claude reconciliation call.  It compares the current deterministic forecast
// (the GFS-built provisional body) against the last *sent* forecast and reports
// whether anything materially changed.  When nothing has, ReportWorker skips the
// Claude call entirely — that is where the token savings come from.
//
// It can only *suppress* a call, never force a send: significance/sending stays
// with Claude (WX-47-consistent).  The gate errs toward "significant" (call
// Claude) on every boundary case — a wrongly-suppressed send is the only failure
// mode that loses a real update, so the gate is conservative by construction and
// is tightened later using the DEBUG skip-vs-call data it emits.
//
// The `current` body the caller supplies is GFS+TAF merged (WX-160:
// TafBlockProjector overlays the parsed TAF onto the GFS provisional), so the
// gate sees TAF content and judges it with the same threshold table as GFS.
// Earlier this body was GFS-only and the gate was blind to TAF, so it presumed
// every fresh-TAF cycle significant (`taf-fresh`) — a blanket bypass that barely
// filtered, since TAFs reissue routinely.  Feeding it a TAF-aware `current`
// retired that bypass without touching the threshold logic.

using MetarParser.Data.Entities;

using WxServices.Common;

namespace WxReport.Svc;

/// <summary>Outcome of a <see cref="SignificanceGate"/> evaluation.</summary>
/// <param name="Significant">True when at least one criterion tripped (Claude should be called).</param>
/// <param name="FiredCriteria">Human-readable names of the criteria that tripped, for the DEBUG log.  Empty when not significant.</param>
/// <param name="EarliestChangedDayLocal">WX-181: the recipient-local day of the earliest block/day that tripped a criterion — the day-banded update debounce keys its band on this. Null when not significant, or significant-but-uncharacterized (disjoint horizon → no debounce, send).</param>
/// <param name="SevereEntered">WX-181: true when a not-severe→severe onset tripped (severe-add) — lets the change punch through the debounce schedule.</param>
internal readonly record struct SignificanceResult(
    bool Significant, IReadOnlyList<string> FiredCriteria, DateOnly? EarliestChangedDayLocal, bool SevereEntered);

/// <summary>
/// Deterministic significance evaluator for WX-114.  Pure function: no I/O, no
/// state.  See <c>WxServices/DESIGN.md</c> (significance gate) and the WX-114
/// ticket for the criteria table this implements.
/// </summary>
internal static class SignificanceGate
{
    // No magic numbers here: the tunable per-tier delta arrays and advisory lines live
    // on SignificanceGateConfig (bound from appsettings), and the fixed bright lines —
    // the freeze point and the horizon-tier bounds — live in WxThresholds (WX-160).
    // This class holds only the criteria *logic*.

    /// <summary>
    /// Evaluate whether the current deterministic forecast differs materially from
    /// the last sent one.  Blocks are matched by <see cref="ForecastSnapshotBlock.StartUtc"/>;
    /// only blocks within the 0–120h horizon are considered.  A block (or day) that
    /// rolled into the horizon since the last send has no prior counterpart and is
    /// not, by itself, treated as news — matching the WX-108 horizon-edge convention.
    /// </summary>
    /// <param name="prior">The last <em>sent</em> forecast body (what the recipient last saw).</param>
    /// <param name="current">The current cycle's deterministic provisional body.</param>
    /// <param name="cfg">Tunable thresholds.</param>
    /// <param name="nowUtc">Cycle timestamp; tiers are measured from here.</param>
    /// <param name="tz">Recipient timezone, for grouping temperature into local calendar days.</param>
    internal static SignificanceResult Evaluate(
        ForecastSnapshotBody prior, ForecastSnapshotBody current,
        SignificanceGateConfig cfg, DateTime nowUtc, TimeZoneInfo tz)
    {
        ArgumentNullException.ThrowIfNull(prior);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(tz);

        var fired = new List<string>();
        var stats = new ChangeStats();
        var horizonEnd = nowUtc.AddHours(WxThresholds.TierUpperBoundHours[^1]);

        // Disjoint horizons — an empty prior, or a last send so old its blocks no
        // longer overlap the current 120h window — are not "no change": there is a
        // real in-horizon forecast but nothing to compare it against, so treat as
        // significant and let Claude judge. Mirrors ForecastSnapshotBody.MaterialMatch,
        // which treats the same empty/disjoint case as a real change. Guarded by
        // "there are in-horizon current blocks" so a forecast whose only blocks are
        // beyond 120h (nothing to gate on) is still ignored, not forced significant.
        if (AnyInHorizon(current, nowUtc, horizonEnd) && !HasOverlap(prior, current, nowUtc, horizonEnd))
            return new SignificanceResult(true, ["disjoint-horizon"], null, false);

        EvaluateDailyFigures(prior, current, cfg, nowUtc, horizonEnd, tz, fired, stats);
        EvaluatePerBlockEvents(prior, current, cfg, nowUtc, horizonEnd, tz, fired, stats);

        return new SignificanceResult(fired.Count > 0, fired, stats.EarliestDayLocal, stats.SevereEntered);
    }

    /// <summary>
    /// WX-181 accumulator: the earliest recipient-local day among the blocks/days that
    /// tripped a criterion, plus whether a severe onset (not-severe→severe) was one of
    /// them. Drives the day-banded update debounce + its severe punch-through.
    /// </summary>
    private sealed class ChangeStats
    {
        public DateOnly? EarliestDayLocal { get; private set; }
        public bool SevereEntered { get; private set; }

        public void Note(DateOnly dayLocal)
        {
            if (EarliestDayLocal is null || dayLocal < EarliestDayLocal.Value)
                EarliestDayLocal = dayLocal;
        }

        public void NoteSevereEntered() => SevereEntered = true;
    }

    // ── Per-day temperature and wind: daily high/low magnitude, freeze/thaw, heat crossing,
    // peak-wind advisory and magnitude ──
    // Daily aggregation (not per-block) because the extended forecast shows one row
    // per local calendar day (WX-112) — a 6-hour block wobble that does not move the
    // day's high, low or peak wind is not something the recipient ever sees. The
    // published and new day figures come from DayFigures, shared with
    // DeterministicChangeDetector (WX-506): hours already past keep their published values.
    private static void EvaluateDailyFigures(
        ForecastSnapshotBody prior, ForecastSnapshotBody current,
        SignificanceGateConfig cfg, DateTime nowUtc, DateTime horizonEnd, TimeZoneInfo tz,
        List<string> fired, ChangeStats stats)
    {
        foreach (var d in DayFigures.Compare(prior, current, nowUtc, horizonEnd, tz))
        {
            var day = d.Day;
            // The criteria are judged once for the gate and the detector (DayCriteria, WX-506). The temperature tier is
            // the day's earliest compared block still to come (not local midnight), which keeps day tiering consistent
            // with per-block tiering and avoids a DST-fragile local-midnight→UTC conversion.
            if (DayCriteria.Temperature(d, cfg, nowUtc) is { } t)
            {
                if (t.Delta)
                    fired.Add($"temp-delta@T{t.Tier + 1}({day:yyyy-MM-dd})");
                if (t.FreezeAdd)
                    fired.Add($"freeze-add({day:yyyy-MM-dd})");
                if (t.Thaw)
                    fired.Add($"thaw({day:yyyy-MM-dd})");
                if (t.HeatAdd || t.HeatEnd)
                    fired.Add($"heat-cross({day:yyyy-MM-dd})");
                if (t.Any)
                    stats.Note(day);
            }

            // Peak sustained wind: the window block is the one holding the new peak, or the published peak when it fell.
            if (DayCriteria.Wind(d, cfg, nowUtc) is { } w)
            {
                string at = $"@T{w.Tier + 1}({w.WindowStartUtc:MM-dd HH}Z)";
                if (w.AdvisoryAdd)
                    fired.Add($"wind-advisory-add{at}");
                if (w.AdvisoryRemove)
                    fired.Add($"wind-advisory-remove{at}");
                if (w.Delta)
                    fired.Add($"wind-delta{at}");
                if (w.Any)
                    stats.Note(day);
            }
        }
    }

    // ── Per-block events: precip occurrence/type, severe ──
    private static void EvaluatePerBlockEvents(
        ForecastSnapshotBody prior, ForecastSnapshotBody current,
        SignificanceGateConfig cfg, DateTime nowUtc, DateTime horizonEnd, TimeZoneInfo tz,
        List<string> fired, ChangeStats stats)
    {
        var priorByStart = new Dictionary<DateTime, ForecastSnapshotBlock>(prior.Blocks.Count);
        foreach (var b in prior.Blocks)
            priorByStart[b.StartUtc] = b;

        foreach (var cur in current.Blocks)
        {
            if (!InHorizon(cur.StartUtc, nowUtc, horizonEnd))
                continue;
            if (!priorByStart.TryGetValue(cur.StartUtc, out var pri))
                continue; // rolled-in block — not news by itself

            int tier = ChangeHorizons.TierOf(cur.StartUtc, nowUtc);
            if (tier < 0)
                continue;
            string at = $"@T{tier + 1}({cur.StartUtc:MM-dd HH}Z)";
            int before = fired.Count;

            // The criteria are shared with DeterministicChangeDetector (BlockCriteria, WX-506).
            foreach (var criterion in BlockCriteria.Fired(pri, cur, tier))
            {
                fired.Add($"{criterion}{at}");
                if (criterion == "severe-add")
                    stats.NoteSevereEntered();
            }

            if (fired.Count > before)
                stats.Note(DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(cur.StartUtc, DateTimeKind.Utc), tz)));
        }
    }

    /// <summary>True when the body has at least one block within the 0–120h horizon.</summary>
    private static bool AnyInHorizon(ForecastSnapshotBody body, DateTime nowUtc, DateTime horizonEnd)
    {
        foreach (var b in body.Blocks)
            if (InHorizon(b.StartUtc, nowUtc, horizonEnd))
                return true;
        return false;
    }

    /// <summary>True when at least one in-horizon current block shares a <see cref="ForecastSnapshotBlock.StartUtc"/> with the prior body — i.e. there is something to compare.</summary>
    private static bool HasOverlap(ForecastSnapshotBody prior, ForecastSnapshotBody current, DateTime nowUtc, DateTime horizonEnd)
    {
        var priorStarts = new HashSet<DateTime>(prior.Blocks.Count);
        foreach (var b in prior.Blocks)
            priorStarts.Add(b.StartUtc);
        foreach (var c in current.Blocks)
            if (InHorizon(c.StartUtc, nowUtc, horizonEnd) && priorStarts.Contains(c.StartUtc))
                return true;
        return false;
    }

    /// <summary>A block is in the gate's horizon when it has not fully elapsed and starts before the 120h edge.</summary>
    private static bool InHorizon(DateTime startUtc, DateTime nowUtc, DateTime horizonEnd) =>
        startUtc.AddHours(6) > nowUtc && startUtc < horizonEnd;

    private static double CtoF(double celsius) => celsius * 9.0 / 5.0 + 32.0;
}