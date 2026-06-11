// WX-160 deterministic TAF→block projection for the significance gate.
//
// The WX-114 gate compares the current forecast against the last *sent* one and
// suppresses the Claude call when nothing material changed. Its provisional body
// is built from GFS only, so the gate was structurally blind to TAF content and
// took a blanket shortcut: any fresh TAF presumed the cycle significant
// (`taf-fresh`). TAFs reissue routinely, so that shortcut barely filtered — it
// just deferred every TAF refresh to Claude (WX-160 motivation).
//
// This projector removes the blindness instead of papering over it. It overlays
// the parsed TAF onto the GFS provisional body, block by block, producing a
// merged GFS+TAF body the gate can evaluate with its existing threshold table —
// the SAME logic, now fed an honest input. Within a TAF's coverage the TAF
// prevails (matching the reconciler's "TAF prevails within its validity window"
// rule and therefore the stored baseline's provenance); outside it, GFS stands.
//
// Wind is SUSTAINED only (WX-160 forecaster rule): gust never enters windKt. The
// one exception is severe — a wind of 50 kt or higher is severe by definition,
// gust OR sustained — so the merged severeFlag ORs the GFS severe flag with the
// 50-kt (sustained-or-gust) rule. Temperature always stays GFS (a TAF carries no
// temperature); sky stays GFS (the gate does not read sky).
//
// The projection errs toward "significant" on every ambiguity, consistent with
// the gate's err-toward-Claude design: a wrongly-suppressed send is the only
// failure mode that loses a real update.

using MetarParser.Data.Entities;

using WxInterp;

using WxServices.Common;

namespace WxReport.Svc;

/// <summary>
/// Deterministic merge of a parsed TAF (<see cref="ForecastPeriod"/> groups) onto a
/// GFS-only provisional <see cref="ForecastSnapshotBody"/>, for the WX-114
/// significance gate (WX-160).  Pure function: no I/O, no state.
/// </summary>
internal static class TafBlockProjector
{
    /// <summary>
    /// Overlay <paramref name="tafPeriods"/> onto <paramref name="gfsProvisional"/>,
    /// returning a merged body for gate comparison.  A block the TAF does not cover
    /// is returned unchanged; a covered block keeps its GFS temperature, sky, and
    /// obscuration but takes its sustained wind, precipitation, and severe signal
    /// from the TAF (severe OR-ed with the GFS flag).  Returns the provisional
    /// unchanged when there is no TAF to project.
    /// </summary>
    /// <param name="gfsProvisional">The GFS-only provisional body (the gate's former "current").</param>
    /// <param name="tafPeriods">The parsed TAF change groups (<c>snapshot.ForecastPeriods</c>), in TAF order.</param>
    /// <param name="tafValidToUtc">End of the TAF's validity window (<c>snapshot.TafValidToUtc</c>).  Coverage is clamped here so the projection never extends past the TAF — blocks beyond it (a TAF is ~24-30h; the gate horizon is 120h) keep their live GFS forecast rather than inheriting the expired TAF's last group.</param>
    public static ForecastSnapshotBody Merge(
        ForecastSnapshotBody gfsProvisional, IReadOnlyList<ForecastPeriod> tafPeriods,
        DateTime? tafValidToUtc)
    {
        ArgumentNullException.ThrowIfNull(gfsProvisional);

        if (tafPeriods is null || tafPeriods.Count == 0)
            return gfsProvisional;

        // The TAF says nothing past its validity end; clamp coverage there so a far
        // block (well beyond a ~24-30h TAF, out to the 120h horizon) is never
        // overridden by the expired last prevailing group. Null → no clamp (defensive;
        // a non-empty TAF always carries a validity end).
        var validTo = tafValidToUtc ?? DateTime.MaxValue;

        // Prevailing groups (BASE/FM/BECMG) define a timeline; each is active from
        // its start until the next prevailing group starts (BECMG conditions prevail
        // from the group's ValidFrom, per the WX-160 mapping), the last running to the
        // TAF validity end. Overlay groups (TEMPO/PROB) are active only within their
        // own explicit window.
        var prevailing = BuildPrevailingTimeline(tafPeriods, validTo);
        var overlays = tafPeriods
            .Where(p => IsOverlay(p.ChangeType) && p.ValidFromUtc.HasValue && p.ValidToUtc.HasValue)
            .ToList();

        var mergedBlocks = new List<ForecastSnapshotBlock>(gfsProvisional.Blocks.Count);
        foreach (var block in gfsProvisional.Blocks)
        {
            var blockEnd = block.StartUtc.AddHours(GfsSnapshotBuilder.BlockHours);

            // Every TAF group whose resolved window overlaps the block. Max-across-
            // overlapping is the approved aggregation: a TEMPO/PROB fluctuation
            // counts toward the block's worst case, never softens it.
            var cover = new List<ForecastPeriod>();
            foreach (var (period, from, to) in prevailing)
                if (Overlaps(from, to, block.StartUtc, blockEnd))
                    cover.Add(period);
            foreach (var ov in overlays)
                if (Overlaps(ov.ValidFromUtc!.Value, ov.ValidToUtc!.Value, block.StartUtc, blockEnd))
                    cover.Add(ov);

            if (cover.Count == 0)
            {
                // The TAF says nothing about this block (beyond its validity, or a
                // gap): GFS stands.
                mergedBlocks.Add(block);
                continue;
            }

            mergedBlocks.Add(MergeBlock(block, cover));
        }

        return new ForecastSnapshotBody { Blocks = mergedBlocks };
    }

