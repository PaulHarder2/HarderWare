using MetarParser.Data.Entities;

using WxInterp;

using WxServices.Common;

namespace WxReport.Svc;

/// <summary>
/// One local day's high and low (°C) and peak sustained wind (kt), with the block holding that peak. A high is null when
/// the compared blocks hold no afternoon block, and a low when they hold no pre-dawn or morning block (WX-234: a partial
/// day's extreme from the wrong day-part is a pseudo-extreme, shown as a dash in the grid).
/// </summary>
internal readonly record struct DayFigure(double? HiC, double? LoC, int PeakWindKt, DateTime PeakWindStartUtc);

/// <summary>
/// One local day as the recipient was last sent it (<see cref="Published"/>) and as it stands now (<see cref="Now"/>),
/// with the day's first and last compared blocks still to come.
/// </summary>
internal readonly record struct DayComparison(
    DateOnly Day, DayFigure Published, DayFigure Now, DateTime FirstLiveStartUtc, DateTime LastLiveStartUtc);

/// <summary>
/// WX-506: the per-day temperature and wind comparison shared by <see cref="SignificanceGate"/> (whether to wake the
/// reconciler) and <see cref="DeterministicChangeDetector"/> (what the change band reports), so the two cannot
/// disagree about whether a day changed. <see cref="DayCriteria"/> judges the result, once, for both.
///
/// <para>
/// Both sides share the day's past: every block of the prior forecast that has fully elapsed, at its sent values — a
/// new forecast cannot change the past, and a new snapshot often no longer carries this morning's blocks. For the rest of
/// the day, <see cref="DayComparison.Published"/> takes the prior's blocks and <see cref="DayComparison.Now"/> the new
/// forecast's, over the blocks both carry plus any new block inside the prior's span (a gap in the prior is not the
/// horizon's edge, so what fills it can be news). A block past the prior's last block only rolled into the horizon —
/// each model run adds one to day 5 — and is not news by itself (the WX-108 horizon-edge convention); a block the new
/// forecast no longer carries is not a change either. So once the afternoon has passed, today's high can only rise,
/// its low only fall and its peak wind only rise; while the afternoon is still ahead, a lower afternoon can still lower
/// the high.
/// </para>
/// </summary>
internal static class DayFigures
{
    /// <summary>
    /// Compares each local day that has a compared block still to come, starting before <paramref name="horizonEnd"/>.
    /// </summary>
    public static IReadOnlyList<DayComparison> Compare(
        ForecastSnapshotBody prior, ForecastSnapshotBody current, DateTime nowUtc, DateTime horizonEnd, TimeZoneInfo tz)
    {
        if (prior.Blocks.Count == 0)
            return [];
        var priorByStart = new Dictionary<DateTime, ForecastSnapshotBlock>(prior.Blocks.Count);
        foreach (var b in prior.Blocks)
            priorByStart[b.StartUtc] = b;
        var priorLastStart = prior.Blocks.Max(b => b.StartUtc);
        var priorElapsedByDay = prior.Blocks
            .Where(b => Elapsed(b, nowUtc))
            .GroupBy(b => LocalDay(b.StartUtc, tz))
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<DayComparison>();
        foreach (var liveDay in current.Blocks
                     .Where(b => !Elapsed(b, nowUtc) && b.StartUtc < horizonEnd
                                 && (priorByStart.ContainsKey(b.StartUtc) || b.StartUtc < priorLastStart))
                     .GroupBy(b => LocalDay(b.StartUtc, tz))
                     .OrderBy(g => g.Key))
        {
            var live = liveDay.OrderBy(b => b.StartUtc).ToList();
            var past = priorElapsedByDay.GetValueOrDefault(liveDay.Key) ?? [];
            var published = past.Concat(live.Where(b => priorByStart.ContainsKey(b.StartUtc)).Select(b => priorByStart[b.StartUtc])).ToList();
            var now = past.Concat(live).ToList();
            if (published.Count == 0)
                continue;   // the day is all new blocks inside a gap with nothing sent to compare against
            result.Add(new DayComparison(
                liveDay.Key, Aggregate(published, tz), Aggregate(now, tz), live[0].StartUtc, live[^1].StartUtc));
        }
        return result;
    }

