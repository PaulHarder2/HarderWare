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
    /// True when <paramref name="candidate"/> preserves exactly the <c>{n}</c> format placeholders of the
    /// English source <paramref name="english"/> — same set, ignoring order/repetition position. A phrase
    /// that drops or adds a placeholder would break the renderer's <c>string.Format</c> contract.
    /// </summary>
    public static bool PlaceholdersMatch(string english, string candidate) =>
        Placeholders(english).SequenceEqual(Placeholders(candidate));

    private static IEnumerable<string> Placeholders(string s) =>
        PlaceholderRegex().Matches(s ?? "").Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal);

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderRegex();
}