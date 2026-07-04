using System.Text;

namespace WxReport.Svc;

/// <summary>
/// WX-238 — builds the approved-vocabulary glossary injected into the forecast-reconciler prompt
/// so the free-composed per-language narrative (WX-128) anchors on the curated
/// <see cref="MetarParser.Data.Entities.LanguageTemplate"/> terms instead of free-generating a
/// synonym (e.g. es "llovizna engelante" for the approved "llovizna helada"). The concepts to
/// anchor are the <see cref="LanguageTemplateStore.GlossaryTokens"/> (the language-neutral
/// PromptGlossaryTokens); the per-language approved phrases come from the same store.
///
/// English is anchored too **when it is among the requested narrative languages** — the QA runner
/// always requests it, and in production it is present whenever a locality has an English recipient.
/// The English narrative free-generates as well, and the QA judge back-translates each target
/// against the English reference, so anchoring only the targets would let the reference itself
/// drift out from under the comparison.
///
/// Pure/deterministic: no I/O, no Claude call. Placed in the reconciler's per-report (uncached)
/// system block, so it costs only its own tokens and breaks no prompt caching.
/// </summary>
public static class NarrativeGlossary
{
    /// <summary>
    /// The glossary text for <paramref name="narrativeLanguages"/>, or an empty string when there
    /// are no glossary tokens, no languages, or nothing resolves. Each language lists its approved
    /// term per concept as <c>english concept → «approved term»</c> (for English the concept and
    /// the approved term coincide — an identity anchor that still pins the English wording).
    /// </summary>
    public static string Build(LanguageTemplateStore templates, IReadOnlyList<string> narrativeLanguages)
    {
        var tokens = templates.GlossaryTokens;
        if (tokens.Count == 0 || narrativeLanguages.Count == 0)
            return "";

        var ordered = tokens.OrderBy(t => t, StringComparer.Ordinal).ToList();
        // English is both the concept label for every language and its own anchor. Its completeness
        // is guaranteed upstream by the startup Tok.Required completeness check + the build-time parity
        // gate (a missing en phrase is a
        // loud startup ERROR, not a silent gap here), so using it as the per-concept label is safe.
        var en = templates.PhrasesFor("en");

        var langLines = new List<string>();
        foreach (var iso in narrativeLanguages)
        {
            var target = templates.PhrasesFor(iso);
            var pairs = new List<string>();
            foreach (var token in ordered)
            {
                // Anchor a concept only when BOTH the English label and the target phrase resolve;
                // a blocked/missing term has no approved phrase to hold the wording to.
                if (en.TryGetValue(token, out var enPhrase)
                    && !string.IsNullOrWhiteSpace(enPhrase)
                    && target.TryGetValue(token, out var targetPhrase)
                    && !string.IsNullOrWhiteSpace(targetPhrase))
                {
                    pairs.Add($"{enPhrase} → «{targetPhrase}»");
                }
            }
            if (pairs.Count > 0)
                // Canonicalize the label so it matches the key the store/renderer resolve against,
                // even if the caller ever passes a regional/mixed-case tag (e.g. "es-US" → "es").
                langLines.Add($"  {LanguageTemplateStore.CanonicalIso(iso)}: {string.Join("; ", pairs)}");
        }

        if (langLines.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.Append("Approved vocabulary for this report — when the narrative describes one of these ");
        sb.Append("meteorological or time-of-day concepts, use this approved wording for that language (inflected as the ");
        sb.Append("grammar requires and capitalized for its position; do not substitute a synonym):\n");
        sb.Append(string.Join("\n", langLines));
        return sb.ToString();
    }
}