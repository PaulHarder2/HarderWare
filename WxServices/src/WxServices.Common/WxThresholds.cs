using System.Collections.Immutable;

namespace WxServices.Common;

/// <summary>
/// Fixed meteorological and forecast-engineering constants shared across the
/// WxServices assemblies — the single source of truth for the wind/temperature
/// bright lines the deterministic forecast logic keys off.
///
/// <para>
/// These are <em>definitions</em>, not configuration: a wind of 50 kt is severe by
/// meteorological definition, water freezes at 32 °F, 34 kt is tropical-storm force.
/// They live here, in one place, so no value is defined twice (WX-160 — previously
/// <c>SevereWindKt</c> sat in <c>GfsSnapshotBuilder</c> and was mirrored into
/// <c>SignificanceGateConfig</c>).  The significance gate's <em>tunable</em> knobs —
/// the per-tier delta arrays, the advisory lines, the operating mode — remain on
/// <c>SignificanceGateConfig</c>, bound from <c>Report:SignificanceGate</c> in
/// appsettings, because they are meant to be tuned per deployment without a recompile.
/// </para>
/// </summary>
public static class WxThresholds
{
    /// <summary>Sustained-or-gust wind (kt) that is severe by definition.  Used by the GFS severe-flag derivation (sustained; GFS carries no gust), the TAF→block merge (<c>max(sustained,gust)</c>, WX-160), and the WX-156 severe subject prefix.</summary>
    public const int SevereWindKt = 50;

    /// <summary>Tropical-storm-force sustained wind (kt) — the WX-149 significance-hierarchy safety bright line for non-thunderstorm wind.  Used by the reconciler's Safety-tier backing check.</summary>
    public const int SafetyWindKt = 34;

    /// <summary>Freezing point (°F).  A day "has a freeze" when its low is strictly below this; a thaw is declared only when the low is strictly above it — 32 °F itself stays in the prior state (the latent-heat dead band, per forecaster guidance).  Used by the significance gate's freeze/thaw criteria.</summary>
    public const double FreezeDegF = 32.0;

    /// <summary>Rounding slack (kt) between a block's maximum sustained-wind source and Claude's <c>windKt.max</c> before the max reads as a folded gust (WX-160).  A reported gust exceeds its sustained wind by ~8 kt or more, so this separates honest rounding from a fold.  Used by the reconciler's windKt sustained-ceiling guard.</summary>
    public const int SustainedCeilingToleranceKt = 4;

    /// <summary>Significance-gate horizon-tier upper bounds in hours from "now": T1 ≤ [0], T2 ≤ [1], T3 ≤ [2], T4 ≤ [3].  Blocks beyond the last bound are narrative-only and never gate.  The per-tier threshold arrays on <c>SignificanceGateConfig</c> are indexed against these tiers.  <see cref="ImmutableArray{T}"/> so this shared constant cannot be mutated by a caller in any assembly (a plain <c>static readonly int[]</c> protects only the reference, not its elements).</summary>
    public static readonly ImmutableArray<int> TierUpperBoundHours = [24, 48, 72, 120];
}