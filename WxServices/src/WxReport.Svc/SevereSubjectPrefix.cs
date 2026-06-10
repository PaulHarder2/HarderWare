using MetarParser.Data.Entities;

using WxInterp;

namespace WxReport.Svc;

/// <summary>
/// WX-156: the deterministic severe-weather subject prefix (no LLM in the decision).
/// Returns the localized severe phenomenon noun — "Severe storms" / "Severe weather"
/// (En), "Tormentas severas" / "Tiempo severo" (Es) — when a forecast block carrying
/// <see cref="ForecastSnapshotBlock.SevereFlag"/> falls in the next 24 h and meets the
/// WX-156 rule, otherwise <c>null</c> (no prefix).
///
/// <para>
/// The hazard salience this adds to the subject is a separate axis from
/// <see cref="ReportKind"/> (provenance); it rides on top of the report type word.
/// The noun comes from <see cref="ReportVocabulary.SevereNoun"/> — the same source the
/// body hazard banner uses — so the subject and the body never name the hazard differently.
/// </para>
///
/// <para>
/// Hysteresis is inherited implicitly (WX-156, Option 1): the prefix is a pure function
/// of the final snapshot, and the every-cycle whipsaw vector — unscheduled sends — is
/// already gated by <c>EvaluateUnscheduledSuppression</c> (a severe de-escalation on an
/// observation-only advance does not send, so no whipsaw email). Computing the prefix from
/// the same snapshot the body renders also keeps the subject and body coherent (the WX-153
/// anti-drift goal): we never claim severe in the subject while the body shows none.
/// </para>
/// </summary>
internal static class SevereSubjectPrefix
{
    /// <summary>The look-ahead window: a severe block must overlap <c>[now, now + 24 h]</c> to surface in the subject.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>
    /// The localized severe noun if the WX-156 rule holds for any block overlapping the next 24 h,
    /// else <c>null</c>. The noun follows the earliest qualifying block, matching the body banner's choice.
    /// </summary>
    /// <param name="body">The reconciled forecast snapshot whose blocks are inspected.</param>
    /// <param name="nowUtc">The current instant (UTC); the window is <c>[nowUtc, nowUtc + 24 h]</c>.</param>
    /// <param name="vocab">The recipient-language vocabulary supplying the severe noun.</param>
    public static string? Evaluate(ForecastSnapshotBody body, DateTime nowUtc, ReportVocabulary vocab)
    {
        var horizon = nowUtc.Add(Window);
        var block = body.Blocks
            .Where(b =>
            {
                var startUtc = DateTime.SpecifyKind(b.StartUtc, DateTimeKind.Utc);
                return Qualifies(b) && startUtc.AddHours(GfsSnapshotBuilder.BlockHours) > nowUtc && startUtc < horizon;
            })
            .OrderBy(b => b.StartUtc)
            .FirstOrDefault();
        return block is null ? null : vocab.SevereNoun(block.PrecipPhenomenon);
    }

    /// <summary>
    /// The WX-156 qualification rule. A block must carry <see cref="ForecastSnapshotBlock.SevereFlag"/> and then
    /// satisfy one of two branches:
    /// <list type="bullet">
    /// <item><b>Wind</b> — sustained wind ≥ <see cref="GfsSnapshotBuilder.SevereWindKt"/>: qualifies standalone.</item>
    /// <item><b>Convective</b> — wind below that threshold (so <c>SevereFlag</c> can only be CAPE-driven, since blocks
    /// carry no CAPE): additionally requires <c>PrecipExpectation ≥ Likely</c> (storms expected, not mere instability).</item>
    /// </list>
    /// Excludes snow / freezing / fog / sub-severe (34–49 kt) wind — <c>SevereFlag</c> already excludes them.
    ///
    /// <para>
    /// The block persists wind as a rounded <c>int</c> (<c>GfsSnapshotBuilder</c> rounds the raw max away from zero),
    /// while <c>DeriveSevereFlag</c> set <see cref="ForecastSnapshotBlock.SevereFlag"/> from the raw float. So a
    /// CAPE-severe block whose raw wind is 49.5–49.99 kt rounds to a stored 50 and takes the wind branch, skipping
    /// the convective gate. The raw wind isn't available here to disambiguate; the discrepancy is a sub-1-kt band and
    /// errs toward over-warning (the block is severe either way), which is the safe direction for a hazard signal.
    /// </para>
    /// </summary>
    private static bool Qualifies(ForecastSnapshotBlock b) =>
        b.SevereFlag
        && (b.WindKt.Max >= GfsSnapshotBuilder.SevereWindKt
            || b.PrecipExpectation >= PrecipExpectation.Likely);
}