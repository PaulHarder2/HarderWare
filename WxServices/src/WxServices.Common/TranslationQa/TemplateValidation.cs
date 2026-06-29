using System.Text.RegularExpressions;

namespace WxServices.Common.TranslationQa;

/// <summary>
/// Shared guards for writing a report-vocabulary phrase to <c>LanguageTemplates</c> (WX-233). Used by the
/// WX-219 review tab's copy-to-DB action and the WX-233 vocabulary editor so every write path enforces the
/// same contract; the fuller WX-173 "sneer" validations can join here later.
/// </summary>
public static partial class TemplateValidation
{
    /// <summary>
    /// True when <paramref name="candidate"/> uses exactly the same <em>set</em> of distinct <c>{n}</c>
    /// format placeholders as the English source <paramref name="english"/> — order and repetition are
    /// ignored (a translation may legitimately use <c>{0}</c> once where the source repeats it, or vice
    /// versa). Dropping an index (information loss) or adding one (an out-of-range <c>string.Format</c>
    /// argument) is rejected.
    /// </summary>
    public static bool PlaceholdersMatch(string english, string candidate) =>
        Placeholders(english).SequenceEqual(Placeholders(candidate));

    private static IEnumerable<string> Placeholders(string s) =>
        PlaceholderRegex().Matches(s ?? "").Select(m => m.Value)
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal);

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderRegex();
}