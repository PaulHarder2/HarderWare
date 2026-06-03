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

using MetarParser.Data.Entities;

namespace WxReport.Svc;

/// <summary>Outcome of a <see cref="SignificanceGate"/> evaluation.</summary>
/// <param name="Significant">True when at least one criterion tripped (Claude should be called).</param>
/// <param name="FiredCriteria">Human-readable names of the criteria that tripped, for the DEBUG log.  Empty when not significant.</param>
internal readonly record struct SignificanceResult(bool Significant, IReadOnlyList<string> FiredCriteria);

/// <summary>
/// Deterministic significance evaluator for WX-114.  Pure function: no I/O, no
/// state.  See <c>WxServices/DESIGN.md</c> (significance gate) and the WX-114
/// ticket for the criteria table this implements.
/// </summary>
internal static class SignificanceGate
{
    /// <summary>Horizon-tier upper bounds in hours from "now": T1 ≤ 24h, T2 ≤ 48h, T3 ≤ 72h, T4 ≤ 120h.  Blocks beyond 120h are narrative-only and never gate.</summary>
    private static readonly int[] TierUpperBoundHours = [24, 48, 72, 120];

    /// <summary>Freezing point in °F.  A day "has a freeze" when its low is strictly below this; a thaw is declared only when the low is strictly above it — 32 °F itself stays in the prior state (the latent-heat dead band, per forecaster guidance).</summary>
    private const double FreezeDegF = 32.0;

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
        var horizonEnd = nowUtc.AddHours(TierUpperBoundHours[^1]);

        // Disjoint horizons — an empty prior, or a last send so old its blocks no
        // longer overlap the current 120h window — are not "no change": there is a
        // real in-horizon forecast but nothing to compare it against, so treat as
        // significant and let Claude judge. Mirrors ForecastSnapshotBody.MaterialMatch,
        // which treats the same empty/disjoint case as a real change. Guarded by
        // "there are in-horizon current blocks" so a forecast whose only blocks are
        // beyond 120h (nothing to gate on) is still ignored, not forced significant.
        if (AnyInHorizon(current, nowUtc, horizonEnd) && !HasOverlap(prior, current, nowUtc, horizonEnd))
            return new SignificanceResult(true, ["disjoint-horizon"]);

        EvaluateDailyTemperature(prior, current, cfg, nowUtc, horizonEnd, tz, fired);
        EvaluatePerBlockEvents(prior, current, cfg, nowUtc, horizonEnd, fired);

