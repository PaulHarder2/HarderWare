using System.Text.Json;

namespace WxReport.Tools.TranslationQa;

/// <summary>
/// WX-218 — the shared, source-agnostic core: turn a model's raw reply (a pasted Copilot/ChatGPT
/// answer today, an API body later) into a validated <see cref="JudgeResponse"/>. Models routinely
/// wrap their JSON in a ```` ```json ```` fence or surround it with prose (which can itself contain
/// braces like <c>{token}</c> or <c>{0}</c>), so every balanced top-level object is tried in turn and
/// the first one that both deserializes and satisfies the non-null contract wins. Tolerant by design;
/// reports a clear, actionable error rather than throwing on a bad paste.
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

            // Normalize omitted arrays to empty so callers never null-check (the judge may legitimately
            // return no findings); then enforce the non-null contract on the required scalars.
            var normalized = parsed with
            {
                BackTranslations = parsed.BackTranslations ?? [],
                ReportFindings = parsed.ReportFindings ?? [],
                VocabularyVerdicts = parsed.VocabularyVerdicts ?? [],
            };

            var contractError = ContractViolation(normalized);
            if (contractError is not null)
            {
                lastError = contractError;
                continue; // e.g. a stray "{}" or non-verdict object — keep scanning for the real one
            }

            response = normalized;
            return true;
        }

        error = !sawCandidate
            ? "no JSON object found in the reply (expected a { … } block, optionally inside a ```json fence)."
            : lastError ?? "no JSON object in the reply matched the expected verdict shape.";
        return false;
    }

    /// <summary>Reject a payload that omitted a required (non-nullable) field — it deserialized to null. Returns null when valid.</summary>
    private static string? ContractViolation(JudgeResponse r)
    {
        if (string.IsNullOrWhiteSpace(r.Language))
            return "the reply is missing the required 'language' field.";

        if (r.SelfReportedConfidence is { } c && (c.Level is null || c.Note is null))
            return "selfReportedConfidence is missing 'level' or 'note'.";

        if (r.BackTranslations.Any(b => b.Scenario is null || b.English is null))
            return "a backTranslations entry is missing 'scenario' or 'english'.";

        if (r.ReportFindings.Any(f => f.Scenario is null || f.Location is null || f.Problem is null || f.SuggestedFix is null))
            return "a reportFindings entry is missing a required field.";

        if (r.VocabularyVerdicts.Any(v => v.Token is null || v.Comment is null))
            return "a vocabularyVerdicts entry is missing 'token' or 'comment'.";

        return null;
    }

    /// <summary>
    /// Yield every balanced top-level <c>{ … }</c> object in document order, correct through nesting and
    /// braces that appear inside JSON string values. Free-text braces in prose come out as their own
    /// (usually invalid) candidates and are filtered by the deserialize/contract checks in TryParse.
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