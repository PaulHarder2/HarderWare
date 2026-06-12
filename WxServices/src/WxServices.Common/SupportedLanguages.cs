namespace WxServices.Common;

/// <summary>
/// The set of ISO 639-1 codes for which localized report templates currently exist —
/// the gate WX-166 enforces when enabling a language in WxManager, so a recipient is
/// never assigned a language the renderer cannot actually produce.
/// <para>
/// Interim (WX-166): the codes are a hard-coded constant here, mirroring
/// WxReport.Svc's built-in <c>ReportVocabulary</c> (<c>en</c> final, <c>es</c> draft).
/// WX-167 replaces this with a DB-backed template-existence check as the per-language
/// template strings move into the Languages registry; at that point this constant
/// becomes a lookup over stored templates and the two sources unify.
/// </para>
/// </summary>
public static class SupportedLanguages
{
    /// <summary>ISO 639-1 codes (lower-case) that have localized report templates and may therefore be enabled.</summary>
    public static readonly IReadOnlySet<string> TemplateCodes =
        new HashSet<string>(StringComparer.Ordinal) { "en", "es" };

    /// <summary>
    /// Whether localized report templates exist for <paramref name="isoCode"/>, so the
    /// language may be enabled (moved into the SupportedLanguages set). Case-sensitive:
    /// registry codes are stored lower-case.
    /// </summary>
    public static bool HasTemplates(string isoCode) => TemplateCodes.Contains(isoCode);
}