    /// <summary>True when the block has fully elapsed at <paramref name="nowUtc"/>; a block in progress has not.</summary>
    internal static bool Elapsed(ForecastSnapshotBlock b, DateTime nowUtc) =>
        b.StartUtc.AddHours(GfsSnapshotBuilder.BlockHours) <= nowUtc;

    private static DayFigure Aggregate(IReadOnlyList<ForecastSnapshotBlock> blocks, TimeZoneInfo tz)
    {
        var hours = blocks.Select(b => (Block: b, Hour: LocalHour(b.StartUtc, tz))).ToList();
        // Earliest block wins a peak-wind tie, so the window names when the peak first arrives.
        var peak = blocks.OrderByDescending(b => b.WindKt.Max).ThenBy(b => b.StartUtc).First();
        return new DayFigure(
            hours.Any(x => DayPartBands.HasAfternoon(x.Hour)) ? blocks.Max(b => b.TemperatureCelsius.Max) : null,
            hours.Any(x => DayPartBands.HasDawnWindow(x.Hour)) ? blocks.Min(b => b.TemperatureCelsius.Min) : null,
            peak.WindKt.Max, peak.StartUtc);
    }

    private static DateOnly LocalDay(DateTime startUtc, TimeZoneInfo tz) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc), tz));

    private static int LocalHour(DateTime startUtc, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(startUtc, DateTimeKind.Utc), tz).Hour;
}

/// <summary>WX-506: a day's temperature criteria, as <see cref="DayCriteria.Temperature"/> judged them.</summary>
/// <param name="Tier">Horizon tier of the day's first compared block still to come.</param>
/// <param name="Delta">The high or low moved by at least the tier's threshold (both sides must have that extreme).</param>
/// <param name="FreezeAdd">The low fell strictly below 32 °F from at or above it (or from no low sent).</param>
/// <param name="Thaw">The low rose strictly above 32 °F from below it.</param>
/// <param name="HeatAdd">The high reached the heat-advisory line from below it (or from no high sent).</param>
/// <param name="HeatEnd">The high fell below the heat-advisory line from at or above it.</param>
/// <param name="HiMoveF">The high's move in °F, 0 when either side has no high.</param>
/// <param name="LoMoveF">The low's move in °F, 0 when either side has no low.</param>
internal readonly record struct TemperatureCriteria(
    int Tier, bool Delta, bool FreezeAdd, bool Thaw, bool HeatAdd, bool HeatEnd, double HiMoveF, double LoMoveF)
{
    /// <summary>True when any criterion fires.</summary>
    public bool Any => Delta || FreezeAdd || Thaw || HeatAdd || HeatEnd;
}

/// <summary>WX-506: a day's peak-wind criteria, as <see cref="DayCriteria.Wind"/> judged them.</summary>
/// <param name="Tier">Horizon tier of <paramref name="WindowStartUtc"/>.</param>
/// <param name="WindowStartUtc">The block holding the new peak, or the published peak when it fell.</param>
/// <param name="AdvisoryAdd">The peak reached the advisory line.</param>
/// <param name="AdvisoryRemove">The peak fell below the advisory line, within the near-term limit.</param>
/// <param name="Delta">The peak moved by at least the tier's threshold.</param>
internal readonly record struct WindCriteria(
    int Tier, DateTime WindowStartUtc, bool AdvisoryAdd, bool AdvisoryRemove, bool Delta, int PublishedPeakKt, int NowPeakKt)
{
    /// <summary>True when any criterion fires.</summary>
    public bool Any => AdvisoryAdd || AdvisoryRemove || Delta;
}

