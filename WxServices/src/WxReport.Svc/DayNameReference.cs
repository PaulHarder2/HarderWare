using System.Globalization;
using System.Text;

using MetarParser.Data.Entities;

namespace WxReport.Svc;

/// <summary>
/// WX-246: builds the per-report <c>day_name_reference</c> block injected into the reconciler's
/// user message, so the free-composed narrative binds each forecast date to the correct localized
/// day name instead of mis-deriving it — the Albanian "Tuesday→Wednesday" wrong-<em>root</em> class
/// caught by the WX-214 QA.
///
/// <para>
/// Day names are <b>deterministic facts</b> sourced from <see cref="CultureInfo"/>, not curated/tone
/// vocabulary that needs human review (unlike the weather terms in <see cref="NarrativeGlossary"/>).
/// So they are computed here at reconcile time, never persisted as <c>LanguageTemplates</c> tokens.
/// The value is the citation (lemma) form; the prompt instructs Claude to <b>inflect it for
/// grammatical agreement</b> with its use in the sentence (WX-246 comment 12875), the same rule the
/// deterministic renderer's day names already follow via <c>CultureInfo</c>. Because both the grid
/// (which renders <c>"ddd"/"dddd"</c> from <c>CultureInfo</c>) and this reference draw from the same
/// source, grid and prose agree by construction.
/// </para>
/// </summary>
public static class DayNameReference
{
    /// <summary>
    /// Emits the reference block mapping each LOCAL date the forecast spans to its day name in every
    /// narrative language, or an empty string when there are no forecast blocks or no languages. The
    /// English date anchor is written in the invariant culture (unambiguous <c>yyyy-MM-dd (ddd)</c>);
    /// the mapped value is the target language's <c>CultureInfo</c> day name.
    /// </summary>
    /// <param name="snapshot">The provisional forecast body; its blocks define the forecast window.</param>
    /// <param name="narrativeLanguages">ISO codes the narrative is composed in (may include <c>en</c>).</param>
    /// <param name="cultureFor">Resolves an ISO code to its <see cref="CultureInfo"/> (e.g. <c>LanguageTemplateStore.CultureFor</c>).</param>
    /// <param name="tz">Locality timezone — a block's LOCAL date is the day the reader sees.</param>
    public static string Build(
        ForecastSnapshotBody snapshot,
        IReadOnlyList<string> narrativeLanguages,
        Func<string, CultureInfo> cultureFor,
        TimeZoneInfo tz)
    {
        if (snapshot.Blocks.Count == 0 || narrativeLanguages.Count == 0)
            return "";

        // The distinct LOCAL dates the forecast spans, earliest first. Blocks carry UTC instants
        // (kind-unspecified, treated as UTC); the reader sees each block on its local calendar day.
        var dates = snapshot.Blocks
            .Select(b => DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(b.StartUtc, DateTimeKind.Utc), tz)))
            .Distinct()
            .OrderBy(d => d)
            .ToList();
        if (dates.Count == 0)
            return "";

        // Date anchors are language-invariant — compute each date's (day-of-week, anchor string) once,
        // then vary only the CultureInfo day word per language (rather than rebuilding the anchor D×L).
        var anchors = dates
            .Select(d =>
            {
                var dt = d.ToDateTime(TimeOnly.MinValue);
                return (dt.DayOfWeek, Anchor: dt.ToString("yyyy-MM-dd (ddd)", CultureInfo.InvariantCulture));
            })
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("day_name_reference (the correct day name for each forecast date, per narrative "
            + "language — use these EXACT day words in the narrative prose, inflected for grammatical "
            + "agreement with their use in the sentence; never name a different day for a date):");
        foreach (var iso in narrativeLanguages)
        {
            var culture = cultureFor(iso);
            var pairs = anchors.Select(a => $"{a.Anchor} = {culture.DateTimeFormat.GetDayName(a.DayOfWeek)}");
            sb.Append("  ").Append(iso).Append(": ").AppendLine(string.Join("; ", pairs));
        }
        return sb.ToString();
    }
}