    /// <summary>Build one merged block from a GFS block and the TAF groups that cover it.</summary>
    private static ForecastSnapshotBlock MergeBlock(ForecastSnapshotBlock gfs, List<ForecastPeriod> cover)
    {
        // ── sustained wind (gust deliberately excluded) ──
        // TAF prevails in its window, so sustained wind comes from the TAF when it
        // states one; fall back to the GFS block only when no covering group reports
        // wind. Max-across-overlapping per the approved rule.
        var sustained = cover.Where(p => p.WindSpeedKt.HasValue).Select(p => p.WindSpeedKt!.Value).ToList();
        var windKt = sustained.Count > 0
            ? new MinMax<int>(sustained.Min(), sustained.Max())
            : gfs.WindKt;

        // ── severe: GFS flag OR the 50-kt (sustained-or-gust) rule ──
        // The GFS flag carries convective (CAPE) severe the TAF cannot see, so it is
        // preserved; the TAF additionally supplies gust, the only place gust touches
        // a gate decision.
        int peakWind = 0;
        foreach (var p in cover)
            peakWind = Math.Max(peakWind, Math.Max(p.WindSpeedKt ?? 0, p.WindGustKt ?? 0));
        bool severe = gfs.SevereFlag || peakWind >= WxThresholds.SevereWindKt;

        // ── precipitation ──
        var (precipExpectation, phenomenon) = DerivePrecip(cover);

        return new ForecastSnapshotBlock
        {
            StartUtc = gfs.StartUtc,
            SkyState = gfs.SkyState,            // gate does not read sky; keep GFS
            Obscuration = gfs.Obscuration,
            TemperatureCelsius = gfs.TemperatureCelsius,  // a TAF has no temperature
            WindKt = windKt,
            PrecipExpectation = precipExpectation,
            PrecipPhenomenon = phenomenon,
            SevereFlag = severe,
        };
    }

