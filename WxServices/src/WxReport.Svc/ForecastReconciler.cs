using System.Text;
using System.Text.Json;

using MetarParser.Data.Entities;

using WxInterp;

using WxServices.Common;
using WxServices.Logging;

namespace WxReport.Svc;

/// <summary>
/// Outcome of a single reconciliation pass.  A <see cref="Success"/>
/// carries the three artifacts Claude returned (HTML email body, parsed
/// final snapshot, plain-English reasoning trace) plus token-usage
/// metadata.  A <see cref="Failure"/> carries a short reason string; the
/// caller is expected to log it, skip the SMTP send, and leave any
/// already-committed audit rows in place (never un-commit state).
/// </summary>
public abstract record ReconcileResult
{
    private ReconcileResult() { }

    /// <summary>Reconciliation succeeded; the three artifacts parsed cleanly.</summary>
    /// <param name="EmailBody">HTML email body for the recipient (inner content of the <c>&lt;body&gt;</c> tag).</param>
    /// <param name="FinalSnapshot">Refined <see cref="ForecastSnapshotBody"/> after Claude reconciled provisional + TAF + observation + prior.</param>
    /// <param name="ReasoningTrace">Brief plain-English audit log of what changed at each of the three reconciliation steps.</param>
    /// <param name="Tokens">Token-usage metadata extracted from the Anthropic API response.</param>
    public sealed record Success(
        string EmailBody,
        ForecastSnapshotBody FinalSnapshot,
        string ReasoningTrace,
        TokenUsage Tokens) : ReconcileResult;

    /// <summary>
    /// The WX-80 invalidation gate fired: Claude judged the cycle's evidence
    /// not news worth sending and called <c>skip_send</c> instead of
    /// <c>submit_reconciled_report</c>.  No email is sent and the committed
    /// forecast is left unchanged; the caller persists
    /// <see cref="ReasoningTrace"/> on the provisional row for diagnosability.
    /// Only reachable on cycles invoked with <c>allowSkip = true</c>.
    /// </summary>
    /// <param name="ReasoningTrace">Claude's plain-English explanation of why this cycle is not news.</param>
    /// <param name="Tokens">Token-usage metadata for the call (a skip still costs tokens).</param>
    public sealed record NotNews(string ReasoningTrace, TokenUsage Tokens) : ReconcileResult;

    /// <summary>
    /// Reconciliation did not produce a valid result.  Reasons include API
    /// transport failure, missing or wrong-named tool_use block, malformed tool
    /// input JSON, schema-violation in <c>final_snapshot</c>, or violation of the
    /// precipPhenomenon-iff-non-none invariant.
    /// </summary>
    /// <param name="Reason">Short human-readable description of why reconciliation failed.</param>
    public sealed record Failure(string Reason) : ReconcileResult;
}

/// <summary>
/// Orchestrates the WX-79 forecast reconciliation pass: builds Claude's
/// per-recipient system prompt and user message from a recipient's
/// <see cref="WeatherSnapshot"/>, the GFS-derived provisional
/// <see cref="ForecastSnapshotBody"/>, and the prior committed snapshot if
/// any; calls <see cref="ClaudeClient.InvokeReconciliationAsync"/>;
/// validates the response (<c>final_snapshot</c> via
/// <see cref="ForecastSnapshotBody.Deserialize"/>); and returns either a
/// parsed three-artifact <see cref="ReconcileResult.Success"/> or a typed
/// <see cref="ReconcileResult.Failure"/>.
///
/// <para>
/// Malformed-output policy (decided at WX-79 grooming): schema-violation,
/// missing field, or non-JSON tool input returns a typed Failure.  The
/// caller (<see cref="ReportWorker"/>) does not send the email when Failure
/// is returned, but does not un-commit any provisional rows that were
/// written before the reconciler ran — the auditable shape of "we tried,
/// Claude failed" stays in place.
/// </para>
/// </summary>
public sealed class ForecastReconciler
{
    private readonly ClaudeClient _claude;

    /// <summary>Initializes a new instance backed by the supplied <see cref="ClaudeClient"/>.</summary>
    /// <param name="claude">Anthropic Messages API wrapper used for the tool-use call.</param>
    public ForecastReconciler(ClaudeClient claude)
    {
        _claude = claude;
    }