/// <summary>
/// WX-506: the per-day temperature and wind criteria, judged once for <see cref="SignificanceGate"/> and
/// <see cref="DeterministicChangeDetector"/> — the thresholds are the gate's tuned table (WX-114/160).
/// </summary>
internal static class DayCriteria
{
    /// <summary>The day's temperature criteria, or null when its first compared block is beyond the last tier.</summary>
    internal static TemperatureCriteria? Temperature(DayComparison d, SignificanceGateConfig cfg, DateTime nowUtc)
    {
        int tier = ChangeHorizons.TierOf(d.FirstLiveStartUtc, nowUtc);
        if (tier < 0)
            return null;
        double? priHi = F(d.Published.HiC), curHi = F(d.Now.HiC), priLo = F(d.Published.LoC), curLo = F(d.Now.LoC);
        int threshold = ChangeHorizons.PerTier(cfg.TempDeltaDegF, tier);
        double hiMove = priHi is { } ph && curHi is { } ch ? ch - ph : 0;
        double loMove = priLo is { } pl && curLo is { } cl ? cl - pl : 0;
        return new TemperatureCriteria(
            tier,
            Delta: (priHi is not null && curHi is not null && Math.Abs(hiMove) >= threshold)
                || (priLo is not null && curLo is not null && Math.Abs(loMove) >= threshold),
            // Freeze ADD (falling through freezing): prior not freezing, now strictly below 32 °F. Always significant.
            FreezeAdd: curLo < WxThresholds.FreezeDegF && !(priLo < WxThresholds.FreezeDegF),
            // Thaw (rising out of a freeze): prior freezing, now strictly above 32 °F — a threshold crossing, always
            // significant at every tier (frost protection, pipes, travel), not a lazy near-term cessation.
            Thaw: priLo < WxThresholds.FreezeDegF && curLo > WxThresholds.FreezeDegF,
            HeatAdd: curHi >= cfg.HeatAdvisoryDegF && !(priHi >= cfg.HeatAdvisoryDegF),
            HeatEnd: priHi >= cfg.HeatAdvisoryDegF && curHi < cfg.HeatAdvisoryDegF,
            hiMove, loMove);
    }

    /// <summary>The day's peak-wind criteria, or null when the window block is beyond the last tier.</summary>
    internal static WindCriteria? Wind(DayComparison d, SignificanceGateConfig cfg, DateTime nowUtc)
    {
        int cMax = d.Now.PeakWindKt, pMax = d.Published.PeakWindKt;
        var windowStart = cMax >= pMax ? d.Now.PeakWindStartUtc : d.Published.PeakWindStartUtc;
        int tier = ChangeHorizons.TierOf(windowStart, nowUtc);
        if (tier < 0)
            return null;
        return new WindCriteria(
            tier, windowStart,
            AdvisoryAdd: pMax < cfg.WindAdvisoryKt && cMax >= cfg.WindAdvisoryKt,
            AdvisoryRemove: pMax >= cfg.WindAdvisoryKt && cMax < cfg.WindAdvisoryKt && tier <= ChangeHorizons.WindAdvisoryEndingMaxTier,
            Delta: Math.Abs(cMax - pMax) >= ChangeHorizons.PerTier(cfg.WindDeltaKt, tier),
            pMax, cMax);
    }

    private static double? F(double? celsius) => celsius * 9.0 / 5.0 + 32.0;
}

/// <summary>
/// WX-506: the horizon tiers and the near-term limits on a hazard ENDING, shared by <see cref="SignificanceGate"/> and
/// <see cref="DeterministicChangeDetector"/> so both decide alike. Onsets count at every tier; an ending counts only
/// near-term, because a distant hazard lifting is not news (Paul, 2026-09-15: both follow the gate's limits).
/// </summary>
internal static class ChangeHorizons
{
    /// <summary>Highest tier at which a block going from wet to dry counts: the first 24 h.</summary>
    internal const int PrecipEndingMaxTier = 0;

    /// <summary>Highest tier at which frozen or freezing precipitation giving way to another type counts: the first 48 h.</summary>
    internal const int FrozenTypeEndingMaxTier = 1;

    /// <summary>Highest tier at which a severe flag clearing counts: the first 72 h.</summary>
    internal const int SevereEndingMaxTier = 2;

    /// <summary>Highest tier at which sustained wind falling below the advisory line counts: the first 48 h.</summary>
    internal const int WindAdvisoryEndingMaxTier = 1;

