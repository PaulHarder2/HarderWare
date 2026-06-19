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
/// The noun comes from <see cref="ReportLabels.SevereNounToken"/> resolved against the
/// recipient's <see cref="TemplateSet"/> — the same source the body hazard banner uses —
/// so the subject and the body never name the hazard differently.
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
    /// <param name="templates">The recipient-language template set supplying the localized severe noun.</param>
    public static string? Evaluate(ForecastSnapshotBody body, DateTime nowUtc, TemplateSet templates)
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
        return block is null ? null : templates.Get(ReportLabels.SevereNounToken(block.PrecipPhenomenon));
    }

    /// <summary>
    /// The qualification rule: a block carries <see cref="ForecastSnapshotBlock.SevereFlag"/>. The flag IS the
    /// authoritative severe signal, so the prefix trusts it directly — matching the body hazard banner, which
    /// triggers on <c>SevereFlag</c> alone (<c>StructuredReportRenderer</c>), so subject and body never disagree.
    ///
    /// <para>
    /// History (WX-160, Option A): WX-156 originally split this into a wind branch (sustained ≥ 50 kt qualifies
    /// standalone) and a convective branch (sub-50-kt wind — so the flag could only be CAPE-driven — additionally
    /// requiring <c>PrecipExpectation ≥ Likely</c> to exclude mere instability). That refinement rested on the
    /// premise "sub-50-kt sustained ⇒ SevereFlag is CAPE-driven," which WX-160 broke: a wind of 50 kt or more is
    /// now severe whether <em>sustained or gust</em>, and a gust-severe block carries <c>SevereFlag</c> with
    /// sustained &lt; 50 and possibly no precip — so it matched neither branch and a genuinely severe (gust)
    /// forecast went unmarked in the subject (while the body banner still showed it). We now trust
    /// <c>SevereFlag</c> directly. The only case this broadens is CAPE-severe with only <c>Possible</c> precip,
    /// which WX-156 excluded as instability — acceptable, since CAPE-severe always carries precip (it requires wet
    /// hours) and a flagged-severe block is severe either way; the safe direction for a hazard signal.
    /// </para>
    /// </summary>
    private static bool Qualifies(ForecastSnapshotBlock b) => b.SevereFlag;
}