    /// <summary>
    /// Reconciles the GFS-derived provisional snapshot against the current
    /// observation, the active TAF (when present), and the prior committed
    /// snapshot (when present), via a single Claude tool-use call.  Returns
    /// the parsed three artifacts on success or a typed failure on transport
    /// or schema problems.
    /// </summary>
    /// <param name="snapshot">Recipient's <see cref="WeatherSnapshot"/>; supplies METAR, TAF periods, GFS daily summary, and per-station metadata used in the rendering rules.</param>
    /// <param name="provisional">GFS-derived provisional snapshot body produced by <see cref="GfsSnapshotBuilder.Build"/> (the first pass).</param>
    /// <param name="gfsModelRunUtc">UTC initialisation time of the GFS run the provisional was built from; supplied to Claude for the issuance-time comparison in reconciliation step 1.  <see langword="null"/> when no GFS data was available for the recipient's location (provisional body will be empty in that case).</param>
    /// <param name="tafIssuanceUtc">UTC time the active TAF was issued, or <see langword="null"/> when no TAF is available.  Required for reconciliation step 1.</param>
    /// <param name="tafValidToUtc">UTC end of the TAF's validity window, or <see langword="null"/> when no TAF is available.  Helps Claude scope step 1 to in-window blocks.</param>
    /// <param name="prior">Most recently committed <see cref="ForecastSnapshot"/> for this station, or <see langword="null"/> on a first send.  Drives the news judgment in reconciliation step 3.</param>
    /// <param name="language">Natural-language name for the desired email language (e.g. <c>"English"</c>, <c>"Spanish"</c>).</param>
    /// <param name="recipientName">Recipient's display name, surfaced in the user-message header.</param>
    /// <param name="tz">Recipient's timezone, used by <see cref="SnapshotDescriber"/> when emitting the structured observation/forecast text.</param>
    /// <param name="isFirstReport">When <see langword="true"/>, the system prompt asks Claude to open with a welcome note.</param>
    /// <param name="scheduledHour">Daily scheduled send hour (0–23) in the recipient's timezone; referenced by the welcome note.</param>
    /// <param name="units">Unit preferences for the rendered email; defaults to US customary when <see langword="null"/>.</param>
    /// <param name="changeSeverity">Severity of the trigger that caused this send (alert, update, or none).</param>
    /// <param name="previousMetarIcao">ICAO of the previous report's station, when it differs from the current snapshot's station; <see langword="null"/> when no station change occurred.</param>
    /// <param name="allowSkip">When <see langword="true"/> (unscheduled, arrival-triggered cycles), Claude may decline to send via the <c>skip_send</c> tool, yielding a <see cref="ReconcileResult.NotNews"/>.  When <see langword="false"/> (scheduled / first / startup), the send is guaranteed and skipping is not offered.</param>
    /// <param name="changedSinceLastSend">Which inputs (METAR/TAF/GFS) are newer than they were at the last report actually delivered to this recipient (WX-108).  Surfaced to Claude as <c>changed_since_last_sent_report</c> so the anti-reversal rule can bind on observation-only cycles.  Empty list means nothing advanced since the last send; treated as a first send when no prior send exists.</param>
    /// <param name="ct">Cancellation token propagated to the underlying HTTP call.</param>
    /// <returns>A <see cref="ReconcileResult.Success"/> on a clean three-artifact return; a <see cref="ReconcileResult.NotNews"/> when Claude skips an arrival-triggered send; a <see cref="ReconcileResult.Failure"/> otherwise.</returns>
    /// <sideeffects>Makes one HTTP POST to the Anthropic Messages API via <see cref="ClaudeClient.InvokeReconciliationAsync"/>.  Writes error log entries on schema-validation failure.</sideeffects>
    public async Task<ReconcileResult> ReconcileAsync(
        WeatherSnapshot snapshot,
        ForecastSnapshotBody provisional,
        DateTime? gfsModelRunUtc,
        DateTime? tafIssuanceUtc,
        DateTime? tafValidToUtc,
        ForecastSnapshot? prior,
        string language,
        string recipientName,
        TimeZoneInfo tz,
        bool isFirstReport,
        int scheduledHour,
        UnitPreferences? units,
        ChangeSeverity changeSeverity,
        string? previousMetarIcao,
        bool allowSkip,
        IReadOnlyList<TriggerSource> changedSinceLastSend,
        CancellationToken ct = default)
    {
        units ??= new UnitPreferences();

        var perRecipientPrompt = BuildPerRecipientSystemPrompt(
            snapshot, language, units, isFirstReport, scheduledHour, changeSeverity, previousMetarIcao, allowSkip);

        var userMessage = BuildUserMessage(
            snapshot, provisional, gfsModelRunUtc, tafIssuanceUtc, tafValidToUtc, prior, recipientName, tz, units,
            changedSinceLastSend);

        // WX-110: Claude intermittently returns a complete (untruncated) tool_use
        // that omits a required field or whose final_snapshot fails schema
        // validation — the Anthropic API treats a tool schema's `required` list as
        // advisory, so a field can simply be absent. The fault is transient (WX-109
        // raised the cap and added max_tokens detection, yet the missing-field
        // failures continued on the fixed binary with stop_reason != max_tokens), so
        // an immediate re-call almost always succeeds. Retry up to maxAttempts within
        // the cycle rather than leaving the recipient to self-heal on the next tick.
        // NOT retried: a max_tokens truncation (re-calling at the same cap would just
        // re-truncate — left to self-heal) and a guaranteed-send skip_send (a contract
        // violation, not a transient fault). The prompts are built once above and are
        // identical across attempts (so the cached system-prompt prefix keeps retries
        // cheap).
        const int maxAttempts = 3;
        int accIn = 0, accOut = 0, accCacheRead = 0, accCacheWrite = 0;
        for (int attempt = 1; ; attempt++)
        {
            var apiResult = await _claude.InvokeReconciliationAsync(perRecipientPrompt, userMessage, allowSkip, ct);
            if (apiResult is null)
            {
                return new ReconcileResult.Failure(
                    "Claude API call failed or returned no submit_reconciled_report or skip_send tool_use block.");
            }

            // WX-109: a "max_tokens" stop_reason means generation was cut at the
            // output-token cap, so the tool_use input is a truncated partial object —
            // a trailing required field (often email_body) is dropped. Detect that
            // here, before reading any field, so the operator sees a truthful
            // "response truncated" failure instead of WX-104's accurate-but-misleading
            // "missing required field 'email_body'". Not retried (see above): a re-call
            // at the same cap would re-truncate; the caller leaves the provisional
            // CommittedSend in place and the next cycle reconciles a fresh one.
            if (apiResult.StopReason == "max_tokens")
            {
                Logger.Error(
                    "Reconciliation response was truncated at the output-token cap "
                    + "(stop_reason=max_tokens); the tool_use input is incomplete.");
                return new ReconcileResult.Failure(
                    "Reconciliation response was truncated at the output-token cap "
                    + "(stop_reason=max_tokens) before the tool_use input was complete.");
            }

            // WX-110: accumulate token usage across attempts so a retried-then-
            // succeeded cycle reports its full billed cost — a failed malformed
            // attempt is still real API spend, and the new cost dashboards would
            // otherwise undercount it. On a single-attempt cycle this equals that
            // attempt's tokens (unchanged behaviour).
            accIn += apiResult.Tokens.InputTokens;
            accOut += apiResult.Tokens.OutputTokens;
            accCacheRead += apiResult.Tokens.CacheReadInputTokens;
            accCacheWrite += apiResult.Tokens.CacheCreationInputTokens;
            var tokens = new TokenUsage(accIn, accOut, accCacheRead, accCacheWrite);

            try
            {
                var input = apiResult.ToolUseInput;

                // Invalidation gate: Claude chose skip_send — not news worth sending.
                // Defense in depth: ClaudeClient now rejects a skip_send returned on a
                // non-skippable cycle at the API boundary, so the !allowSkip branch
                // below is normally unreachable. We keep it deliberately — "a
                // guaranteed send (scheduled / first / startup) must never be silently
                // skipped" is a customer-facing invariant worth guarding twice. If the
                // boundary check ever regresses, this still fails closed (Failure, so
                // the provisional stays and the caller logs it) rather than suppressing
                // a send that must always go out. Not retried: a contract violation is
                // not a transient fault.
                if (apiResult.ToolName == "skip_send")
                {
                    if (!allowSkip)
                        return new ReconcileResult.Failure("Claude returned skip_send on a non-skippable (guaranteed) send.");

                    var skipTrace = RequireString(input, "reasoning_trace");
                    return new ReconcileResult.NotNews(skipTrace, tokens);
                }

                var emailBody = RequireString(input, "email_body");

                var finalSnapshotJson = RequireProperty(input, "final_snapshot").GetRawText();
                var finalSnapshot = ForecastSnapshotBody.Deserialize(finalSnapshotJson);

                var reasoningTrace = RequireString(input, "reasoning_trace");

                return new ReconcileResult.Success(emailBody, finalSnapshot, reasoningTrace, tokens);
            }
            catch (Exception ex) when (ex is MissingToolUseFieldException or JsonException or InvalidOperationException)
            {
                // Retryable-malformed output: re-call unless attempts are exhausted.
                if (attempt < maxAttempts)
                {
                    var what = ex is MissingToolUseFieldException
                        ? $"is {ex.Message}"
                        : $"failed validation: {ex.Message}";
                    Logger.Warn($"Reconciliation tool_use input {what} (attempt {attempt}/{maxAttempts}); retrying.");
                    continue;
                }

                // Exhausted. Report exactly as the pre-WX-110 single-shot path did —
                // WX-104's precise by-name field message, or the schema-validation
                // message — so the operator-facing failure is unchanged but for the
                // attempt count. The caller leaves the provisional CommittedSend in
                // place as a failed-attempt audit row (SentAtUtc stays null, so it
                // never becomes a prior snapshot); the next cycle reconciles a fresh
                // provisional, which is how the system self-heals.
                if (ex is MissingToolUseFieldException)
                {
                    Logger.Error($"Reconciliation tool_use input is {ex.Message} (after {maxAttempts} attempts).");
                    return new ReconcileResult.Failure($"Reconciliation response {ex.Message} (after {maxAttempts} attempts).");
                }
                Logger.Error($"Reconciliation tool_use input failed validation (after {maxAttempts} attempts): {ex.Message}");
                return new ReconcileResult.Failure($"Schema validation failed (after {maxAttempts} attempts): {ex.Message}");
            }
        }
    }

