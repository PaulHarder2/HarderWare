using MetarParser.Data.Entities;

namespace WxReport.Svc;

/// <summary>
/// WX-284: the recipient-facing precipitation collapse — both the phenomenon axis (<see cref="Of"/>)
/// and the likelihood axis (<see cref="Expectation"/>) — in one place so every surface that shows the
/// reader precipitation agrees on what it shows. Most recipients don't distinguish
/// rain / showers / a non-severe thunderstorm — they all read as ordinary <b>"rain"</b>; storm
/// and severe wording is reserved for a <see cref="ForecastSnapshotBlock.SevereFlag"/> block
/// (the rain vs. severe-storms binary). The frozen track stays distinct: this collapses the
/// convective-intensity gradient, never precip <em>type</em> (Snow / Mixed / FreezingPrecip are
/// untouched).
///
/// <para>
/// The deterministic renderer (<see cref="StructuredReportRenderer"/>), the change detector
/// (<see cref="DeterministicChangeDetector"/>), and its consistency oracle
/// (<c>ForecastReconciler.ValidateChangeSnapshotConsistency</c>) must classify a block the same
/// way, or the detector/oracle tautology (WX-189/WX-151) breaks — the detector would emit a Rain
/// change the oracle, still keying off the raw Thunderstorm phenomenon, would reject as a phantom.
/// This rule is that single classification.
/// </para>
/// </summary>
internal static class RecipientPrecip
{
    /// <summary>
    /// The phenomenon the recipient is shown for <paramref name="b"/>: a <b>non-severe</b>
    /// thunderstorm collapses to <see cref="PrecipPhenomenon.Rain"/>; a <b>severe</b> thunderstorm
    /// keeps <see cref="PrecipPhenomenon.Thunderstorm"/> so the rain → severe-storms escalation
    /// still surfaces. Every other phenomenon (including a null/no-precip block) is returned
    /// unchanged.
    /// </summary>
    internal static PrecipPhenomenon? Of(ForecastSnapshotBlock b) =>
        b.PrecipPhenomenon is PrecipPhenomenon.Thunderstorm && !b.SevereFlag
            ? PrecipPhenomenon.Rain
            : b.PrecipPhenomenon;

    /// <summary>
    /// The likelihood tier the recipient is shown for <paramref name="e"/>: WX-284 step 2 collapses
    /// <see cref="PrecipExpectation.Likely"/> into <see cref="PrecipExpectation.Possible"/> because
    /// "possible" and "likely" read as the same register to most recipients (Niki's observation), and
    /// "possible" is the surviving hedge word (Paul) — the word "likely" never reaches the reader.
    /// <see cref="PrecipExpectation.None"/> (dry) and <see cref="PrecipExpectation.Certain"/> (the
    /// higher-confidence "expected" tier) are unchanged — so the reader sees three tiers, dry /
    /// "possible" / "expected", and a possible &lt;-&gt; likely move is <em>not</em> a change worth an
    /// unscheduled update. The renderer, change detector, and consistency oracle all fold through this
    /// (exactly as the phenomenon axis folds through <see cref="Of"/>), keeping the WX-189/151
    /// detector/oracle tautology green.
    /// </summary>
    internal static PrecipExpectation Expectation(PrecipExpectation e) =>
        e == PrecipExpectation.Likely ? PrecipExpectation.Possible : e;

    /// <summary>
    /// The recipient likelihood tier for a whole block, used by the change detector and its oracle. A
    /// non-severe block folds likely &#8594; possible (<see cref="Expectation(PrecipExpectation)"/>). A
    /// <b>severe</b> block is <b>pinned to the top of the ladder</b> (<see cref="PrecipExpectation.Certain"/>):
    /// its recipient wording is the constant "severe storms/weather possible" (never likely/expected — see
    /// <see cref="StructuredReportRenderer"/>), so its internal expectation tier must not drive a change —
    /// a block that stays severe while its tier firms is not something the reader sees change. Pinning to
    /// the TOP (not Possible) keeps the severe &#8596; non-severe direction correct: severe onset stays an
    /// "up", severe clearing a "down", and a severe-both-sides tier bump reads flat. The severe onset/clearing
    /// itself is carried by the separate <c>SevereFlag</c> axis (the detector's <c>SevereOf</c>/newSevere),
    /// independent of this tier — so pinning the tier removes only the phantom, never a real escalation.
    /// </summary>
    internal static PrecipExpectation Expectation(ForecastSnapshotBlock b) =>
        b.SevereFlag ? PrecipExpectation.Certain : Expectation(b.PrecipExpectation);
}