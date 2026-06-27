using System.Text;
using System.Text.Json;

namespace WxReport.Tools.TranslationQa;

/// <summary>One scenario's rendered report pair — the English reference and the target rendering.</summary>
public sealed record RenderedScenario(string Name, string Synopsis, string EnglishHtml, string TargetHtml);

/// <summary>
/// One controlled-vocabulary token, its English source paired with the target-language rendering and the
/// generation metadata the judge needs to assess the term in context.
/// </summary>
public sealed record VocabularyPair(
    string Token,
    string EnglishPhrase,
    string EnglishContext,
    string ContextKind,
    string TargetPhrase,
    string TargetContext,
    bool Representable,
    string? Note,
    bool Reviewed);

/// <summary>
/// WX-217 — assembles the judging request: the single self-describing document an independent (non-Claude)
/// model is handed to audit a target language. It bundles an instruction preamble (the asks + the exact
/// response shape so WX-218 can parse the reply + the advisory-only framing), the rendered reports
/// (English reference + target, both scenarios), and the paired controlled vocabulary as JSON.
///
/// Pure assembly: deterministic given its inputs, no Claude call, no I/O. The harness gathers the inputs
/// (WX-216's rendered HTML + a DB read) and writes the result.
/// </summary>
public static class JudgingPayload
{
    /// <summary>Build the request document for one target language.</summary>
    public static string Build(
        string targetIso,
        string? targetDisplayName,
        IReadOnlyList<RenderedScenario> scenarios,
        IReadOnlyList<VocabularyPair> vocabulary)
    {
        var langLabel = string.IsNullOrWhiteSpace(targetDisplayName) ? targetIso : $"{targetDisplayName} ({targetIso})";
        var blocked = vocabulary.Count(v => !v.Representable);

        var sb = new StringBuilder();

        sb.Append("# Translation-QA judging request — ").Append(langLabel).Append('\n');
        sb.Append('\n');
        sb.Append("You are an independent reviewer fluent in ").Append(langLabel)
          .Append(". You are auditing an automatically generated weather-report translation. The reports below were produced by a *different* AI; your job is a fresh, skeptical second opinion — not to assume it got things right.\n\n");

        // ── What's in this request ────────────────────────────────────────────────────────────────
        sb.Append("## What you are given\n\n");
        sb.Append("1. **Rendered reports** — for each of ").Append(scenarios.Count)
          .Append(" weather scenarios, the **English reference** report and its **").Append(langLabel)
          .Append("** rendering, as the HTML a recipient receives. The two say the same thing; the target is the one under audit.\n");
        sb.Append("2. **Controlled vocabulary** — the ").Append(vocabulary.Count)
          .Append(" atomic terms the report renderer substitutes, each as `englishPhrase` ↔ `targetPhrase` with the usage `context` it was translated under. ");
        if (blocked > 0)
            sb.Append(blocked).Append(" term(s) are marked `representable: false` (the generator could not fill the slot with a simple phrase — see the `note`). ");
        sb.Append("Judge each term in its context, not in isolation.\n\n");

        // ── The asks ──────────────────────────────────────────────────────────────────────────────
        sb.Append("## What to do\n\n");
        sb.Append("1. **Back-translate** each ").Append(langLabel).Append(" report back to natural English, faithfully (do not silently \"repair\" awkward source — translate what is actually there).\n");
        sb.Append("2. **Flag report-level problems**: any passage that reads awkwardly, ungrammatically, or means something different from the English reference — with its location, the problem, and a suggested fix.\n");
        sb.Append("3. **Judge each vocabulary term** for accuracy and naturalness in its context; where it is wrong or clumsy, suggest a better term.\n");
        sb.Append("4. **Report your own confidence** — how fluent you are in this language and how much to trust this review (be honest; lower confidence for low-resource languages is useful, not a failure).\n\n");

        // ── Response shape (WX-218 parses this) ─────────────────────────────────────────────────────
        sb.Append("## Respond with exactly this JSON shape\n\n");
        sb.Append("```json\n");
        sb.Append("""
        {
          "language": "<iso>",
          "selfReportedConfidence": { "level": "high|medium|low", "note": "one sentence on your fluency / how much to trust this" },
          "backTranslations": [
            { "scenario": "<scenario name>", "english": "<faithful back-translation of the target report>" }
          ],
          "reportFindings": [
            { "scenario": "<scenario name>", "location": "<where in the report>", "problem": "<what is wrong>", "suggestedFix": "<a better wording>" }
          ],
          "vocabularyVerdicts": [
            { "token": "<token>", "accurate": true, "natural": true, "comment": "<short>", "suggestion": "<better term, or null>" }
          ]
        }
        """);
        sb.Append("\n```\n\n");

        sb.Append("> **Note:** your verdicts are advisory for a human reviewer to adjudicate — they will not be auto-applied. If you are unsure of a term (especially in a low-resource language), say so rather than guessing confidently.\n\n");

        // ── The rendered reports ────────────────────────────────────────────────────────────────────
        sb.Append("## Rendered reports\n");
        foreach (var s in scenarios)
        {
            sb.Append("\n### Scenario: ").Append(s.Name).Append(" — ").Append(s.Synopsis).Append('\n');
            sb.Append("\n#### English reference\n\n");
            AppendFenced(sb, "html", s.EnglishHtml.TrimEnd());
            sb.Append("\n#### ").Append(langLabel).Append(" (under audit)\n\n");
            AppendFenced(sb, "html", s.TargetHtml.TrimEnd());
        }

        // ── Paired vocabulary ───────────────────────────────────────────────────────────────────────
        sb.Append("\n## Controlled vocabulary (English ↔ ").Append(langLabel).Append(")\n\n");
        AppendFenced(sb, "json", JsonSerializer.Serialize(vocabulary, TranslationQaJson.Write));

        return sb.ToString();
    }

    /// <summary>
    /// Append a fenced code block whose fence is longer than the longest backtick run in the content, so
    /// embedded triple-backticks (in rendered HTML or a generator note) can't terminate the fence early.
    /// </summary>
    private static void AppendFenced(StringBuilder sb, string info, string content)
    {
        var longestRun = 0;
        var run = 0;
        foreach (var c in content)
        {
            if (c == '`')
            {
                run++;
                if (run > longestRun)
                    longestRun = run;
            }
            else
            {
                run = 0;
            }
        }

        var fence = new string('`', Math.Max(3, longestRun + 1));
        sb.Append(fence).Append(info).Append('\n').Append(content).Append('\n').Append(fence).Append('\n');
    }
}