    // ── tool_use field accessors (WX-104) ─────────────────────────────────────

    // Reads a required property from Claude's tool_use input, naming the field when
    // absent. JsonElement.GetProperty throws a bare KeyNotFoundException on a missing
    // key — which previously surfaced to the operator mislabelled "schema validation
    // failed"; TryGetProperty + this typed throw report the exact field instead.
    private static JsonElement RequireProperty(JsonElement input, string field)
        => input.TryGetProperty(field, out var value)
            ? value
            : throw new MissingToolUseFieldException(field);

    // As RequireProperty, but also requires a non-null string value; a present-but-null
    // or wrong-typed value is treated as a missing field so the failure still names it.
    private static string RequireString(JsonElement input, string field)
        => RequireProperty(input, field) is { ValueKind: JsonValueKind.String } v && v.GetString() is { } s
            ? s
            : throw new MissingToolUseFieldException(field);

    // Signals that Claude's tool_use input lacked a usable value for a required field.
    // The message names the field so the operator-facing failure is precise (WX-104).
    private sealed class MissingToolUseFieldException : Exception
    {
        public MissingToolUseFieldException(string field)
            : base($"missing required field '{field}'") { }
    }

    // ── per-recipient system prompt ──────────────────────────────────────────

    // Builds the per-recipient rendering rules (the third system block).
    // Originally ported from the now-removed ClaudeClient.GenerateReportAsync.
    private static string BuildPerRecipientSystemPrompt(
        WeatherSnapshot snapshot, string language, UnitPreferences units,
        bool isFirstReport, int scheduledHour, ChangeSeverity changeSeverity, string? previousMetarIcao,
        bool allowSkip)
    {
        var currentConditionsSubtitle = BuildCurrentConditionsSubtitle(snapshot);
        var currentConditionsHeading = currentConditionsSubtitle is null
            ? "\"Current Conditions\""
            : $"\"Current Conditions\" followed on a new line by the subtitle \"{currentConditionsSubtitle}\" "
              + "(font-size 13px, font-style italic, color #6b8fa8, font-weight normal)";
        var forecastHeading = $"\"Forecast for {snapshot.LocalityName}\"";

        var tempLabel = units.Temperature == "C" ? "Celsius" : "Fahrenheit";
        var pressLabel = units.Pressure == "kPa" ? "kPa" : "inches of mercury (inHg)";
        var windLabel = units.WindSpeed == "kph" ? "km/h" : "mph";
        var unitInstruction = $"Use {tempLabel} for temperatures, {pressLabel} for pressure, "
                            + $"and {windLabel} for wind speeds throughout. ";

        var welcomeInstruction = isFirstReport
            ? "This is the recipient's very first report. "
              + $"Open with a warm, brief welcome note (2–3 sentences) in {language} "
              + "introducing the WxReport service and letting them know they will receive "
              + $"a daily weather update at {scheduledHour}:00 local time, plus additional "
              + "alerts whenever significant weather changes occur. "
              + "Then continue with the weather report as normal. "
            : "";

        var currentStationLabel = snapshot.StationMunicipality ?? snapshot.StationName ?? snapshot.StationIcao;
        var stationChangeInstruction = previousMetarIcao is not null
            ? "Note: the weather data source has changed since the last report. "
              + "The previous weather station had no recent data, "
              + $"so this report uses conditions from {currentStationLabel} instead. "
              + "Briefly acknowledge this in the report: on an unscheduled update, include one sentence "
              + "in the change-summary band noting the station switch; on a scheduled report, include one "
              + "sentence in the closing summary. Keep the tone matter-of-fact — this is routine fallback "
              + "behaviour, not a cause for concern. "
            : "";

        var changeAlertInstruction = changeSeverity switch
        {
            ChangeSeverity.Alert =>
                "This is an unscheduled weather alert — a significant and potentially dangerous change "
                + "has occurred since the last report. "
                + "For the change-summary band (section 2), write a single clear, direct sentence "
                + "identifying what changed (e.g. 'A thunderstorm has moved into the area' or "
                + "'Visibility has dropped sharply'). ",
            ChangeSeverity.Update =>
                "This is an unscheduled update — conditions have changed since the last report. "
                + "For the change-summary band (section 2), write one or two sentences summarising "
                + "what has changed (e.g. a forecast risk that has appeared, or a significant temperature shift). ",
            _ => "",
        };

        var skipInstruction = allowSkip
            ? "This is an unscheduled, arrival-triggered cycle. Apply the invalidation gate: if the "
              + "new evidence is not news worth sending — it confirms, or only trivially drifts from, "
              + "what the prior committed forecast already told this recipient — call the skip_send tool "
              + "with a brief reasoning_trace instead of producing a report. Only call "
              + "submit_reconciled_report when the change is genuinely worth an unscheduled email. "
            : "This cycle is always worth sending — call submit_reconciled_report. Do not call skip_send. ";

        return
            $"You are producing a weather report email in HTML format, written in {language}, "
            + "for a general (non-specialist) audience. "
            + "Return the HTML as the email_body argument to the submit_reconciled_report tool — "
            + "this body must be only the inner HTML for the <body> tag, no <html>, <head>, or <body> "
            + "tags, no markdown, no code fences. "
            + "Use inline CSS throughout (email clients do not reliably support external stylesheets). "
            + "Maximum content width: 600px, centred, with a clean and professional visual style. "
            + "Structure the output in this order: "
            + "(1) Header div — background #1a3a5c, white text, left-aligned, padding 20px 24px, border-radius 6px 6px 0 0. "
            + "Line 1: the forecast location name in bold at 22px. "
            + "Line 2: local observation time at 14px, color #c8daea. "
            + "Line 3 (unscheduled reports only): italic text at 13px, color #a0bcd4, "
            + $"reading 'Unscheduled update — see note below', translated into {language}. "
            + "Never use the recipient's name in the header. "
            + "(2) Change-summary band (unscheduled reports only) — background #fef6e4, "
            + "left border 4px solid #e8a020, padding 14px 20px, font-size 14px. "
            + $"Begin with the bold label 'What's changed:' translated into {language}, "
            + "followed by the change summary text. "
            + "Omit this section entirely on scheduled reports. "
            + "(3) Current Conditions section — background #f7f9fc, padding 20px 24px. "
            + $"Section heading: bold, 17px, color #1a3a5c, 2px solid #1a3a5c bottom border; text is {currentConditionsHeading}. "
            + (snapshot.ObservationAvailable
                ? "Two-column table (label | value), alternating row shading (#eaf0f7 / white). "
                  + "Rows in this exact order: Sky; Visibility; Wind; "
                  + "Weather (include only when weather phenomena are present, e.g. rain, fog, drizzle — omit on clear days); "
                  + "Temperature; Relative Humidity; Pressure. "
                : "When the data payload shows 'Current observation: NOT AVAILABLE', omit any weather table. "
                  + $"Instead, render a single short paragraph in italic at 14px, color #6b8fa8, translated into {language}, "
                  + "explaining in plain language that no recent observation is available from any station within about 30 miles, "
                  + "and that the report below is therefore based on forecast model data only. "
                  + "Do not invent or estimate any current-conditions values. ")
            + "(4) Extended Forecast section — background white, padding 20px 24px. "
            + $"Section heading styled identically to Current Conditions; text is {forecastHeading}. "
            + "Multi-column table, header row background #1a3a5c white text. "
            + "Columns: Date, Temperatures, Wind, Conditions. "
            + "The Temperatures cell shows the daily high above the daily low on two lines "
            + "separated by <br/>, with each value carrying its own unit suffix and labeled "
            + "(e.g. 'High: 85°F<br/>Low: 72°F' or 'High: 29°C<br/>Low: 22°C') — never "
            + "combine the two values with a slash. "
            + "Each Conditions cell: a single sentence of no more than 15 words — "
            + "lead with the most important condition and omit anything that can be inferred. "
            + "(5) Closing div — background #f0f4f9, padding 16px 24px, "
            + "border-top 1px solid #d0dce8, border-radius 0. "
            + $"Begin with the bold label 'In summary:' translated into {language}, "
            + "followed by no more than two sentences of plain-language context — "
            + "headline storm risk, a notable temperature trend, or similar. "
            + unitInstruction
            + "Rules: use only the data provided — never invent or estimate conditions. "
            + "Never show raw METAR codes, numeric precipitation rates, or CAPE values to the reader. "
            + "Never use aviation terminology — no 'ceiling', 'TAF', 'METAR', 'IFR', 'VFR', or similar. "
            + "Never include altitude or height figures in sky descriptions. "
            + "Describe sky conditions with a short plain phrase that conveys overall coverage and height "
            + "(e.g. 'Low overcast', 'High thin overcast', 'Partly cloudy') — "
            + "do not list or enumerate individual cloud layers. "
            + "You may use TAF forecast data to inform your descriptions, but do not reference it explicitly. "
            + "Use the CAPE label to describe thunderstorm potential in plain language — "
            + "low CAPE warrants at most a mention of an isolated storm; "
            + "significant or extreme CAPE should be described in terms of what the public might "
            + "experience (strong storms, possible damaging winds or hail). "
            + "When precipitation is forecast near freezing temperatures, consider whether "
            + "snow, sleet, or a wintry mix is possible and mention it if so. "
            + "Never state weather as flatly certain — no forecast is ever 100% sure, and "
            + "no forecaster speaks as if it were. Even when a block's precipExpectation is "
            + "'certain' or its severeFlag is set — events as close to certain as makes no "
            + "difference — render them as calibrated strong likelihood ('almost certain', "
            + "'highly likely', 'expect', 'very likely'), never as a guarantee: avoid "
            + "'will' used as a promise, 'certain', 'definitely', 'guaranteed', and "
            + "'no chance'. Apply the same hedging to severe-weather and hazard wording. "
            + welcomeInstruction
            + stationChangeInstruction
            + changeAlertInstruction
            + skipInstruction;
    }

