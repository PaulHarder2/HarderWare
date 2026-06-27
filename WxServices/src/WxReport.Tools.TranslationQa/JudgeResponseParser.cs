using System.Text.Json;

namespace WxReport.Tools.TranslationQa;

/// <summary>
/// WX-218 — the shared, source-agnostic core: turn a model's raw reply (a pasted Copilot/ChatGPT
/// answer today, an API body later) into a validated <see cref="JudgeResponse"/>. Models routinely
/// wrap their JSON in a ```` ```json ```` fence or surround it with prose, so the JSON object is
/// extracted before deserializing. Tolerant by design; reports a clear, actionable error rather than
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

        var json = ExtractJsonObject(raw);
        if (json is null)
        {
            error = "no JSON object found in the reply (expected a { … } block, optionally inside a ```json fence).";
            return false;
        }

        JudgeResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<JudgeResponse>(json, TranslationQaJson.Read);
        }
        catch (JsonException ex)
        {
            error = $"the reply isn't valid JSON: {ex.Message}";
            return false;
        }

        if (parsed is null)
        {
            error = "the reply parsed to null.";
            return false;
        }

        // Normalize omitted arrays to empty so callers never null-check (advisory data: don't reject on
        // a missing section — the judge may legitimately return no findings).
        response = parsed with
        {
            BackTranslations = parsed.BackTranslations ?? [],
            ReportFindings = parsed.ReportFindings ?? [],
            VocabularyVerdicts = parsed.VocabularyVerdicts ?? [],
        };
        return true;
    }

    /// <summary>
    /// Extract the first complete JSON object from arbitrary reply text by balancing braces. Works
    /// whether the object is bare, fenced in a ```json block, or surrounded by prose, and is correct
    /// through nesting and braces that appear inside JSON string values. Returns null if no balanced
    /// object is found (e.g. a truncated paste).
    /// </summary>
    private static string? ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        if (start < 0)
            return null;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < raw.Length; i++)
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
                    depth++;
                    break;
                case '}':
                    if (--depth == 0)
                        return raw[start..(i + 1)];
                    break;
            }
        }

        return null; // unbalanced — no complete object
    }
}