    /// <summary>
    /// Derive a block's precipitation from its covering TAF groups.  A prevailing
    /// group with precip is a confident call (<see cref="PrecipExpectation.Likely"/>);
    /// an overlay (TEMPO/PROB) alone is <see cref="PrecipExpectation.Possible"/>.  No
    /// precip in any covering group means the TAF forecasts dry — and the TAF
    /// prevails, so that overrides any GFS wet signal for the block.  The gate keys
    /// only on none-vs-not and on the phenomenon, so the Likely/Possible split is
    /// advisory; the phenomenon is frozen-leaning so the safety-floor frozen-add
    /// criterion never under-fires.
    /// </summary>
    private static (PrecipExpectation, PrecipPhenomenon?) DerivePrecip(List<ForecastPeriod> cover)
    {
        bool prevailingWet = false;
        bool overlayWet = false;
        bool hasThunder = false, hasFreezing = false, hasFrozenPrecip = false, hasLiquid = false;

        foreach (var p in cover)
        {
            foreach (var w in p.WeatherPhenomena)
            {
                if (w.IsRecent) continue;                  // recent-weather group, not a forecast
                if (w.Precipitation.Count == 0 && w.Descriptor != WeatherDescriptor.Thunderstorm)
                    continue;                              // obscuration-only (fog/haze) is not precip

                if (IsOverlay(p.ChangeType)) overlayWet = true; else prevailingWet = true;

                if (w.Descriptor == WeatherDescriptor.Thunderstorm) hasThunder = true;
                if (w.Descriptor == WeatherDescriptor.Freezing) hasFreezing = true;
                foreach (var precip in w.Precipitation)
                {
                    if (IsFrozenPrecip(precip)) hasFrozenPrecip = true;
                    if (precip is PrecipitationType.Rain or PrecipitationType.Drizzle) hasLiquid = true;
                }
            }
        }

        if (!prevailingWet && !overlayWet)
            return (PrecipExpectation.None, null);

        // Phenomenon priority mirrors the GFS builder's intent: convection first,
        // then the frozen family (so frozen-add stays a safety floor), then liquid.
        PrecipPhenomenon phenomenon =
            hasThunder ? PrecipPhenomenon.Thunderstorm
            : hasFreezing ? PrecipPhenomenon.FreezingPrecip
            : (hasFrozenPrecip && hasLiquid) ? PrecipPhenomenon.Mixed
            : hasFrozenPrecip ? PrecipPhenomenon.Snow
            : PrecipPhenomenon.Rain;

        var expectation = prevailingWet ? PrecipExpectation.Likely : PrecipExpectation.Possible;
        return (expectation, phenomenon);
    }

    /// <summary>Frozen/ice precipitation types — the de-icing-relevant ones the gate's frozen-add floor cares about.</summary>
    private static bool IsFrozenPrecip(PrecipitationType t) =>
        t is PrecipitationType.Snow or PrecipitationType.SnowGrains
            or PrecipitationType.IceCrystals or PrecipitationType.IcePellets;

    /// <summary>
    /// Resolve BASE/FM/BECMG groups into a prevailing timeline of (period, from, to)
    /// windows.  Each prevailing group is active from its start until the next
    /// prevailing group's start; the last runs to <paramref name="validTo"/> (the TAF
    /// validity end), never open-ended.  A null ValidFrom (an unbounded BASE) is
    /// treated as the earliest instant so it covers everything before the first dated
    /// change.  Every window is capped at <paramref name="validTo"/> so no group's
    /// state is projected past the TAF.
    /// </summary>
    private static List<(ForecastPeriod Period, DateTime From, DateTime To)> BuildPrevailingTimeline(
        IReadOnlyList<ForecastPeriod> tafPeriods, DateTime validTo)
    {
        var prevailing = tafPeriods
            .Where(p => !IsOverlay(p.ChangeType))
            .OrderBy(p => p.ValidFromUtc ?? DateTime.MinValue)
            .ToList();

        var timeline = new List<(ForecastPeriod, DateTime, DateTime)>(prevailing.Count);
        for (int i = 0; i < prevailing.Count; i++)
        {
            var from = prevailing[i].ValidFromUtc ?? DateTime.MinValue;
            // Active until the next prevailing group starts; the last runs to the TAF
            // validity end. An explicit ValidTo (BASE/BECMG carry one) bounds it only
            // if it falls before the next group — otherwise the conditions persist (a
            // BECMG's new state prevails past the transition window). Capped at validTo
            // so an expired group never overrides a far GFS block.
            var nextStart = i + 1 < prevailing.Count
                ? (prevailing[i + 1].ValidFromUtc ?? validTo)
                : validTo;
            var to = nextStart < validTo ? nextStart : validTo;
            timeline.Add((prevailing[i], from, to));
        }
        return timeline;
    }

    /// <summary>TEMPO/PROB groups are transient overlays; BASE/FM/BECMG are prevailing.</summary>
    private static bool IsOverlay(ForecastChangeType t) => t is
        ForecastChangeType.Temporary
        or ForecastChangeType.Probability30 or ForecastChangeType.Probability40
        or ForecastChangeType.Probability30Temporary or ForecastChangeType.Probability40Temporary;

    /// <summary>Half-open interval overlap: [aFrom, aTo) ∩ [bFrom, bTo) ≠ ∅.</summary>
    private static bool Overlaps(DateTime aFrom, DateTime aTo, DateTime bFrom, DateTime bTo) =>
        aFrom < bTo && bFrom < aTo;
}