    // ── user message ─────────────────────────────────────────────────────────

    // Assembles the per-call inputs Claude needs for the reconciliation
    // procedure: the GFS model run timestamp, the provisional snapshot body
    // (as canonical JSON), the structured observation/forecast text (via
    // SnapshotDescriber), the TAF issuance/validity timestamps, and the
    // prior snapshot's generated-at timestamp + body when present.
    private static string BuildUserMessage(
        WeatherSnapshot snapshot, ForecastSnapshotBody provisional, DateTime? gfsModelRunUtc,
        DateTime? tafIssuanceUtc, DateTime? tafValidToUtc, ForecastSnapshot? prior,
        string recipientName, TimeZoneInfo tz, UnitPreferences units,
        IReadOnlyList<TriggerSource> changedSinceLastSend)
    {
        var sb = new StringBuilder();
        sb.Append("Reconcile the following inputs for ").Append(recipientName)
          .AppendLine(" and emit your three artifacts via the submit_reconciled_report tool.");
        sb.AppendLine();

        sb.Append("changed_since_last_sent_report: ")
          .AppendLine(DescribeChangedSinceLastSend(prior, changedSinceLastSend));
        sb.AppendLine();

        sb.Append("provisional_snapshot.gfs_model_run_utc: ")
          .AppendLine(gfsModelRunUtc.HasValue ? gfsModelRunUtc.Value.ToString("O") : "null (no GFS data available)");
        sb.AppendLine("provisional_snapshot.body:");
        sb.AppendLine(provisional.Serialize());
        sb.AppendLine();

        if (tafIssuanceUtc.HasValue && tafValidToUtc.HasValue)
        {
            sb.Append("current_forecast.issuance_utc: ").AppendLine(tafIssuanceUtc.Value.ToString("O"));
            sb.Append("current_forecast.validity_to_utc: ").AppendLine(tafValidToUtc.Value.ToString("O"));
        }
        else
        {
            sb.AppendLine("current_forecast: null (no TAF available for this station)");
        }
        sb.AppendLine();

        sb.AppendLine("current_observation and current_forecast (structured text):");
        sb.AppendLine(SnapshotDescriber.Describe(snapshot, tz, units));
        sb.AppendLine();

        if (prior is not null)
        {
            sb.Append("prior_snapshot.generated_at_utc: ").AppendLine(prior.GeneratedAtUtc.ToString("O"));
            sb.AppendLine("prior_snapshot.body:");
            sb.AppendLine(prior.Body);
        }
        else
        {
            sb.AppendLine("prior_snapshot: null (first send for this station)");
        }

        return sb.ToString();
    }

