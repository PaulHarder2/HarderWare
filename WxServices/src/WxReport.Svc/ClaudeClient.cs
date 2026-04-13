using System.Net.Http.Json;
using System.Text.Json.Serialization;
using WxInterp;
using WxServices.Common;
using WxServices.Logging;

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

    /// <summary>Initializes a new instance of <see cref="ClaudeClient"/> with the given credentials and model.</summary>
    /// <param name="http">HTTP client used for all requests to the Anthropic Messages API.</param>
    /// <param name="apiKey">Anthropic API key; sent as the <c>x-api-key</c> header.</param>
    /// <param name="model">Claude model ID to use for generation (e.g. <c>"claude-haiku-4-5-20251001"</c>).</param>
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
    /// <param name="snapshot">The weather snapshot to describe; converted to a structured text prompt via <see cref="SnapshotDescriber"/>.</param>
    /// <param name="language">Natural language name for the desired output language (e.g. <c>"English"</c>, <c>"Spanish"</c>).</param>
    /// <param name="recipientName">Recipient's display name, included in the user prompt.</param>
    /// <param name="tz">Recipient's timezone, used by <see cref="SnapshotDescriber"/> to localise timestamps in the prompt.</param>
    /// <param name="isFirstReport">When <see langword="true"/>, the system prompt includes a welcome-note instruction.</param>
    /// <param name="scheduledHour">Daily scheduled send hour (0–23) in the recipient's timezone, included in the welcome note.</param>
    /// <param name="units">
    /// Unit preferences for the recipient.  Controls how temperatures, pressure, and wind speeds
    /// are formatted in the data payload and instructs Claude to use the same units in its narrative.
    /// Defaults to US customary when <see langword="null"/>.
    /// </param>
    /// <param name="changeSeverity">
    /// Severity of the change that triggered this unscheduled send.
    /// <see cref="ChangeSeverity.Alert"/> instructs Claude to open with an urgent notice
    /// identifying the dangerous condition that appeared.
    /// <see cref="ChangeSeverity.Update"/> instructs Claude to open with a brief, neutral
    /// summary of what changed.
    /// <see cref="ChangeSeverity.None"/> (the default) produces no special opening.
    /// </param>
    /// <param name="previousMetarIcao">
    /// ICAO of the station used for the previous report, when it differs from the current
    /// snapshot's station.  When provided, Claude is instructed to briefly note the station
    /// change so the recipient understands why reported conditions may look different.
    /// Pass <see langword="null"/> when no station change occurred.
    /// </param>
    /// <param name="ct">Cancellation token propagated to the HTTP request so that host shutdown aborts an in-flight API call.</param>
    /// <returns>The generated report text, or <see langword="null"/> if the API call or response parsing fails.</returns>
    /// <sideeffects>Makes an HTTP POST request to the Anthropic Messages API. Writes error log entries on failure.</sideeffects>
    public async Task<string?> GenerateReportAsync(
        WeatherSnapshot snapshot, string language, string recipientName,
        TimeZoneInfo tz, bool isFirstReport = false, int scheduledHour = 7,
        UnitPreferences? units = null,
        ChangeSeverity changeSeverity = ChangeSeverity.None,
        string? previousMetarIcao = null,
        CancellationToken ct = default)
    {
        if (scheduledHour < 0 || scheduledHour > 23)
        {
            Logger.Warn($"scheduledHour {scheduledHour} is outside 0–23; clamping to valid range.");
            scheduledHour = Math.Clamp(scheduledHour, 0, 23);
        }

        units ??= new UnitPreferences();
        var weatherData = SnapshotDescriber.Describe(snapshot, tz, units);

        var currentConditionsSubtitle = BuildCurrentConditionsSubtitle(snapshot);
        var currentConditionsHeading  = currentConditionsSubtitle is null
            ? "\"Current Conditions\""
            : $"\"Current Conditions\" followed on a new line by the subtitle \"{currentConditionsSubtitle}\" " +
              $"(font-size 13px, font-style italic, color #6b8fa8, font-weight normal)";
        var forecastHeading = $"\"Forecast for {snapshot.LocalityName}\"";

        var tempLabel  = units.Temperature == "C" ? "Celsius"    : "Fahrenheit";
        var pressLabel = units.Pressure    == "kPa" ? "kPa"      : "inches of mercury (inHg)";
        var windLabel  = units.WindSpeed   == "kph" ? "km/h"     : "mph";
        var unitInstruction = $"Use {tempLabel} for temperatures, {pressLabel} for pressure, " +
                              $"and {windLabel} for wind speeds throughout. ";

        var welcomeInstruction = isFirstReport
            ? $"This is the recipient's very first report. " +
              $"Open with a warm, brief welcome note (2–3 sentences) in {language} " +
              $"introducing the WxReport service and letting them know they will receive " +
              $"a daily weather update at {scheduledHour}:00 local time, plus additional " +
              $"alerts whenever significant weather changes occur. " +
              $"Then continue with the weather report as normal. "
            : "";

        var currentStationLabel = snapshot.StationMunicipality ?? snapshot.StationName ?? snapshot.StationIcao;
        var stationChangeInstruction = previousMetarIcao is not null
            ? $"Note: the weather data source has changed since the last report. " +
              $"The previous weather station had no recent data, " +
              $"so this report uses conditions from {currentStationLabel} instead. " +
              $"Briefly acknowledge this in the report: on an unscheduled update, include one sentence " +
              $"in the change-summary band noting the station switch; on a scheduled report, include one " +
              $"sentence in the closing summary. Keep the tone matter-of-fact — this is routine fallback " +
              $"behaviour, not a cause for concern. "
            : "";

        var changeAlertInstruction = changeSeverity switch
        {
            ChangeSeverity.Alert =>
                "This is an unscheduled weather alert — a significant and potentially dangerous change " +
                "has occurred since the last report. " +
                "For the change-summary band (section 2), write a single clear, direct sentence " +
                "identifying what changed (e.g. 'A thunderstorm has moved into the area' or " +
                "'Visibility has dropped sharply'). ",
            ChangeSeverity.Update =>
                "This is an unscheduled update — conditions have changed since the last report. " +
                "For the change-summary band (section 2), write one or two sentences summarising " +
                "what has changed (e.g. a forecast risk that has appeared, or a significant temperature shift). ",
            _ => "",
        };

        var systemPrompt =
            $"You are producing a weather report email in HTML format, written in {language}, " +
            "for a general (non-specialist) audience. " +
            "Return ONLY the HTML that belongs inside a <body> tag — no <html>, <head>, or <body> " +
            "tags, no markdown, and no code fences. " +
            "Use inline CSS throughout (email clients do not reliably support external stylesheets). " +
            "Maximum content width: 600px, centred, with a clean and professional visual style. " +
            "Structure the output in this order: " +
            "(1) Header div — background #1a3a5c, white text, left-aligned, padding 20px 24px, border-radius 6px 6px 0 0. " +
            "Line 1: the forecast location name in bold at 22px. " +
            "Line 2: local observation time at 14px, color #c8daea. " +
            $"Line 3 (unscheduled reports only): italic text at 13px, color #a0bcd4, " +
            $"reading 'Unscheduled update — see note below', translated into {language}. " +
            "Never use the recipient's name in the header. " +
            "(2) Change-summary band (unscheduled reports only) — background #fef6e4, " +
            "left border 4px solid #e8a020, padding 14px 20px, font-size 14px. " +
            $"Begin with the bold label 'What's changed:' translated into {language}, " +
            "followed by the change summary text. " +
            "Omit this section entirely on scheduled reports. " +
            $"(3) Current Conditions section — background #f7f9fc, padding 20px 24px. " +
            $"Section heading: bold, 17px, color #1a3a5c, 2px solid #1a3a5c bottom border; text is {currentConditionsHeading}. " +
            "Two-column table (label | value), alternating row shading (#eaf0f7 / white). " +
            "Rows in this exact order: Sky; Visibility; Wind; " +
            "Weather (include only when weather phenomena are present, e.g. rain, fog, drizzle — omit on clear days); " +
            "Temperature; Relative Humidity; Pressure. " +
            $"(4) Extended Forecast section — background white, padding 20px 24px. " +
            $"Section heading styled identically to Current Conditions; text is {forecastHeading}. " +
            "Multi-column table, header row background #1a3a5c white text. " +
            "Columns: Date, High/Low, Wind, Conditions. " +
            "Each Conditions cell: a single sentence of no more than 15 words — " +
            "lead with the most important condition and omit anything that can be inferred. " +
            "(5) Closing div — background #f0f4f9, padding 16px 24px, " +
            "border-top 1px solid #d0dce8, border-radius 0. " +
            $"Begin with the bold label 'In summary:' translated into {language}, " +
            "followed by no more than two sentences of plain-language context — " +
            "headline storm risk, a notable temperature trend, or similar. " +
            $"{unitInstruction}" +
            "Rules: use only the data provided — never invent or estimate conditions. " +
            "Never show raw METAR codes, numeric precipitation rates, or CAPE values to the reader. " +
            "Never use aviation terminology — no 'ceiling', 'TAF', 'METAR', 'IFR', 'VFR', or similar. " +
            "Never include altitude or height figures in sky descriptions. " +
            "Describe sky conditions with a short plain phrase that conveys overall coverage and height " +
            "(e.g. 'Low overcast', 'High thin overcast', 'Partly cloudy') — " +
            "do not list or enumerate individual cloud layers. " +
            "You may use TAF forecast data to inform your descriptions, but do not reference it explicitly. " +
            "Use the CAPE label to describe thunderstorm potential in plain language — " +
            "low CAPE warrants at most a mention of an isolated storm; " +
            "significant or extreme CAPE should be described in terms of what the public might " +
            "experience (strong storms, possible damaging winds or hail). " +
            "When precipitation is forecast near freezing temperatures, consider whether " +
            "snow, sleet, or a wintry mix is possible and mention it if so. " +
            $"{welcomeInstruction}" +
            $"{stationChangeInstruction}" +
            $"{changeAlertInstruction}";

        var userPrompt =
            $"Please write a weather report for {recipientName} based on the following observations:\n\n" +
            weatherData;

        var request = new
        {
            model    = _model,
            max_tokens = 4096,
            system   = systemPrompt,
            messages = new[]
            {
                new { role = "user", content = userPrompt },
            },
        };

        const int maxAttempts = 3;
        HttpResponseMessage resp;

        for (int attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, MessagesEndpoint);
            req.Headers.Add("x-api-key", _apiKey);
            req.Headers.Add("anthropic-version", AnthropicVersion);
            req.Content = JsonContent.Create(request);

            try
            {
                resp = await _http.SendAsync(req, ct);
            }
            catch (Exception ex) when (attempt < maxAttempts && ex is HttpRequestException)
            {
                Logger.Warn($"Claude API request failed (attempt {attempt}/{maxAttempts}): {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
                continue;
            }
            catch (Exception ex)
            {
                Logger.Error($"Claude API request failed: {ex}");
                return null;
            }

            var statusCode = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode) break;

            var body = await resp.Content.ReadAsStringAsync();
            resp.Dispose();

            if (attempt < maxAttempts && statusCode is 429 or 529 or >= 500)
            {
                Logger.Warn($"Claude API returned {statusCode} (attempt {attempt}/{maxAttempts}): {body}");
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
                continue;
            }

            Logger.Error($"Claude API returned {statusCode}: {body}");
            return null;
        }

        using (resp)
        {

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

            var bodyContent = parsed?.Content?.FirstOrDefault(c => c.Type == "text")?.Text;
            if (bodyContent is null) return null;

            var langCode = LanguageHelper.ToIetfTag(language);

            var footer = BuildFooterHtml(snapshot, tz);

            return $"""
                <!DOCTYPE html>
                <html lang="{langCode}">
                <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                </head>
                <body style="margin:0;padding:16px;background:#f0f4f8;font-family:Arial,Helvetica,sans-serif;">
                {bodyContent.Trim()}
                {footer}
                </body>
                </html>
                """;
        }
    }

    // ── footer ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a dark-blue footer div containing the observation timestamp,
    /// locality, METAR station, and GFS model run cycle.
    /// Generated in C# so the data is always accurate and consistently formatted,
    /// regardless of report language.
    /// </summary>
    /// <param name="snap">Snapshot supplying station, locality, observation time, and GFS run.</param>
    /// <param name="tz">Timezone used to localise the observation time.</param>
    /// <returns>An HTML string for the footer div.</returns>
    /// <sideeffects>None.</sideeffects>
    private static string BuildFooterHtml(WeatherSnapshot snap, TimeZoneInfo tz)
    {
        var gfsPart = snap.GfsForecast is { } gfs
            ? $" &middot; GFS: {gfs.ModelRunUtc:yyyy-MM-dd HHmm}Z"
            : " &middot; GFS: n/a";

        var line = $"{snap.StationIcao}: {snap.ObservationTimeUtc:yyyy-MM-dd HHmm}Z{gfsPart}"
                 + $" &middot; HarderWare WxServices {WxServices.Common.WxPaths.ProductVersion}";

        return $"""
            <div style="max-width:600px;margin:0 auto;">
            <!--meteogram-->
            <div style="background:#1a3a5c;color:#c8daea;font-size:12px;text-align:center;padding:10px 20px;border-radius:0 0 6px 6px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">
            {line}
            </div>
            </div>
            """;
    }

    // ── subtitle helper ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds the subtitle string for the Current Conditions section heading,
    /// or returns <see langword="null"/> when the station city matches the
    /// recipient's locality (no subtitle needed) or no station metadata is available.
    /// </summary>
    private static string? BuildCurrentConditionsSubtitle(WeatherSnapshot snap)
    {
        var municipality = snap.StationMunicipality;
        var airportName  = snap.StationName;
        var locality     = snap.LocalityName;

        // Cities match — no subtitle
        if (municipality is not null &&
            string.Equals(municipality, locality, StringComparison.OrdinalIgnoreCase))
            return null;

        // Build "at City, Airport Name" from whatever we have.
        // If the airport name already contains the municipality (e.g. "Brenham Municipal Airport"
        // already contains "Brenham"), omit the redundant city prefix.
        if (municipality is not null && airportName is not null)
        {
            return airportName.Contains(municipality, StringComparison.OrdinalIgnoreCase)
                ? $"at {airportName}"
                : $"at {municipality}, {airportName}";
        }
        if (airportName is not null)
            return $"at {airportName}";
        if (municipality is not null)
            return $"at {municipality}";

        return null;
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