        return new SignificanceResult(fired.Count > 0, fired);
    }

    // ── Per-day temperature: daily high/low magnitude, freeze/thaw, heat crossing ──
    // Daily aggregation (not per-block) because the extended forecast shows one row
    // per local calendar day (WX-112) — a 6-hour block wobble that does not move the
    // day's high or low is not something the recipient ever sees.
    private static void EvaluateDailyTemperature(
        ForecastSnapshotBody prior, ForecastSnapshotBody current,
        SignificanceGateConfig cfg, DateTime nowUtc, DateTime horizonEnd, TimeZoneInfo tz,
        List<string> fired)
    {
        var curDays = DailyHiLoDegF(current, nowUtc, horizonEnd, tz);
        var priDays = DailyHiLoDegF(prior, nowUtc, horizonEnd, tz);

        foreach (var (day, cur) in curDays)
        {
            // A day that rolled into the horizon has no prior counterpart — not news by itself.
            if (!priDays.TryGetValue(day, out var pri))
                continue;

            // Tier from the day's earliest in-horizon block (not local midnight): keeps
            // day tiering consistent with per-block tiering and avoids a DST-fragile
            // local-midnight→UTC conversion.
            int tier = TierOf(cur.FirstStartUtc, nowUtc);
            if (tier < 0)
                continue;

            // Daily high/low magnitude change.
            int delta = PerTier(cfg.TempDeltaDegF, tier);
            if (Math.Abs(cur.Hi - pri.Hi) >= delta || Math.Abs(cur.Lo - pri.Lo) >= delta)
                fired.Add($"temp-delta@T{tier + 1}({day:yyyy-MM-dd})");

            // Freeze ADD (falling through freezing): prior not freezing, now strictly below 32 °F. Always significant.
            if (pri.Lo >= FreezeDegF && cur.Lo < FreezeDegF)
                fired.Add($"freeze-add({day:yyyy-MM-dd})");

            // Thaw (rising out of a freeze): prior freezing, now strictly above 32 °F. Always significant.
            if (pri.Lo < FreezeDegF && cur.Lo > FreezeDegF)
                fired.Add($"thaw({day:yyyy-MM-dd})");

            // Heat-advisory crossing (either direction). Always significant.
            bool priHeat = pri.Hi >= cfg.HeatAdvisoryDegF;
            bool curHeat = cur.Hi >= cfg.HeatAdvisoryDegF;
            if (priHeat != curHeat)
                fired.Add($"heat-cross({day:yyyy-MM-dd})");
        }
    }

    // ── Per-block events: precip occurrence/type, severe, wind advisory + magnitude ──
    private static void EvaluatePerBlockEvents(
        ForecastSnapshotBody prior, ForecastSnapshotBody current,
        SignificanceGateConfig cfg, DateTime nowUtc, DateTime horizonEnd,
        List<string> fired)
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

            int tier = TierOf(cur.StartUtc, nowUtc);
            if (tier < 0)
                continue;
            string at = $"@T{tier + 1}({cur.StartUtc:MM-dd HH}Z)";

            bool priWet = pri.PrecipExpectation != PrecipExpectation.None;
            bool curWet = cur.PrecipExpectation != PrecipExpectation.None;

            // Precip occurrence ADD (dry→wet): all tiers.
            if (!priWet && curWet)
                fired.Add($"precip-add{at}");
            // Precip occurrence REMOVE (wet→dry): near-term only (T1).
            if (priWet && !curWet && tier == 0)
                fired.Add($"precip-remove{at}");

            // Precip type ADD frozen/freezing (snow, sleet, ZR): safety floor, all tiers.
            if (!IsFrozen(pri) && IsFrozen(cur))
                fired.Add($"frozen-add{at}");
            // Precip type downgrade frozen→liquid rain: T1–T2.
            if (IsFrozen(pri) && cur.PrecipPhenomenon == PrecipPhenomenon.Rain && tier <= 1)
                fired.Add($"frozen-downgrade{at}");

            // Severe ADD (onset): safety floor, all tiers.
            if (!pri.SevereFlag && cur.SevereFlag)
                fired.Add($"severe-add{at}");
            // Severe REMOVE (cleared): T1–T3 (T4 is info-only, does not gate).
            if (pri.SevereFlag && !cur.SevereFlag && tier <= 2)
                fired.Add($"severe-remove{at}");

            // Wind reaches advisory (ADD): always significant.
            if (pri.WindKt.Max < cfg.WindAdvisoryKt && cur.WindKt.Max >= cfg.WindAdvisoryKt)
                fired.Add($"wind-advisory-add{at}");
            // Wind drops below advisory (REMOVE): T1–T2.
            if (pri.WindKt.Max >= cfg.WindAdvisoryKt && cur.WindKt.Max < cfg.WindAdvisoryKt && tier <= 1)
                fired.Add($"wind-advisory-remove{at}");
            // Sustained-wind magnitude change: per-tier threshold, all tiers.
            if (Math.Abs(cur.WindKt.Max - pri.WindKt.Max) >= PerTier(cfg.WindDeltaKt, tier))
                fired.Add($"wind-delta{at}");
        }
    }

    /// <summary>A block is frozen/freezing when snow, freezing precipitation, or a rain/snow mix.  Mixed counts as frozen — it carries snow/ice and is de-icing-relevant — so a Rain→Mixed transition trips the safety-floor frozen ADD; only a move to plain Rain is treated as a frozen→liquid downgrade.</summary>
    private static bool IsFrozen(ForecastSnapshotBlock b) =>
        b.PrecipPhenomenon is PrecipPhenomenon.Snow or PrecipPhenomenon.FreezingPrecip or PrecipPhenomenon.Mixed;

    /// <summary>Per-local-day high/low in °F across in-horizon blocks, with the day's earliest block start (for tiering).</summary>
    private static Dictionary<DateOnly, (double Hi, double Lo, DateTime FirstStartUtc)> DailyHiLoDegF(
        ForecastSnapshotBody body, DateTime nowUtc, DateTime horizonEnd, TimeZoneInfo tz)
    {
        var days = new Dictionary<DateOnly, (double Hi, double Lo, DateTime FirstStartUtc)>();
        foreach (var b in body.Blocks)
        {
            if (!InHorizon(b.StartUtc, nowUtc, horizonEnd))
                continue;
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(b.StartUtc, DateTimeKind.Utc), tz);
            var day = DateOnly.FromDateTime(local);
            double hi = CtoF(b.TemperatureCelsius.Max);
            double lo = CtoF(b.TemperatureCelsius.Min);
            if (days.TryGetValue(day, out var cur))
                days[day] = (Math.Max(cur.Hi, hi), Math.Min(cur.Lo, lo), b.StartUtc < cur.FirstStartUtc ? b.StartUtc : cur.FirstStartUtc);
            else
                days[day] = (hi, lo, b.StartUtc);
        }
        return days;
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

    /// <summary>Per-tier threshold with defensive clamping: a misconfigured short array reuses its last element for higher tiers; an empty array yields 0 (so any change is significant — the gate fails toward calling Claude, never toward suppressing).</summary>
    private static int PerTier(int[] arr, int tier) =>
        arr is { Length: > 0 } ? arr[Math.Min(tier, arr.Length - 1)] : 0;

    /// <summary>A block is in the gate's horizon when it has not fully elapsed and starts before the 120h edge.</summary>
    private static bool InHorizon(DateTime startUtc, DateTime nowUtc, DateTime horizonEnd) =>
        startUtc.AddHours(6) > nowUtc && startUtc < horizonEnd;

    /// <summary>Horizon tier (0-based) of a block start, or -1 if beyond the 120h horizon.  A block already in progress (start before now) is tier 0.</summary>
    private static int TierOf(DateTime startUtc, DateTime nowUtc)
    {
        double hours = (startUtc - nowUtc).TotalHours;
        if (hours < 0) return 0;
        for (int t = 0; t < TierUpperBoundHours.Length; t++)
            if (hours < TierUpperBoundHours[t])
                return t;
        return -1;
    }

    private static double CtoF(double celsius) => celsius * 9.0 / 5.0 + 32.0;
}