    // Renders the WX-108 changed_since_last_sent_report descriptor: which inputs
    // are newer than at the last delivered report, phrased so the anti-reversal
    // rule reads naturally. "observation only" is the case the rule keys on.
    private static string DescribeChangedSinceLastSend(
        ForecastSnapshot? prior, IReadOnlyList<TriggerSource> changedSinceLastSend)
    {
        if (prior is null)
            return "first send to this recipient (no prior delivered report)";
        if (changedSinceLastSend.Count == 0)
            return "nothing — no input has advanced since the last sent report";

        bool metar = changedSinceLastSend.Contains(TriggerSource.Metar);
        bool taf = changedSinceLastSend.Contains(TriggerSource.Taf);
        bool gfs = changedSinceLastSend.Contains(TriggerSource.Gfs);

        if (metar && !taf && !gfs)
            return "observation only — no new TAF or GFS run since the last sent report";

        var parts = new List<string>(3);
        if (metar) parts.Add("new observation");
        if (taf) parts.Add("new TAF");
        if (gfs) parts.Add("new GFS run");
        return string.Join("; ", parts);
    }

    // ── subtitle helper ──────────────────────────────────────────────────────

    // Ported from ClaudeClient.BuildCurrentConditionsSubtitle; same logic.
    private static string? BuildCurrentConditionsSubtitle(WeatherSnapshot snap)
    {
        var municipality = snap.StationMunicipality;
        var airportName = snap.StationName;
        var locality = snap.LocalityName;

        if (municipality is not null &&
            string.Equals(municipality, locality, StringComparison.OrdinalIgnoreCase))
            return null;

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
}