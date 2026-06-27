using System.Text;
using System.Text.Json;

namespace WxReport.Tools.TranslationQa;

/// <summary>
/// WX-227 — the automated, non-Claude judge: POSTs the full judging request to the Gemini
/// <c>generateContent</c> API in JSON-output mode and parses the verdict through the shared
/// <see cref="JudgeResponseParser"/>. Unlike the manual-paste MVP it has no paste/output ceiling — the
/// whole request goes in one call and the full verdict returns. Independence is preserved (Gemini is
/// not Claude). The API key travels in the <c>x-goog-api-key</c> header (never in the URL, so it can't
/// leak into request logs).
/// </summary>
public sealed class GeminiJudge(HttpClient http, GeminiConfig config) : IJudge
{
    public async Task<JudgeResponse> JudgeAsync(string requestMarkdown, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new JudgeParseException("Gemini API key is not configured — set Gemini:ApiKey in appsettings.local.json at the InstallRoot.");

        var url = $"{config.BaseUrl.TrimEnd('/')}/v1beta/models/{config.Model}:generateContent";
        var requestBody = JsonSerializer.Serialize(new
        {
            contents = new[] { new { parts = new[] { new { text = requestMarkdown } } } },
            // JSON output mode → the reply is a JSON object, not fenced/prose; temperature 0 for a stable verdict.
            generationConfig = new { responseMimeType = "application/json", temperature = 0.0 },
        });

        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json"),
        };
        message.Headers.Add("x-goog-api-key", config.ApiKey);

        string payload;
        System.Net.HttpStatusCode status;
        bool success;
        try
        {
            using var response = await http.SendAsync(message, ct);
            payload = await response.Content.ReadAsStringAsync(ct);
            status = response.StatusCode;
            success = response.IsSuccessStatusCode;
        }
        catch (HttpRequestException ex)
        {
            // DNS / connection reset / TLS / refused — a transport failure, not a cancel or a timeout
            // (those surface as OperationCanceledException and propagate for the caller to handle).
            throw new JudgeParseException($"the Gemini API call failed (network error): {ex.Message}", ex);
        }

        if (!success)
            throw new JudgeParseException($"Gemini API returned {(int)status} {status}: {Truncate(payload)}");

        var text = ExtractText(payload)
            ?? throw new JudgeParseException($"Gemini reply contained no text (blocked, filtered, or empty?): {Truncate(payload)}");

        if (!JudgeResponseParser.TryParse(text, out var verdict, out var error))
            throw new JudgeParseException($"Gemini reply did not parse into a verdict — {error}");

        return verdict!;
    }

    /// <summary>Pull the model's text out of the Gemini envelope: <c>candidates[0].content.parts[*].text</c>.</summary>
    private static string? ExtractText(string payload)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("candidates", out var candidates) ||
                candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
                return null;

            if (!candidates[0].TryGetProperty("content", out var content) ||
                !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
                return null;

            var sb = new StringBuilder();
            foreach (var part in parts.EnumerateArray())
                if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    sb.Append(t.GetString());

            var text = sb.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
    }

    private static string Truncate(string s) => s.Length <= 400 ? s : s[..400] + "…";
}