    /// <summary>Horizon tier (0-based) of a block start, or -1 beyond the last <see cref="WxThresholds.TierUpperBoundHours"/> bound. A block already in progress (start before now) is tier 0.</summary>
    internal static int TierOf(DateTime startUtc, DateTime nowUtc)
    {
        double hours = (startUtc - nowUtc).TotalHours;
        if (hours < 0) return 0;
        for (int t = 0; t < WxThresholds.TierUpperBoundHours.Length; t++)
            if (hours < WxThresholds.TierUpperBoundHours[t])
                return t;
        return -1;
    }

    /// <summary>Per-tier threshold with defensive clamping: a misconfigured short array reuses its last element for higher tiers; an empty array yields 0 (so any change counts — the gate fails toward calling Claude, never toward suppressing).</summary>
    internal static int PerTier(int[] arr, int tier) =>
        arr is { Length: > 0 } ? arr[Math.Min(tier, arr.Length - 1)] : 0;
}

/// <summary>
/// WX-506: the per-block precipitation and severe criteria — the ones that can prompt an update. <see cref="SignificanceGate"/>
/// fires on them, and <see cref="DeterministicChangeDetector"/> reports a precipitation or severe change only on a block
/// where one fires, so What's Changed names what prompted the update and a smaller change (rain firming from possible to
/// expected, say) is left to the forecast table (Paul, 2026-09-15).
/// </summary>
internal static class BlockCriteria
{
    /// <summary>
    /// The criteria that fire for one block at its horizon <paramref name="tier"/>, by name: <c>precip-add</c>,
    /// <c>precip-remove</c>, <c>frozen-add</c>, <c>frozen-downgrade</c>, <c>severe-add</c>, <c>severe-remove</c>. Empty when
    /// none fires. Onsets fire at every tier; endings only within <see cref="ChangeHorizons"/>' near-term limits.
    /// </summary>
    internal static IReadOnlyList<string> Fired(ForecastSnapshotBlock prior, ForecastSnapshotBlock current, int tier)
    {
        var fired = new List<string>();
        if (tier < 0)
            return fired;

        bool priWet = prior.PrecipExpectation != PrecipExpectation.None;
        bool curWet = current.PrecipExpectation != PrecipExpectation.None;

        // Precip occurrence ADD (dry→wet): all tiers.
        if (!priWet && curWet)
            fired.Add("precip-add");
        // Precip occurrence REMOVE (wet→dry): near-term only.
        if (priWet && !curWet && tier <= ChangeHorizons.PrecipEndingMaxTier)
            fired.Add("precip-remove");

        // Precip type ADD frozen/freezing (snow, sleet, ZR): safety floor, all tiers.
        if (!IsFrozen(prior) && IsFrozen(current))
            fired.Add("frozen-add");
        // Precip type downgrade frozen→liquid rain: near-term only.
        if (IsFrozen(prior) && current.PrecipPhenomenon == PrecipPhenomenon.Rain && tier <= ChangeHorizons.FrozenTypeEndingMaxTier)
            fired.Add("frozen-downgrade");

        // Severe ADD (onset): safety floor, all tiers.
        if (!prior.SevereFlag && current.SevereFlag)
            fired.Add("severe-add");
        // Severe REMOVE (cleared): near-term only.
        if (prior.SevereFlag && !current.SevereFlag && tier <= ChangeHorizons.SevereEndingMaxTier)
            fired.Add("severe-remove");

        return fired;
    }

    /// <summary>A block is frozen/freezing when snow, freezing precipitation, or a rain/snow mix.  Mixed counts as frozen — it carries snow/ice and is de-icing-relevant — so a Rain→Mixed transition trips the safety-floor frozen ADD; only a move to plain Rain is treated as a frozen→liquid downgrade.</summary>
    private static bool IsFrozen(ForecastSnapshotBlock b) =>
        b.PrecipPhenomenon is PrecipPhenomenon.Snow or PrecipPhenomenon.FreezingPrecip or PrecipPhenomenon.Mixed;
}