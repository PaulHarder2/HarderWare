using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

using WxServices.Common;

namespace WxManager;

/// <summary>
/// Calls the Claude API to format a plain-text service announcement as a
/// professional HTML email in the specified language.
/// For English, the text is preserved verbatim. For all other languages,
/// the text is translated by Claude before formatting.
/// </summary>
public sealed class AnnouncementFormatter
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _endpoint;
    private readonly string _apiVersion;
    private readonly int _maxTokens;

    /// <summary>Initialises a new instance with the given HTTP client and Claude credentials.</summary>
    /// <param name="http">HTTP client used for all Anthropic API requests.</param>
    /// <param name="apiKey">Anthropic API key.</param>
    /// <param name="model">Claude model ID (e.g. <c>"claude-sonnet-4-6"</c>).</param>
    /// <param name="endpoint">Anthropic Messages API endpoint URL.</param>
    /// <param name="apiVersion">Anthropic API version header value.</param>
    /// <param name="maxTokens">Maximum tokens for the Claude response.</param>
    public AnnouncementFormatter(HttpClient http, string apiKey, string model,
                                 string endpoint, string apiVersion, int maxTokens)
    {
        _http = http;
        _apiKey = apiKey;
        _model = model;
        _endpoint = endpoint;
        _apiVersion = apiVersion;
        _maxTokens = maxTokens;
    }

    /// <summary>
    /// Sends <paramref name="text"/> to Claude and returns a complete HTML document
    /// formatted as a service announcement email in <paramref name="language"/>.
    /// Returns <see langword="null"/> if the API call fails.
    /// </summary>
    /// <param name="text">Plain-text announcement written by the operator.</param>
    /// <param name="language">Target language name (e.g. <c>"English"</c>, <c>"Spanish"</c>).</param>
    /// <param name="ct">Cancellation token propagated to the HTTP request.</param>
    /// <returns>A complete HTML document string, or <see langword="null"/> on failure.</returns>
    /// <sideeffects>Makes an HTTP POST request to the Anthropic Messages API. Throws on network failure (caught by caller).</sideeffects>
    public async Task<string?> FormatAsync(string text, string language, CancellationToken ct = default)
    {
        var translateInstruction = language.Equals("English", StringComparison.OrdinalIgnoreCase)
            ? "Preserve the announcement text exactly as written — do not translate, paraphrase, or alter the wording."
            : $"Translate the announcement text into {language}. Preserve all meaning and tone faithfully.";

        var systemPrompt =
            $"You are formatting a service announcement as an HTML email, written in {language}. " +
            "Return ONLY the HTML that belongs inside a <body> tag — no <html>, <head>, or <body> tags, no markdown, no code fences. " +
            "Use inline CSS throughout (email clients do not reliably support external stylesheets). " +
            "Maximum content width: 600px, centred, clean and professional. " +
            "Structure the output in this order: " +
            "(1) Header div — background #1a3a5c, white text, left-aligned, padding 20px 24px, border-radius 6px 6px 0 0. " +
            $"All header text at 22px in a single span: <strong>HarderWare</strong> followed by a non-bold space and the phrase 'Service Announcement' translated into {language} — same font size throughout, no other bold. " +
            "(2) Body div — background white, padding 20px 24px, font-size 14px, line-height 1.6, color #222, border-radius 0 0 6px 6px. " +
            "Render the announcement content as clean HTML paragraphs. Preserve all paragraph breaks from the source text. " +
            translateInstruction +
            " Do not add, remove, or editorialize any content beyond what is required for HTML formatting.";

        var request = new
        {
            model = _model,
            max_tokens = _maxTokens,
            system = systemPrompt,
            messages = new[] { new { role = "user", content = text } },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        req.Headers.Add("x-api-key", _apiKey);
        req.Headers.Add("anthropic-version", _apiVersion);
        req.Content = JsonContent.Create(request);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Claude API request failed: {ex.Message}", ex);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"Claude API returned {(int)resp.StatusCode}: {body}");
            }

            ClaudeResponse? parsed;
            try
            {
                parsed = await resp.Content.ReadFromJsonAsync<ClaudeResponse>(cancellationToken: ct);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to parse Claude API response: {ex.Message}", ex);
            }

            var bodyContent = parsed?.Content?.FirstOrDefault(c => c.Type == "text")?.Text;
            if (bodyContent is null)
                throw new InvalidOperationException("Claude API returned an empty response.");

            var langCode = LanguageHelper.ToIetfTag(language);

            return $"""
                <!DOCTYPE html>
                <html lang="{langCode}">
                <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                </head>
                <body style="margin:0;padding:16px;background:#f0f4f8;font-family:Arial,Helvetica,sans-serif;">
                {bodyContent.Trim()}
                </body>
                </html>
                """;
        }
    }

    // ── response DTOs ─────────────────────────────────────────────────────────

    private sealed class ClaudeResponse
    {
        [JsonPropertyName("content")]
        public List<ContentBlock>? Content { get; set; }
    }

    private sealed class ContentBlock
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
    }
}