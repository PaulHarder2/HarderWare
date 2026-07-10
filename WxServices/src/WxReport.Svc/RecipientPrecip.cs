using MetarParser.Data.Entities;

namespace WxReport.Svc;

/// <summary>
/// WX-284: the recipient-facing precipitation collapse, in one place so every surface that
/// shows the reader a phenomenon agrees on what it shows. Most recipients don't distinguish
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
}