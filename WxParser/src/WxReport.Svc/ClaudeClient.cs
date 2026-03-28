using System.Net.Http.Json;
using System.Text.Json.Serialization;
using WxInterp;
using WxParser.Logging;

namespace WxReport.Svc;

/// <summary>
/// Thin wrapper around the Anthropic Messages API.
/// Calls Claude to generate a human-readable weather report in the requested language.
/// </summary>
public sealed class ClaudeClient
{
    private const string MessagesEndpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string     _apiKey;
    private readonly string     _model;

    public ClaudeClient(HttpClient http, string apiKey, string model)
    {
        _http   = http;
        _apiKey = apiKey;
        _model  = model;
    }

    /// <summary>
    /// Asks Claude to produce a weather report in <paramref name="language"/>
    /// based on the structured description of <paramref name="snapshot"/>.
    /// When <paramref name="isFirstReport"/> is true, Claude is asked to open
    /// with a brief welcome note explaining the service and its schedule.
    /// Returns the generated text, or null if the API call fails.
    /// </summary>
    public async Task<string?> GenerateReportAsync(
        WeatherSnapshot snapshot, string language, string recipientName,
        TimeZoneInfo tz, bool isFirstReport = false, int scheduledHour = 7)
    {
        var weatherData = SnapshotDescriber.Describe(snapshot, tz);

        var welcomeInstruction = isFirstReport
            ? $"This is the recipient's very first report. " +
              $"Open with a warm, brief welcome note (2–3 sentences) in {language} " +
              $"introducing the WxReport service and letting them know they will receive " +
              $"a daily weather update at {scheduledHour}:00 local time, plus additional " +
              $"alerts whenever significant weather changes occur. " +
              $"Then continue with the weather report as normal. "
            : "";

        var systemPrompt =
            $"You are a weather reporter producing a short, friendly weather summary in {language} " +
            "for a general (non-specialist) audience, in the style used by local television news. " +
            "Use only the data provided — do not invent or estimate any conditions. " +
            "Do not include raw METAR codes or technical jargon. " +
            $"{welcomeInstruction}" +
            "The report should be 2–4 short paragraphs.";

        var userPrompt =
            $"Please write a weather report for {recipientName} based on the following observations:\n\n" +
            weatherData;

        var request = new
        {
            model    = _model,
            max_tokens = 1024,
            system   = systemPrompt,
            messages = new[]
            {
                new { role = "user", content = userPrompt },
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, MessagesEndpoint);
        req.Headers.Add("x-api-key", _apiKey);
        req.Headers.Add("anthropic-version", AnthropicVersion);
        req.Content = JsonContent.Create(request);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req);
        }
        catch (Exception ex)
        {
            Logger.Error($"Claude API request failed: {ex.Message}");
            return null;
        }

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            Logger.Error($"Claude API returned {(int)resp.StatusCode}: {body}");
            return null;
        }

        ClaudeResponse? parsed;
        try
        {
            parsed = await resp.Content.ReadFromJsonAsync<ClaudeResponse>();
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to parse Claude API response: {ex.Message}");
            return null;
        }

        return parsed?.Content?.FirstOrDefault(c => c.Type == "text")?.Text;
    }

    // ── response DTOs ─────────────────────────────────────────────────────────

    private sealed class ClaudeResponse
    {
        [JsonPropertyName("content")]
        public List<ContentBlock>? Content { get; set; }
    }

    private sealed class ContentBlock
    {
        [JsonPropertyName("type")]  public string? Type { get; set; }
        [JsonPropertyName("text")]  public string? Text { get; set; }
    }
}
