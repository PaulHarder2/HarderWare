using System.Text;
using System.Text.Json;

using MetarParser.Data.Entities;

using WxInterp;

using WxServices.Common;
using WxServices.Logging;

namespace WxReport.Svc;

/// <summary>
/// Outcome of a single per-locality reconciliation pass.  A <see cref="Success"/>
/// carries the three artifacts Claude returned (parsed final snapshot,
/// unit-neutral structured report, plain-English reasoning trace) plus
/// token-usage metadata.  The HTML email body is no longer a Claude artifact:
/// the WX-129 <see cref="StructuredReportRenderer"/> builds each recipient's
/// body deterministically from the structured report + final snapshot (WX-130),
/// so the expensive reasoning runs once per locality and fans out per recipient
/// for free.  A <see cref="Failure"/> carries a short reason string; the
/// caller is expected to log it, skip the SMTP send, and leave any
/// already-committed audit rows in place (never un-commit state).
/// </summary>
public abstract record ReconcileResult
{
    private ReconcileResult() { }

    /// <summary>Reconciliation succeeded; the three artifacts parsed cleanly.</summary>
    /// <param name="FinalSnapshot">Refined <see cref="ForecastSnapshotBody"/> after Claude reconciled provisional + TAF + observation + prior.</param>
    /// <param name="StructuredReport">Unit-neutral structured report (WX-128): language-free changes plus the language-keyed tokenized narrative the WX-129 renderer consumes.  The live rendering source as of WX-130 — its validation is fatal (a retry, then a skip/Failure), not best-effort.</param>
    /// <param name="ReasoningTrace">Brief plain-English audit log of what changed at each of the three reconciliation steps.</param>
    /// <param name="Tokens">Token-usage metadata extracted from the Anthropic API response.</param>
    public sealed record Success(
        ForecastSnapshotBody FinalSnapshot,
        StructuredReportBody StructuredReport,
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
/// Orchestrates the WX-79 forecast reconciliation pass, once per locality
/// (WX-130): builds Claude's reconciliation system prompt and user message from
/// the locality's <see cref="WeatherSnapshot"/>, the GFS-derived provisional
/// <see cref="ForecastSnapshotBody"/>, and the prior committed snapshot if
/// any; calls <see cref="ClaudeClient.InvokeReconciliationAsync"/>;
/// validates the response (<c>final_snapshot</c> via
/// <see cref="ForecastSnapshotBody.Deserialize"/>, <c>structured_report</c> via
/// <see cref="StructuredReportBody.Deserialize"/> plus the per-call narrative
/// contract); and returns either a parsed three-artifact
/// <see cref="ReconcileResult.Success"/> or a typed
/// <see cref="ReconcileResult.Failure"/>.  The call is recipient-agnostic — it
/// produces unit-neutral, multi-language content; the
/// <see cref="StructuredReportRenderer"/> applies each recipient's units,
/// language, and locale afterwards with no further LLM call.
///
/// <para>
/// Malformed-output policy (WX-79, retry added WX-110): a schema-violation,
/// missing field, or non-JSON tool input is retried up to a small bounded number
/// of attempts within the cycle (the fault is usually transient — the Anthropic
/// API treats a tool schema's <c>required</c> list as advisory, so a complete
/// response can simply drop a field); only after the attempts are exhausted does
/// it return a typed Failure.  A <c>max_tokens</c> truncation and a guaranteed-send
/// <c>skip_send</c> are <em>not</em> retried.  When Failure is ultimately returned
/// the caller (<see cref="ReportWorker"/>) does not send the email, but does not
/// un-commit any provisional rows written before the reconciler ran — the
/// auditable shape of "we tried, Claude failed" stays in place.
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
    /// snapshot (when present), via a Claude tool-use call (retried up to 3 attempts
    /// on transient malformed output — missing field / schema violation — but never
    /// on a <c>max_tokens</c> truncation or a guaranteed-send <c>skip_send</c>;
    /// returned token usage is summed across attempts).  Returns
    /// the parsed four artifacts on success or a typed failure on transport
    /// or schema problems.
    /// </summary>
    /// <param name="snapshot">Locality's <see cref="WeatherSnapshot"/>; supplies METAR, TAF periods, GFS daily summary, and per-station metadata used in the reconciliation rules.</param>
    /// <param name="provisional">GFS-derived provisional snapshot body produced by <see cref="GfsSnapshotBuilder.Build"/> (the first pass).</param>
    /// <param name="gfsModelRunUtc">UTC initialisation time of the GFS run the provisional was built from; supplied to Claude for the issuance-time comparison in reconciliation step 1.  <see langword="null"/> when no GFS data was available for the locality (provisional body will be empty in that case).</param>
    /// <param name="tafIssuanceUtc">UTC time the active TAF was issued, or <see langword="null"/> when no TAF is available.  Required for reconciliation step 1.</param>
    /// <param name="tafValidToUtc">UTC end of the TAF's validity window, or <see langword="null"/> when no TAF is available.  Helps Claude scope step 1 to in-window blocks.</param>
    /// <param name="prior">Most recently committed <see cref="ForecastSnapshot"/> for this locality's station, or <see langword="null"/> on a first send.  Drives the news judgment in reconciliation step 3.</param>
    /// <param name="narrativeLanguages">ISO 639-1 codes the structured report's narrative must carry (WX-128) — the distinct set of languages across the locality's recipients.  A returned narrative missing any of these (or carrying an extra one) fails closed.</param>
    /// <param name="tz">Locality timezone, used by <see cref="SnapshotDescriber"/> when emitting the structured observation/forecast text and by Claude when reasoning about local time.</param>
    /// <param name="changeSeverity">Severity of the trigger that caused this send (alert, update, or none).</param>
    /// <param name="previousMetarIcao">ICAO of the previous report's station, when it differs from the current snapshot's station; <see langword="null"/> when no station change occurred.</param>
    /// <param name="allowSkip">When <see langword="true"/> (unscheduled, arrival-triggered cycles), Claude may decline to send via the <c>skip_send</c> tool, yielding a <see cref="ReconcileResult.NotNews"/>.  When <see langword="false"/> (scheduled / first / startup), the send is guaranteed and skipping is not offered.</param>
    /// <param name="changedSinceLastSend">Which inputs (METAR/TAF/GFS) are newer than they were at the last report actually delivered for this locality (WX-108).  Surfaced to Claude as <c>changed_since_last_sent_report</c> so the anti-reversal rule can bind on observation-only cycles.  Empty list means nothing advanced since the last send; treated as a first send when no prior send exists.</param>
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
        IReadOnlyList<string> narrativeLanguages,
        TimeZoneInfo tz,
        ChangeSeverity changeSeverity,
        string? previousMetarIcao,
        bool allowSkip,
        IReadOnlyList<TriggerSource> changedSinceLastSend,
        CancellationToken ct = default)
    {
        var systemPrompt = BuildReconcilerSystemPrompt(
            snapshot, narrativeLanguages, changeSeverity, previousMetarIcao, allowSkip);

        var userMessage = BuildUserMessage(
            snapshot, provisional, gfsModelRunUtc, tafIssuanceUtc, tafValidToUtc, prior, tz,
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
            var apiResult = await _claude.InvokeReconciliationAsync(systemPrompt, userMessage, allowSkip, narrativeLanguages, ct);
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

                var finalSnapshotJson = RequireProperty(input, "final_snapshot").GetRawText();
                var finalSnapshot = ForecastSnapshotBody.Deserialize(finalSnapshotJson);

                // A *fresh* reconciled snapshot must carry the current schema
                // version. ForecastSnapshotBody.Deserialize stays version-lenient
                // (old persisted rows must keep loading as priors), so the
                // freshness pin lives here: Claude copying the older version
                // digit it saw in prior_snapshot fails closed through the retry
                // rather than persisting a row whose column says v4 while its
                // body JSON says v3 (WX-128 review finding).
                if (finalSnapshot.SchemaVersion != ForecastSnapshotBody.SchemaVersionCurrent)
                    throw new JsonException(
                        $"final_snapshot.schemaVersion {finalSnapshot.SchemaVersion} is not the current version {ForecastSnapshotBody.SchemaVersionCurrent}.");

                var reasoningTrace = RequireString(input, "reasoning_trace");

                // WX-130: the structured report is now the LIVE rendering source —
                // the StructuredReportRenderer builds every recipient's email from it,
                // so its validation is fatal again (it was best-effort during the
                // WX-144 additive transition, when email_body was the sent artifact).
                // A missing field, schema/token violation, or a requested language
                // absent/extra all throw and route through the retry → skip/Failure
                // path, exactly like a final_snapshot schema violation.
                var structuredReportJson = RequireProperty(input, "structured_report").GetRawText();
                var structuredReport = StructuredReportBody.Deserialize(structuredReportJson);
                ValidateNarrativeContract(structuredReport, narrativeLanguages);

                // WX-120 fall-safe, carried forward to the structured-report world:
                // a present-but-near-blank narrative — e.g. Claude submitted a report
                // when its own reasoning concluded skip_send, leaving an empty closing
                // — passes the schema (Closing is merely non-blank) but must never
                // reach a recipient. Throw the degenerate signal AFTER the schema
                // checks so a genuine schema fault surfaces as itself; on exhaustion
                // this yields a skip-with-trace on an allowSkip cycle (matching
                // Claude's skip-leaning reasoning) rather than a hard Failure.
                if (IsDegenerateNarrative(structuredReport, narrativeLanguages))
                    throw new DegenerateNarrativeException(reasoningTrace);

                return new ReconcileResult.Success(finalSnapshot, structuredReport, reasoningTrace, tokens);
            }
            catch (Exception ex) when (ex is MissingToolUseFieldException or JsonException or InvalidOperationException or DegenerateNarrativeException)
            {
                // Retryable-malformed output: re-call unless attempts are exhausted.
                if (attempt < maxAttempts)
                {
                    var what = ex switch
                    {
                        DegenerateNarrativeException => "returned a content-less narrative",
                        MissingToolUseFieldException => $"is {ex.Message}",
                        _ => $"failed validation: {ex.Message}",
                    };
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
                if (ex is DegenerateNarrativeException deg)
                {
                    // Claude returned a content-less narrative on every attempt. On a
                    // skippable cycle that matches its own (typically skip-leaning)
                    // reasoning, so treat it as a skip and keep its trace for the audit
                    // row; a guaranteed send fails closed and self-heals on the next cycle.
                    if (allowSkip)
                    {
                        Logger.Error($"Reconciliation returned a content-less narrative after {maxAttempts} attempts on a skippable cycle; treating as skip (not sent).");
                        return new ReconcileResult.NotNews(deg.ReasoningTrace, tokens);
                    }
                    Logger.Error($"Reconciliation returned a content-less narrative after {maxAttempts} attempts on a guaranteed send.");
                    return new ReconcileResult.Failure($"Reconciliation returned a content-less narrative after {maxAttempts} attempts.");
                }
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

    // WX-128/WX-130: per-call contract checks the body's intrinsic Validate()
    // cannot perform because they depend on what THIS cycle requested — the exact
    // set of languages the locality's recipients need. Every requested language
    // must be present, and no extra. Throws JsonException so failures route
    // through the existing retryable-malformed path (retry → skip/Failure),
    // exactly like a final_snapshot schema violation. (The degeneracy floor lives
    // separately in IsDegenerateNarrative: a too-thin-but-well-formed narrative
    // is the WX-120 fall-safe case, which must skip-with-trace, not hard-fail.)
    private static void ValidateNarrativeContract(
        StructuredReportBody report, IReadOnlyList<string> requestedLanguages)
    {
        // Exact-set match, both directions: a missing requested language is an
        // unrenderable report for someone; an EXTRA unrequested language is
        // unvalidated content persisting for no recipient — and a trap for a
        // renderer that iterates narrative keys (WX-128 review finding).
        foreach (var lang in report.Narrative.Keys)
            if (!requestedLanguages.Contains(lang, StringComparer.Ordinal))
                throw new JsonException($"structured_report.narrative contains unrequested language '{lang}'.");

        foreach (var lang in requestedLanguages)
            if (!report.Narrative.ContainsKey(lang))
                throw new JsonException($"structured_report.narrative is missing requested language '{lang}'.");
    }

    // WX-120 fall-safe, carried into the structured-report world (WX-130): true
    // when any requested language's narrative is near-blank. The narrative now
    // carries only the two judgment sections — the optional changeSummary band
    // and the required closing — so a genuine report's visible prose is far
    // shorter than the old whole-email_body measure, but a degenerate one (empty
    // closing, anchors only) still strips to ~0. Distinct from the schema floor:
    // a well-formed-but-thin narrative is Claude effectively skipping, so on an
    // allowSkip cycle this yields a skip-with-trace rather than a hard Failure.
    private static bool IsDegenerateNarrative(
        StructuredReportBody report, IReadOnlyList<string> requestedLanguages)
    {
        foreach (var lang in requestedLanguages)
        {
            if (!report.Narrative.TryGetValue(lang, out var sections))
                continue;  // missing-language is ValidateNarrativeContract's job, not this guard's
            int visible = ReportTokens.VisibleLength(sections.Closing)
                + ReportTokens.VisibleLength(sections.ChangeSummary ?? "");
            if (visible < MinVisibleNarrativeChars)
                return true;
        }
        return false;
    }

    // WX-130: smallest combined visible narrative length (changeSummary + closing)
    // a real report can carry per language. Recalibrated down from the WX-120
    // whole-email_body floor of 200: the narrative is now just the two judgment
    // sections, and on a steady scheduled send changeSummary is null, leaving only
    // a one-or-two-sentence closing. A real closing ("Quiet weather; no changes
    // expected.") clears 30 comfortably; a near-blank one (empty/punctuation-only,
    // which the schema's non-blank check alone would let through) strips to ~0.
    private const int MinVisibleNarrativeChars = 30;

    // Signals that Claude returned a well-formed but near-blank narrative — it
    // passed the schema (Closing is merely non-blank) but carries no real
    // forecast prose (WX-120, carried forward in WX-130). Holds the
    // reasoning_trace so a skippable-cycle skip can keep Claude's audit trail.
    private sealed class DegenerateNarrativeException : Exception
    {
        public DegenerateNarrativeException(string reasoningTrace)
            : base("content-less narrative") => ReasoningTrace = reasoningTrace;

        public string ReasoningTrace { get; }
    }

    // ── per-locality cycle instructions (system block 3) ──────────────────────

    // Builds the per-cycle, recipient-agnostic instructions (the third system
    // block). Unlike the cached guidance (block 2), this varies per cycle — the
    // requested languages, a station fallback, the trigger severity, whether
    // skipping is permitted — so it is deliberately small. The HTML layout,
    // units, and per-day grid rules that used to live here are gone: the WX-129
    // StructuredReportRenderer builds each recipient's email deterministically
    // from the structured report. Claude writes only the two judgment sections
    // (changeSummary + closing); the content rules below scope the prose those
    // sections may carry.
    private static string BuildReconcilerSystemPrompt(
        WeatherSnapshot snapshot, IReadOnlyList<string> narrativeLanguages,
        ChangeSeverity changeSeverity, string? previousMetarIcao, bool allowSkip)
    {
        var currentStationLabel = snapshot.StationMunicipality ?? snapshot.StationName ?? snapshot.StationIcao;
        var stationChangeInstruction = previousMetarIcao is not null
            ? "Note: the weather data source has changed since the last report. "
              + "The previous weather station had no recent data, "
              + $"so this report uses conditions from {currentStationLabel} instead. "
              + "Briefly acknowledge this in the narrative: on an unscheduled update, include one sentence "
              + "in the changeSummary noting the station switch; on a scheduled report, include one "
              + "sentence in the closing. Keep the tone matter-of-fact — this is routine fallback "
              + "behaviour, not a cause for concern. "
            : "";

        var changeAlertInstruction = changeSeverity switch
        {
            ChangeSeverity.Alert =>
                "This is an unscheduled weather alert — a significant and potentially dangerous change "
                + "has occurred since the last report. "
                + "For the changeSummary, write a single clear, direct sentence "
                + "identifying what changed (e.g. 'A thunderstorm has moved into the area' or "
                + "'Visibility has dropped sharply'). ",
            ChangeSeverity.Update =>
                "This is an unscheduled update — conditions have changed since the last report. "
                + "For the changeSummary, write one or two sentences summarising "
                + "what has changed (e.g. a forecast risk that has appeared, or a significant temperature shift). ",
            _ => "",
        };

        // WX-128/WX-130: the exact language set the structured report's narrative
        // must carry — the distinct languages across this locality's recipients.
        // The cached guidance defines the structured-report rules language-
        // agnostically; this line supplies the actual set, matching the required
        // keys in the tool schema.
        var narrativeLanguageInstruction =
            "The structured_report narrative must contain exactly these language keys, and no others: "
            + string.Join(", ", narrativeLanguages.Select(l => $"'{l}'"))
            + ". ";

        var skipInstruction = allowSkip
            ? "This is an unscheduled, arrival-triggered cycle. Apply the invalidation gate: if the "
              + "new evidence is not news worth sending — it confirms, or only trivially drifts from, "
              + "what the prior committed forecast already told this locality's recipients — call the "
              + "skip_send tool with a brief reasoning_trace instead of producing a report. Only call "
              + "submit_reconciled_report when the change is genuinely worth an unscheduled email. "
            : "This cycle is always worth sending — call submit_reconciled_report. Do not call skip_send. ";

        return
            "You are reconciling a weather forecast for a single locality and producing a unit-neutral "
            + "structured report for a general (non-specialist) audience. A deterministic renderer turns "
            + "your structured report into each recipient's email — you do not produce HTML, and you write "
            + "no current-conditions table or per-day forecast grid (those are rendered from the data). "
            + "Your prose is only the two judgment sections: the changeSummary band and the closing. "
            + "Narrative content rules (apply to that prose): "
            + "use only the data provided — never invent or estimate conditions. "
            + "Never show raw METAR codes, numeric precipitation rates, or CAPE values to the reader. "
            + "Never use aviation terminology — no 'ceiling', 'TAF', 'METAR', 'IFR', 'VFR', or similar. "
            + "Never include altitude or height figures in sky descriptions. "
            + "You may use TAF forecast data to inform your descriptions, but do not reference it explicitly. "
            + "Use the CAPE label to gauge thunderstorm potential and describe it in plain language — "
            + "low CAPE warrants at most a mention of an isolated storm; "
            + "significant or extreme CAPE should be described in terms of what the public might "
            + "experience (strong storms, possible damaging winds or hail). "
            + "When precipitation is forecast near freezing temperatures, consider whether "
            + "snow, sleet, or a wintry mix is possible and mention it if so. "
            + stationChangeInstruction
            + changeAlertInstruction
            + narrativeLanguageInstruction
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
        TimeZoneInfo tz, IReadOnlyList<TriggerSource> changedSinceLastSend)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Reconcile the following inputs for this locality and emit your three artifacts via the submit_reconciled_report tool.");
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

        // WX-130: the data payload is recipient-agnostic now, so it uses
        // SnapshotDescriber's default display units (US customary). The unit
        // system here is immaterial to Claude's reasoning — the final_snapshot
        // schema fixes canonical °C/kt, and the structured_report's {q:...}
        // tokens are canonical-unit by the token grammar (the renderer converts
        // per recipient), so what Claude reads in the prompt does not bind the
        // units anyone receives.
        sb.AppendLine("current_observation and current_forecast (structured text):");
        sb.AppendLine(SnapshotDescriber.Describe(snapshot, tz));
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
}