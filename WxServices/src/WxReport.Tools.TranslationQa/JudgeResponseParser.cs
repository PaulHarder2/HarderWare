using System.Text.Json;

namespace WxReport.Tools.TranslationQa;

/// <summary>
/// WX-218 — the shared, source-agnostic core: turn a model's raw reply (a pasted Copilot/ChatGPT
/// answer today, an API body later) into a validated <see cref="JudgeResponse"/>. Models routinely
/// wrap their JSON in a ```` ```json ```` fence or surround it with prose (which can itself contain
/// braces like <c>{token}</c> or <c>{0}</c>), so every balanced top-level object is tried in turn and the
/// richest object carrying a <c>language</c> field wins; its fields are then coerced so sparse-but-valid
/// output is kept rather than rejected. Tolerant by design; reports a clear, actionable error rather than
/// throwing on a bad paste.
/// </summary>
public static class JudgeResponseParser
{
    /// <summary>Parse a raw reply. Returns false with a human-readable <paramref name="error"/> on failure.</summary>
    public static bool TryParse(string raw, out JudgeResponse? response, out string? error)
    {
        response = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "the reply is empty.";
            return false;
        }

        var sawCandidate = false;
        string? lastError = null;
        JudgeResponse? best = null;
        var bestScore = -1;

        foreach (var candidate in EnumerateJsonObjects(raw))
        {
            sawCandidate = true;

            JudgeResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<JudgeResponse>(candidate, TranslationQaJson.Read);
            }
            catch (JsonException ex)
            {
                lastError = $"the reply isn't valid JSON: {ex.Message}";
                continue; // a brace fragment in prose, not the payload — keep scanning
            }

            if (parsed is null)
                continue;

            // `language` is the gate that identifies an object as a verdict; a stray "{}" or a prose-brace
            // fragment lacks it and is skipped.
            if (string.IsNullOrWhiteSpace(parsed.Language))
            {
                lastError = "a JSON object in the reply had no 'language' field (not the verdict).";
                continue;
            }

            // A reply can carry more than one language-bearing object (e.g. a model echoes the schema before
            // its real answer). Keep the richest — most verdicts/findings/back-translations — so a sparse
            // echo never short-circuits the real verdict into a silently-empty audit.
            var normalized = Normalize(parsed);
            var score = normalized.VocabularyVerdicts.Count + normalized.ReportFindings.Count + normalized.BackTranslations.Count;
            if (score > bestScore)
            {
                best = normalized;
                bestScore = score;
            }
        }

        if (best is not null)
        {
            response = best;
            return true;
        }

        error = !sawCandidate
            ? "no JSON object found in the reply (expected a { … } block, optionally inside a ```json fence)."
            : lastError ?? "no JSON object in the reply matched the expected verdict shape.";
        return false;
    }

    /// <summary>
    /// Make a verdict safe for consumers without rejecting sparse-but-valid model output: normalize
    /// omitted arrays to empty, drop null elements (and verdicts with no token, which can't be acted on),
    /// and coerce omitted optional text to "" so nothing nulls downstream. The judge's output is advisory,
    /// so a missing comment/finding-field is not a reason to discard the whole audit.
    /// </summary>
    private static JudgeResponse Normalize(JudgeResponse r) => r with
    {
        SelfReportedConfidence = r.SelfReportedConfidence is { } c
            ? c with { Level = c.Level ?? "", Note = c.Note ?? "" }
            : null,
        BackTranslations = (r.BackTranslations ?? [])
            .Where(b => b is not null)
            .Select(b => b with { Scenario = b.Scenario ?? "", English = b.English ?? "" })
            .ToList(),
        ReportFindings = (r.ReportFindings ?? [])
            .Where(f => f is not null)
            .Select(f => f with { Scenario = f.Scenario ?? "", Location = f.Location ?? "", Problem = f.Problem ?? "", SuggestedFix = f.SuggestedFix ?? "" })
            .ToList(),
        VocabularyVerdicts = (r.VocabularyVerdicts ?? [])
            .Where(v => v is not null && !string.IsNullOrWhiteSpace(v.Token))
            .Select(v => v with { Comment = v.Comment ?? "" })
            .ToList(),
    };

    /// <summary>
    /// Yield every balanced top-level <c>{ … }</c> object in document order, correct through nesting and
    /// braces that appear inside JSON string values. Free-text braces in prose come out as their own
    /// (usually invalid) candidates and are filtered by the deserialize + language-gate checks in TryParse.
    /// </summary>
    private static IEnumerable<string> EnumerateJsonObjects(string raw)
    {
        var depth = 0;
        var start = -1;
        var inString = false;
        var escaped = false;

        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (inString)
            {
                if (escaped)
                    escaped = false;
                else if (c == '\\')
                    escaped = true;
                else if (c == '"')
                    inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    if (depth == 0)
                        start = i;
                    depth++;
                    break;
                case '}':
                    if (depth > 0 && --depth == 0 && start >= 0)
                        yield return raw[start..(i + 1)];
                    break;
            }
        }
    }
}