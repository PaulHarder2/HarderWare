using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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

    /// <summary>
    /// Reconciliation exhausted its retries on a contract/consistency violation,
    /// but Claude's <c>final_snapshot</c> itself parsed cleanly (WX-148): the
    /// blocks are usable, only the change narrative could not be made
    /// self-consistent. Distinct from <see cref="Failure"/> (where even the
    /// snapshot is unusable) so the caller can degrade gracefully — on a
    /// safety-critical forecast, send a narrative-less hazard report built from
    /// the parsed snapshot rather than withhold the hazard; otherwise skip and
    /// self-heal next cycle. Carries no structured report: the suspect narrative
    /// is deliberately dropped, not repaired.
    /// </summary>
    /// <param name="FinalSnapshot">The cleanly-parsed reconciled snapshot from the last attempt.</param>
    /// <param name="Tokens">Token-usage metadata summed across the attempts.</param>
    /// <param name="Reason">Short human-readable description of the consistency failure that triggered the degrade.</param>
    public sealed record Degraded(ForecastSnapshotBody FinalSnapshot, TokenUsage Tokens, string Reason) : ReconcileResult;
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
    /// <param name="reportKind">The kind of send (scheduled, unscheduled, or diagnostic) — drives the unscheduled-update change-summary instruction; the recipient-facing label is the renderer's concern.</param>
    /// <param name="previousMetarIcao">ICAO of the previous report's station, when it differs from the current snapshot's station; <see langword="null"/> when no station change occurred.</param>
    /// <param name="allowSkip">When <see langword="true"/> (unscheduled, arrival-triggered cycles), Claude may decline to send via the <c>skip_send</c> tool, yielding a <see cref="ReconcileResult.NotNews"/>.  When <see langword="false"/> (scheduled / first / startup), the send is guaranteed and skipping is not offered.</param>
    /// <param name="changedSinceLastSend">Which inputs (METAR/TAF/GFS) are newer than they were at the last report actually delivered for this locality (WX-108).  Surfaced to Claude as <c>changed_since_last_sent_report</c> so the anti-reversal rule can bind on observation-only cycles.  Empty list means nothing advanced since the last send; treated as a first send when no prior send exists.</param>
    /// <param name="significanceCfg">Significance thresholds shared with the WX-114/160 gate; supplies the freeze/heat/wind-advisory and per-tier magnitude lines the WX-189 <see cref="DeterministicChangeDetector"/> applies to temperature and wind.</param>
    /// <param name="nowUtc">Cycle timestamp; the change detector measures horizon tiers from here, matching the gate.</param>
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
        ReportKind reportKind,
        string? previousMetarIcao,
        bool allowSkip,
        IReadOnlyList<TriggerSource> changedSinceLastSend,
        SignificanceGateConfig significanceCfg,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        // WX-155: a prior snapshot from before the local-day-part rebucketing
        // (schema < current) has UTC-aligned block boundaries that don't line up
        // with the new local-aligned blocks, so a block-by-block comparison would
        // read the whole forecast as changed and could fire a spurious "everything
        // changed" blast on the deploy cycle. Drop such a prior — treat this cycle
        // as a clean baseline (first-send semantics) for both the prompt and the
        // deterministic prior-aware checks.
        if (prior is not null && prior.SchemaVersion != ForecastSnapshotBody.SchemaVersionCurrent)
        {
            Logger.Info($"Prior snapshot is schema v{prior.SchemaVersion} (current v{ForecastSnapshotBody.SchemaVersionCurrent}); treating this cycle as a baseline reset (WX-155 local-day-part rebucketing).");
            prior = null;
        }

        var systemPrompt = BuildReconcilerSystemPrompt(
            snapshot, narrativeLanguages, reportKind, previousMetarIcao, allowSkip);

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
        // WX-148: rejected attempts replayed on the next call as tool_use + tool_result,
        // so a validation retry tells Claude what was wrong instead of blindly resampling.
        var corrections = new List<ReconciliationCorrection>();
        // WX-148: the last attempt whose final_snapshot parsed cleanly. If retries
        // exhaust on a consistency/contract violation (snapshot good, narrative not),
        // this lets the caller degrade to a narrative-less hazard report rather than
        // a bare Failure. Stays null when even the snapshot never parsed.
        ForecastSnapshotBody? lastParsedSnapshot = null;
        // WX-189: the last attempt's parsed structured report (Claude's narrative, before
        // change injection) + its reasoning trace, for the independent-section degrade —
        // if retries exhaust on a PROSE fault, the caller drops ONLY the offending section
        // and sends the rest from these, rather than degrading the whole narrative.
        StructuredReportBody? lastParsedReport = null;
        string? lastReasoningTrace = null;
        // WX-151: parse the prior committed snapshot once for prior-aware change
        // verification. Version-lenient (old persisted priors must still load). A
        // malformed prior is non-fatal — log and skip the prior comparison rather
        // than fail a cycle over an old row; the check then falls back to WX-148's
        // new-only backing. Null prior is a first send (nothing to compare against).
        ForecastSnapshotBody? priorBody = null;
        if (prior is not null)
        {
            try
            {
                priorBody = ForecastSnapshotBody.Deserialize(prior.Body);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Prior snapshot body could not be parsed for prior-aware change verification ({ex.Message}); skipping the prior comparison this cycle.");
            }
        }
        for (int attempt = 1; ; attempt++)
        {
            var apiResult = await _claude.InvokeReconciliationAsync(systemPrompt, userMessage, allowSkip, narrativeLanguages, corrections, ct);
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

                // WX-148: record the snapshot as degrade-usable only AFTER the version
                // pin — a stale-version body must stay a hard Failure, never degrade
                // (degrading would persist the very column-says-v4 / body-says-v3
                // mismatch the pin exists to prevent).
                lastParsedSnapshot = finalSnapshot;

                var reasoningTrace = RequireString(input, "reasoning_trace");
                lastReasoningTrace = reasoningTrace;

                // WX-130: the structured report is now the LIVE rendering source —
                // the StructuredReportRenderer builds every recipient's email from it,
                // so its validation is fatal again (it was best-effort during the
                // WX-144 additive transition, when email_body was the sent artifact).
                // A missing field, schema/token violation, or a requested language
                // absent/extra all throw and route through the retry → skip/Failure
                // path, exactly like a final_snapshot schema violation.
                // WX-180: windKt must be sustained-only — CLAMP a folded gust out of
                // windKt.max (rather than reject → retry → degrade, which on a gusty
                // forecast never converged and degraded every cycle: the ~$45/day cost
                // incident) before it corrupts the baseline the significance gate compares
                // against. lastParsedSnapshot is refreshed so a later degrade uses the
                // corrected windKt too.
                finalSnapshot = NormalizeWindKtSustained(finalSnapshot, provisional, snapshot);
                lastParsedSnapshot = finalSnapshot;

                var structuredReportJson = RequireProperty(input, "structured_report").GetRawText();
                var structuredReport = StructuredReportBody.Deserialize(structuredReportJson);
                lastParsedReport = structuredReport;
                ValidateNarrativeContract(structuredReport, narrativeLanguages);
                ValidateProseHygiene(structuredReport, tz);
                ValidateClosingClaims(structuredReport, finalSnapshot, tz);

                // WX-189: compute the "What's changed" set deterministically from
                // (prior, final_snapshot) and inject it — Claude authored only the
                // narrative prose this cycle, so a structural phantom is impossible by
                // construction. ValidateChangeSnapshotConsistency then runs as cheap
                // defense-in-depth on the COMPUTED set: it is tautologically green
                // unless the detector itself has a bug (the validator stays the safety
                // net the WX-148/151 work built). ValidateAnchoredProseTiming retires
                // with the {chN} anchoring it depended on.
                var computedChanges = DeterministicChangeDetector.Detect(priorBody, finalSnapshot, significanceCfg, nowUtc, tz);
                structuredReport = structuredReport with { Changes = computedChanges };
                ValidateChangeSnapshotConsistency(structuredReport, finalSnapshot, priorBody, tz);

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
                    // WX-148: replay this rejected attempt to Claude on the next call with
                    // the reason, so the retry corrects the specific fault rather than
                    // blindly resampling (a no-op for the semantic faults this catches).
                    // WX-189: structural change rejections are gone — the change set is
                    // computed deterministically, not authored by Claude — so the only
                    // contract faults left for a retry are PROSE faults. A NarrativeProse
                    // rejection pins the snapshot: tell Claude to keep final_snapshot byte-
                    // identical and re-author ONLY the offending prose, which converges the
                    // retry on the actual fault instead of perturbing the (correct) snapshot.
                    // (A ChangeConsistencyException can now only come from the tautological
                    // defense-in-depth check on the COMPUTED set — a detector bug, not a
                    // Claude fault — so it falls to the generic message and self-heals/degrades;
                    // tests are the real guard there.)
                    var feedback = ex is NarrativeProseException
                        ? $"Your previous report's narrative was rejected ({ex.Message}). Keep your final_snapshot "
                          + "EXACTLY as you submitted it — do not change any block. Re-author ONLY the narrative prose "
                          + "(the changeSummary and/or the closing) to fix this, then resubmit via the tool."
                        : $"Your previous {apiResult.ToolName} was rejected ({ex.Message}). Fix only that and resubmit via the tool.";
                    corrections.Add(new ReconciliationCorrection(
                        apiResult.ToolUseId, apiResult.ToolName, apiResult.ToolUseInput, feedback));
                    Logger.Warn($"Reconciliation tool_use input {what} (attempt {attempt}/{maxAttempts}); retrying with feedback.");
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
                // WX-189 independent-section degrade: a PROSE fault that won't converge
                // across the retries drops ONLY the offending section and sends the rest.
                // The deterministic change band, the per-day grid, and the current
                // conditions are unaffected, so a closing-only fault no longer takes the
                // whole narrative (and the band) down with it. The change set is computed
                // here exactly as on the success path. (A structural/schema fault — a
                // detector-bug ChangeConsistencyException, a final_snapshot violation —
                // is NOT a NarrativeProseException and still degrades wholesale below.)
                if (ex is NarrativeProseException npe && lastParsedReport is not null && lastParsedSnapshot is not null)
                {
                    var changes = DeterministicChangeDetector.Detect(priorBody, lastParsedSnapshot, significanceCfg, nowUtc, tz);
                    var cleaned = DropProseSection(lastParsedReport, npe.Section) with { Changes = changes };
                    try
                    {
                        // Re-validate the cleaned report's SURVIVING content: the first prose
                        // fault short-circuited the validators, so the section we KEPT may never
                        // have been checked, and the success-path consistency check was skipped.
                        // If the surviving section is also faulty (a double-section fault), fall
                        // through to a wholesale degrade rather than ship it. The WX-120
                        // degeneracy floor is deliberately NOT applied here — a section degrade
                        // intentionally reduces the narrative, and the closing is always non-blank
                        // (schema-required), so a thin-but-valid survivor must still send.
                        ValidateProseHygiene(cleaned, tz);
                        ValidateClosingClaims(cleaned, lastParsedSnapshot, tz);
                        ValidateChangeSnapshotConsistency(cleaned, lastParsedSnapshot, priorBody, tz);
                    }
                    catch (Exception cleanEx) when (cleanEx is JsonException or InvalidOperationException)
                    {
                        Logger.Error($"Reconciliation section degrade left an invalid report ({cleanEx.Message}); degrading to the parsed snapshot, narrative dropped.");
                        return new ReconcileResult.Degraded(lastParsedSnapshot, tokens, cleanEx.Message);
                    }
                    Logger.Error($"Reconciliation could not make the {npe.Section} prose self-consistent after {maxAttempts} attempts ({ex.Message}); dropping that section only and sending the rest (WX-189 independent-section degrade).");
                    return new ReconcileResult.Success(lastParsedSnapshot, cleaned, lastReasoningTrace ?? string.Empty, tokens);
                }

                // WX-148: if the snapshot itself parsed cleanly and only the narrative
                // could not be made self-consistent, degrade rather than fail outright —
                // the caller can still send a narrative-less hazard report from the
                // snapshot on a safety-critical forecast.
                if (lastParsedSnapshot is not null)
                {
                    Logger.Error($"Reconciliation could not produce a self-consistent report after {maxAttempts} attempts ({ex.Message}); degrading to the parsed snapshot, narrative dropped.");
                    return new ReconcileResult.Degraded(lastParsedSnapshot, tokens, ex.Message);
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

    // WX-165: a change-consistency / change-anchored-prose rejection — the report
    // asserted a "What's changed" item (a changes[] entry) the snapshot data does not
    // support, or narrated one against the wrong window. Distinct from a bare
    // JsonException so the retry feedback can NAME the offending change and tell Claude
    // to correct-or-remove THAT change rather than re-author a fresh (often equally
    // invented) one — the fix for the "three different phantoms in three attempts"
    // non-convergence. Still a JsonException, so it routes through the identical
    // retry-then-degrade path as every other contract check; only the feedback differs.
    private sealed class ChangeConsistencyException : JsonException
    {
        public ChangeConsistencyException(string summaryToken, string message)
            : base(message) => SummaryToken = summaryToken;

        public string SummaryToken { get; }
    }

    // WX-189: a narrative-PROSE rejection (a leak, jargon, a time-word contradiction,
    // or a closing/changeSummary precip claim the snapshot leaves dry) — a fault in the
    // words Claude wrote, NOT in final_snapshot. Distinct from a bare JsonException so
    // the retry feedback can tell Claude to keep its final_snapshot EXACTLY as
    // submitted and re-author only the offending prose — Paul's "fix only that"
    // tightened toward an enforceable pin now that the snapshot is the structural
    // source of truth. Still a JsonException, so it routes through the identical
    // retry-then-degrade path; only the feedback differs.
    // Which narrative prose section a fault came from, so the WX-189 independent-section
    // degrade can drop ONLY that section on exhaustion (rather than the whole narrative).
    private enum NarrativeSection { ChangeSummary, Closing }

    private sealed class NarrativeProseException : JsonException
    {
        public NarrativeProseException(NarrativeSection section, string message) : base(message) => Section = section;

        public NarrativeSection Section { get; }
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

    // WX-148: cross-artifact consistency between the change narrative and the
    // final_snapshot. The "What's changed" prose is driven by changes[].window,
    // while the deterministic day-grid is built from the snapshot blocks; nothing
    // else reconciles the two, so they can disagree (the 6/9 "afternoon" narrative
    // over a forecast whose only rain sat in the block the grid calls "morning").
    // The rules below all fail closed via JsonException so they route through the
    // same retry-then-fail path as the other contract checks:
    //   1. Every change window aligns to a snapshot block boundary (a local day-part
    //      boundary, WX-155). This forbids sub-block precision — a block-aligned window cannot claim timing
    //      the blocks don't support, which is exactly what the 6/9 17-21Z window did.
    //   2. (WX-151, generalizing the original WX-148 new-only backing) Every precip
    //      (or standalone Severe) change must be a REAL difference versus the PRIOR
    //      snapshot in its window: APPEARING/STRENGTHENING requires the in-window
    //      expectation to exceed the prior's (or this phenomenon's severeFlag to rise),
    //      WEAKENING/CLEARING requires it to fall (or severe to drop) — and only when
    //      the prior actually covered the window (else there's nothing to weaken from).
    //      A change identical in prior and new is a phantom (send 1977). Non-precip
    //      phenomena (wind/windshift/fog/temperature) have no clean snapshot-comparable
    //      semantics and stay prompt-governed. WX-149 adds the tier/raw-UTC/prose
    //      assertions on top of this scaffold.
    // Internal (not private) so the WX-189 detector tests can assert the keystone
    // invariant directly: every change DeterministicChangeDetector emits passes this,
    // its inverse — the defense-in-depth net stays tautologically green.
    internal static void ValidateChangeSnapshotConsistency(
        StructuredReportBody report, ForecastSnapshotBody finalSnapshot, ForecastSnapshotBody? prior, TimeZoneInfo tz)
    {
        foreach (var change in report.Changes)
        {
            var w = change.Window;
            if (!IsBlockAligned(w.StartUtc, tz) || !IsBlockAligned(w.EndUtc, tz) || w.EndUtc <= w.StartUtc)
                throw new ChangeConsistencyException(change.SummaryToken,
                    $"structured_report change '{change.SummaryToken}' window {w.StartUtc:O}..{w.EndUtc:O} is not "
                    + "aligned to a snapshot block boundary (a local 00/06/12/18 day-part boundary); change windows must "
                    + "coincide with block boundaries so the narrative cannot claim timing finer than the blocks support.");

            // WX-151: prior-aware change verification. A change must correspond to a
            // REAL prior->new difference of its claimed direction within its window,
            // or it is a phantom — the reader is told something happened that did not
            // (send 1977: a storm "downgrade" + rain "appearing" over a cycle whose
            // only movement was sky-cover wobble; prior == new on every precip block).
            // This GENERALIZES WX-148's new-only backing: appearing/strengthening now
            // also requires the prior LACKED it (a change identical in prior and new
            // is not news), and weakening/clearing — which WX-148 exempted outright —
            // requires the prior actually carried it. The strength axis is
            // precipExpectation (None<Possible<Likely<Certain) OR the block severeFlag,
            // so a thunderstorm whose expectation is flat but whose severeFlag rises
            // false->true still counts as strengthening (the WX-148 worked example).
            // Null prior is a first send: priorE is None and there is no prior severe,
            // so the check degenerates to WX-148's new-only backing (appearing/
            // strengthening must be carried by the new snapshot; weakening/clearing
            // has nothing to have weakened from and is rejected). Covers precipitation
            // phenomena and the standalone Severe phenomenon (WX-151 scope decision);
            // wind/windshift/temperature/fog have no clean snapshot-comparable
            // appearing semantics and stay prompt-governed (the documented residual).
            // Weakening/clearing is only verifiable when the prior actually covered
            // this window — otherwise there is nothing to have weakened from (a first
            // send with a null prior, or a far-horizon window past the prior's reach).
            // Without coverage the validator can't disprove the change, so it doesn't
            // reject it (the WX-149 "never reject what you can't prove wrong" policy);
            // appearing/strengthening still get new-snapshot backing via newE/newSevere,
            // preserving WX-148's behaviour on a first send.
            bool priorCoversWindow = prior is not null && PriorFullyCoversWindow(prior, w);

            if (TryMapPrecip(change.Phenomenon, out var precip))
            {
                int newE = (int)MaxExpect(finalSnapshot, precip, w);
                int priorE = prior is null ? (int)PrecipExpectation.None : (int)MaxExpect(prior, precip, w);
                // Severe is a PER-PHENOMENON strength axis: a block carrying THIS
                // phenomenon whose severeFlag is set. Using window-level severe would
                // let an unrelated severe block (a thunderstorm) vouch for a different
                // phenomenon's change (a phantom "snow strengthening").
                bool newSevere = SevereForPhenomenon(finalSnapshot, precip, w);
                bool priorSevere = prior is not null && SevereForPhenomenon(prior, precip, w);
                bool real = change.Direction switch
                {
                    ChangeDirection.Appearing or ChangeDirection.Strengthening => newE > priorE || (newSevere && !priorSevere),
                    ChangeDirection.Weakening or ChangeDirection.Clearing => !priorCoversWindow || newE < priorE || (priorSevere && !newSevere),
                    _ => true,  // Shifting (WX-111 wind vector) is not a precip-intensity change; prompt-governed
                };
                if (!real)
                    throw new ChangeConsistencyException(change.SummaryToken,
                        $"structured_report change '{change.SummaryToken}' ({change.Phenomenon} {change.Direction}, "
                        + $"window {w.StartUtc:O}..{w.EndUtc:O}) is not a real change versus prior_snapshot: in-window "
                        + $"{precip} expectation moved prior->{(PrecipExpectation)priorE}->new->{(PrecipExpectation)newE} "
                        + $"and severeFlag prior={priorSevere}/new={newSevere} do not support '{change.Direction}'. "
                        + "The narrative would announce a change that did not occur.");
            }
            else if (change.Phenomenon == ChangePhenomenon.Severe)
            {
                // The standalone Severe phenomenon is window-level (any severe block),
                // not tied to a precip phenomenon.
                bool newSevere = AnySevereInWindow(finalSnapshot, w);
                bool priorSevere = prior is not null && AnySevereInWindow(prior, w);
                bool real = change.Direction switch
                {
                    ChangeDirection.Appearing or ChangeDirection.Strengthening => newSevere && !priorSevere,
                    ChangeDirection.Weakening or ChangeDirection.Clearing => !priorCoversWindow || (priorSevere && !newSevere),
                    _ => true,
                };
                if (!real)
                    throw new ChangeConsistencyException(change.SummaryToken,
                        $"structured_report change '{change.SummaryToken}' (Severe {change.Direction}, "
                        + $"window {w.StartUtc:O}..{w.EndUtc:O}) is not a real change versus prior_snapshot: in-window "
                        + $"severeFlag is prior={priorSevere}, new={newSevere}, which does not support '{change.Direction}'. "
                        + "The narrative would announce a severe change that did not occur.");
            }

            // WX-149 (b): tier over-escalation. A Safety-tier change must be backed
            // by a block in its window that actually carries a safety-grade signal —
            // the snapshot's severeFlag (severe convection or wind >= 50 kt, per
            // GfsSnapshotBuilder.DeriveSevereFlag), freezing precip or snow, or
            // sustained wind >= 34 kt (tropical-storm force, the significance
            // hierarchy's bright line for non-thunderstorm wind). This catches
            // defect 4 (send 1938): "possible rain" emitted at the safety tier with
            // no severe block to back it. severeFlag is deliberately NOT the sole
            // criterion: it is narrow (it never encodes dense fog, freezing precip,
            // or 34-49 kt winds the hierarchy still calls safety-critical), so a bare
            // "safety => severeFlag" rule would reject legitimate ice/wind safety
            // sends. Phenomena the snapshot cannot encode a safety signal for at all
            // — fog/haze/smoke/dust (Obscuration is reserved, always None today) and
            // temperature (no safety threshold) — are exempt and lean on the prompt
            // rule (WX-149 documented residual). Gated on APPEARING/STRENGTHENING
            // exactly as the precip-backing check above: a safety hazard WEAKENING
            // or CLEARING is legitimate safety-tier news (the significance hierarchy
            // counts a "newly removed hazard" as news at any horizon), and the new
            // snapshot correctly no longer carries the signal — checking those would
            // false-reject a real "the ice threat has lifted" send.
            if (change.Tier == ChangeTier.Safety
                && change.Direction is ChangeDirection.Appearing or ChangeDirection.Strengthening
                && SafetyTierIsVerifiable(change.Phenomenon))
            {
                bool safetyBacked = finalSnapshot.Blocks.Any(b =>
                    BlockOverlapsWindow(b.StartUtc, w)
                    && (b.SevereFlag
                        || b.PrecipPhenomenon is PrecipPhenomenon.FreezingPrecip or PrecipPhenomenon.Snow
                        || b.WindKt.Max >= WxThresholds.SafetyWindKt));
                if (!safetyBacked)
                    throw new ChangeConsistencyException(change.SummaryToken,
                        $"structured_report change '{change.SummaryToken}' is tier '{change.Tier}' "
                        + $"({change.Phenomenon}) but no final_snapshot block in its window "
                        + $"{w.StartUtc:O}..{w.EndUtc:O} carries a safety-grade signal (severeFlag, "
                        + $"freezing/snow precip, or sustained wind >= {WxThresholds.SafetyWindKt} kt); the tier is over-escalated.");
            }
        }
    }

    // True when the final_snapshot can deterministically carry a safety-grade
    // signal for this phenomenon, so a Safety-tier claim is checkable against the
    // blocks. Fog/haze/smoke/dust have no block field (Obscuration is reserved,
    // always None today) and temperature has no safety threshold, so a Safety-tier
    // change naming one of those is exempt from the backing check and governed by
    // the prompt rule alone (WX-149 documented residual). Severe, the precip
    // phenomena, and wind/wind-shift remain verifiable.
    private static bool SafetyTierIsVerifiable(ChangePhenomenon p) => p is not (
        ChangePhenomenon.Fog or ChangePhenomenon.Haze or ChangePhenomenon.Smoke
        or ChangePhenomenon.Dust or ChangePhenomenon.Temperature);

    // WX-160: windKt carries SUSTAINED wind only — a gust belongs in the narrative
    // {q:gust} token, never in windKt. Claude has historically folded a TAF/observed
    // gust into windKt.max ("12 kt G20 kt" → max 20), which corrupts the stored
    // baseline and makes the significance gate's sustained-wind comparison
    // apples-to-oranges (a GFS-sustained current vs a gust-laden prior). The ceiling
    // each block's windKt.max is pinned to is the SAME GFS+TAF sustained merge the
    // gate compares against (TafBlockProjector.Merge) — read off the merged body
    // rather than re-derived here, so the validator's TAF-coverage model is identical
    // to the gate's by construction (the two diverged when this recomputed per-period
    // overlap while Merge uses prevailing-timeline persistence). The current
    // observation's sustained wind raises the ceiling only for the block that contains
    // it. An overshoot beyond ceiling + WxThresholds.SustainedCeilingToleranceKt is a
    // folded gust (or an invented wind); it fails closed through the same retry-with-
    // feedback path as every other contract check, with a message telling Claude to
    // move the gust to the narrative.
    /// <summary>
    /// WX-180: enforce the windKt-is-sustained-only invariant by <b>clamping</b> a
    /// folded gust out of <c>windKt.max</c>, rather than rejecting it. The merged
    /// GFS+TAF body's <c>windKt.max</c> (plus any in-block observation's sustained
    /// wind) is the per-block sustained ceiling; whenever Claude returns a
    /// <c>windKt.max</c> above that ceiling (a folded gust), it is corrected down to
    /// the ceiling.
    /// <para>
    /// WX-160 originally <em>rejected</em> a folded gust (throw → retry → degrade). On
    /// a gusty forecast Claude could not converge across the attempts, so the locality
    /// degraded every cycle and re-burned reconciliations — the ~$45/day cost incident
    /// (WX-180). The ceiling IS the correct sustained value, so clamping yields the
    /// same invariant deterministically with no retry. The gust still belongs in the
    /// narrative <c>{q:gust}</c> token; clamping never touches the narrative.
    /// </para>
    /// Returns the (possibly corrected) body — the same reference when nothing was clamped.
    /// </summary>
    private static ForecastSnapshotBody NormalizeWindKtSustained(
        ForecastSnapshotBody finalSnapshot, ForecastSnapshotBody provisional, WeatherSnapshot snapshot)
    {
        // The merged body's windKt.max IS the sustained ceiling per block (TAF-prevails
        // where covered, GFS elsewhere, gust excluded), matching what the gate sees.
        var ceilingBody = TafBlockProjector.Merge(provisional, snapshot.ForecastPeriods, snapshot.TafValidToUtc);
        var ceilingByStart = new Dictionary<DateTime, int>(ceilingBody.Blocks.Count);
        foreach (var b in ceilingBody.Blocks)
            ceilingByStart[b.StartUtc] = b.WindKt.Max;

        List<ForecastSnapshotBlock>? corrected = null;
        for (int i = 0; i < finalSnapshot.Blocks.Count; i++)
        {
            var block = finalSnapshot.Blocks[i];
            int ceiling = ceilingByStart.TryGetValue(block.StartUtc, out var m) ? m : int.MinValue;

            // The current observation's SUSTAINED wind (never its gust) raises the
            // ceiling ONLY for the block containing the observation instant — Claude may
            // legitimately track a strong obs there. Applying it to every block would let
            // a windy "now" mask a folded gust in a far-future block.
            if (snapshot.ObservationAvailable && snapshot.WindSpeedKt is int obsKt
                && block.StartUtc <= snapshot.ObservationTimeUtc
                && snapshot.ObservationTimeUtc < block.StartUtc.AddHours(GfsSnapshotBuilder.BlockHours))
                ceiling = Math.Max(ceiling, obsKt);

            // No sustained source covers this block (e.g. a far-horizon block past the
            // GFS/TAF reach): nothing to pin against, so leave it. This guard MUST
            // precede the tolerance addition below — otherwise int.MinValue + tolerance
            // underflows.
            if (ceiling == int.MinValue) continue;

            if (block.WindKt.Max > ceiling + WxThresholds.SustainedCeilingToleranceKt)
            {
                Logger.Debug(
                    $"windKt.max {block.WindKt.Max} kt exceeds the sustained ceiling {ceiling} kt for block "
                    + $"{block.StartUtc:O} (folded gust); clamping windKt.max to {ceiling} kt (WX-180). "
                    + "The gust belongs in the narrative {q:gust} token.");
                corrected ??= [.. finalSnapshot.Blocks];
                // Lower Min too if it sits above the new ceiling, so the clamp can never
                // invert the band (Min > Max) — downstream (the significance gate compares
                // both endpoints, Serialize, and the degrade artifact) assumes Min <= Max.
                corrected[i] = block with { WindKt = block.WindKt with { Min = Math.Min(block.WindKt.Min, ceiling), Max = ceiling } };
            }
        }

        return corrected is null ? finalSnapshot : finalSnapshot with { Blocks = corrected };
    }

    // WX-149: prose hygiene over the language-keyed narrative — two reader-facing
    // faults the structured-schema and the change/snapshot consistency checks
    // cannot see, because they live in the prose itself. Both fail closed via
    // JsonException so they route through the same retry-with-feedback → degrade
    // path as every other contract check. Applied to BOTH narrative sections
    // (changeSummary and closing); a leak or a contradiction is just as wrong in
    // the closing wrap-up as in the change band.
    private static void ValidateProseHygiene(StructuredReportBody report, TimeZoneInfo tz)
    {
        foreach (var (lang, sections) in report.Narrative)
        {
            CheckProse(lang, NarrativeSection.ChangeSummary, sections.ChangeSummary, tz);
            CheckProse(lang, NarrativeSection.Closing, sections.Closing, tz);
        }
    }

    // WX-189: returns a copy of the report with one prose section dropped across EVERY
    // language — changeSummary → null (the renderer then shows the deterministic band
    // fallback built from the computed changes), closing → a short, snapshot-safe
    // localized line (the schema requires a non-blank closing). Used by the
    // independent-section degrade so a fault in one section never takes the whole
    // narrative down. All languages are cleaned uniformly: ValidateClosingClaims is
    // English-only, so a non-English same-section fault is untested and is best treated
    // the same conservative way.
    private static StructuredReportBody DropProseSection(StructuredReportBody report, NarrativeSection section)
    {
        var narrative = new Dictionary<string, NarrativeSections>(report.Narrative.Count, StringComparer.Ordinal);
        foreach (var (lang, sections) in report.Narrative)
            narrative[lang] = section == NarrativeSection.ChangeSummary
                ? sections with { ChangeSummary = null }
                : sections with { Closing = ReportVocabulary.ForLanguage(lang).ClosingFallback };
        return report with { Narrative = narrative };
    }

    // Matches any {...} token; used to mask tokens to equal-length blanks before
    // scanning prose, so a token's interior (a {q:time:...Z} trailing Z, a decimal
    // inside {q:pressure:1013.2}) cannot be mistaken for a leak or a sentence end.
    private static readonly Regex BraceToken = new(@"\{[^}]*\}", RegexOptions.Compiled);
    // Raw 6-hour-grid shorthand: a 1-2 digit hour immediately followed by a Z on a
    // word boundary ("18Z", and each half of "12-18Z"). Case-insensitive so a
    // lower-cased leak ("18z") is caught too (CodeRabbit, PR #87). No space between
    // digits and Z — the real leak has none, and allowing one only widens the
    // false-positive surface ("5 Zulu", a number adjacent to a Z-word).
    private static readonly Regex RawUtcBlock =
        new(@"\d{1,2}Z\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // WX-154: internal data-source / aviation acronyms that must never appear in
    // recipient prose. TAF/METAR/GFS/ICAO have no lowercase homograph, so they are
    // matched case-INsensitively — a lowercased leak ("the latest metar…") is caught
    // too, matching the sibling RawUtcBlock policy. CAPE is matched case-SENSITIVELY
    // because "cape" the landform is a common word. Residual: an all-caps place name
    // in prose ("CAPE COD") would trip the CAPE arm — accepted, since Claude rarely
    // all-caps prose and locality names are injected by the renderer AFTER this check.
    private static readonly Regex JargonToken =
        new(@"\b((?i:TAF|METAR|GFS|ICAO)|CAPE)\b", RegexOptions.Compiled);
    // {q:time:<ISO-8601 UTC>} — the only sanctioned way to express an instant in
    // prose. Captures the inner timestamp so we can render it to a local hour.
    private static readonly Regex QTimeToken = new(@"\{q:time:([^}]+)\}", RegexOptions.Compiled);

    private static void CheckProse(string lang, NarrativeSection section, string? prose, TimeZoneInfo tz)
    {
        if (string.IsNullOrEmpty(prose))
            return;

        // Mask every {...} token to equal-length blanks so none of the three scans
        // can see inside a token: the raw-UTC scan ignores a {q:time:...Z} token's
        // own trailing Z, the sentence-boundary scan ignores a decimal inside
        // {q:pressure:1013.2}, and the day-part word search ignores token interiors
        // — while token offsets still line up with the original (same length).
        string masked = BraceToken.Replace(prose, m => new string(' ', m.Length));

        // (c) Raw internal UTC block-notation leak (defect 3, send 1938:
        // "...the Wednesday afternoon block (12-18Z) has shifted..."). Internal
        // 6-hour-grid shorthand must never reach the reader; instants are emitted
        // only as {q:time:...} tokens the renderer localizes.
        var leak = RawUtcBlock.Match(masked);
        if (leak.Success)
            throw new NarrativeProseException(section,
                $"structured_report narrative '{lang}' leaks raw UTC block notation "
                + $"('{leak.Value.Trim()}') into recipient prose; express instants only as "
                + "{q:time:...} tokens, never the internal NNZ block shorthand.");

        // WX-154: internal data-source / aviation jargon must never reach the reader —
        // name no data source (TAF/METAR/GFS/CAPE/ICAO); use plain wording ("indications").
        var jargon = JargonToken.Match(masked);
        if (jargon.Success)
            throw new NarrativeProseException(section,
                $"structured_report narrative '{lang}' uses the internal/aviation term "
                + $"'{jargon.Value}' in recipient prose; never name a data source "
                + "(TAF/METAR/GFS/CAPE/ICAO) — use plain wording such as 'the latest indications'.");

        // (2) Prose time-of-day word must agree with its {q:time} token's LOCAL
        // rendering (defect 2, send 1927: "...develop Saturday afternoon,
        // {q:time:...T12:00:00Z}" — that token renders to 7:00 AM, i.e. morning).
        // For each token, bucket its local hour into a day part and compare it
        // against the nearest unambiguous day-part word DIRECTLY associated with
        // the token. Only an unambiguous contradiction rejects (see DayPartWords
        // and NearestDayPartWord) — the validator must never reject prose it cannot
        // prove wrong. The {q:time} match runs on the original prose to read the
        // timestamp; its offsets index the equal-length masked string.
        foreach (Match m in QTimeToken.Matches(prose))
        {
            if (!DateTime.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var instantUtc))
                continue;  // token-grammar validation is ReportTokens' job; skip an unparseable instant here

            int localHour = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(instantUtc, DateTimeKind.Utc), tz).Hour;
            int tokenPart = DayPartOfHour(localHour);

            var (word, wordPart) = NearestDayPartWord(masked, m.Index, m.Index + m.Length);
            if (word is not null && wordPart != tokenPart)
                throw new NarrativeProseException(section,
                    $"structured_report narrative '{lang}' says \"{word}\" next to a {{q:time}} "
                    + $"token that renders to the {DayPartName(tokenPart)} (local hour {localHour}); "
                    + "the prose time-of-day word contradicts the token's local rendering.");
        }
    }

    // Day-part buckets, mirroring StructuredReportRenderer.PartOf so the validator
    // reads a {q:time} clock exactly as the renderer (and the reader) will:
    // 0 overnight (00-06), 1 morning (06-12), 2 afternoon (12-18), 3 evening (18-24).
    private static int DayPartOfHour(int hour) => hour switch
    {
        >= 6 and < 12 => 1,
        >= 12 and < 18 => 2,
        >= 18 => 3,
        _ => 0,
    };

    private static string DayPartName(int part) => part switch
    {
        1 => "morning",
        2 => "afternoon",
        3 => "evening",
        _ => "overnight",
    };

    // UNAMBIGUOUS day-part words only. Words whose local bucket is genuinely
    // ambiguous are deliberately omitted so the check cannot false-reject: English
    // "tonight"/"night" (evening OR overnight), and the Spanish "mañana" (morning
    // OR tomorrow), "tarde" (afternoon OR evening), "noche" (evening OR night).
    // English covers the common case and the 6/13 Spring repro; Spanish
    // contributes only the unambiguous pre-dawn "madrugada". The residual leans on
    // the prompt rule (WX-149 documented residual). Whole-word, case-insensitive.
    private static readonly (string Word, int Part)[] DayPartWords =
    {
        ("overnight", 0),
        ("morning", 1),
        ("afternoon", 2),
        ("evening", 3),
        ("madrugada", 0),
    };

    // The unambiguous day-part word that DIRECTLY governs the token: searched only
    // within the token's sentence, and only a word whose gap to the token holds no
    // other word (no letters — just whitespace and punctuation) qualifies. That
    // keeps the check to words the reader plainly attaches to this instant ("...
    // Saturday afternoon, {q:time}") and rejects a day-part word bound to a
    // DIFFERENT time reference in a compound sentence ("...Friday evening, then
    // redevelop {q:time} Saturday..."), which the validator cannot prove wrong.
    // Returns (null, -1) when no such word is directly associated. The trade is
    // recall on the far side of a connector ("{q:time} in the afternoon") — that
    // residual leans on the prompt rule, consistent with the conservative design.
    private static (string? Word, int Part) NearestDayPartWord(string prose, int tokenStart, int tokenEnd)
    {
        int sentStart = SentenceStart(prose, tokenStart);
        int sentEnd = SentenceEnd(prose, tokenEnd);

        (string? Word, int Part) best = (null, -1);
        int bestDist = int.MaxValue;
        foreach (var (word, part) in DayPartWords)
        {
            int from = sentStart;
            while (from < sentEnd)
            {
                int idx = prose.IndexOf(word, from, sentEnd - from, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                    break;
                int after = idx + word.Length;
                bool wholeWord = (idx == 0 || !char.IsLetter(prose[idx - 1]))
                    && (after >= prose.Length || !char.IsLetter(prose[after]));
                if (wholeWord)
                {
                    // Gap between the word and the token (they never overlap — a
                    // day-part word cannot sit inside a masked token). The word
                    // governs the token only when that gap holds no other word.
                    (int gapStart, int gapEnd) = after <= tokenStart
                        ? (after, tokenStart)
                        : (tokenEnd, idx);
                    int dist = gapEnd - gapStart;
                    if (dist < bestDist && !HasLetter(prose, gapStart, gapEnd))
                    {
                        bestDist = dist;
                        best = (prose.Substring(idx, word.Length), part);
                    }
                }
                from = after;
            }
        }
        return best;
    }

    private static bool HasLetter(string s, int start, int end)
    {
        for (int i = start; i < end; i++)
            if (char.IsLetter(s[i]))
                return true;
        return false;
    }

    private static int SentenceStart(string prose, int pos)
    {
        for (int i = pos - 1; i >= 0; i--)
            if (prose[i] is '.' or '!' or '?')
                return i + 1;
        return 0;
    }

    private static int SentenceEnd(string prose, int pos)
    {
        for (int i = pos; i < prose.Length; i++)
            if (prose[i] is '.' or '!' or '?')
                return i;
        return prose.Length;
    }

    // A day-part word immediately preceded by one of these is pinned to a SPECIFIC
    // (often different) day than the change's window — "Friday evening", "tomorrow
    // morning" — which we cannot compare to the window's local buckets without
    // day-level parsing, so we conservatively skip it (never reject prose we can't
    // prove wrong — the WX-149 policy). An unqualified "this evening" / "by evening"
    // still refers to the change and is checked.
    private static readonly string[] DayQualifiers =
    {
        "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday",
        "today", "tonight", "tomorrow", "yesterday",
    };

    // True when the word ending at the letters before wordStart is a DayQualifier.
    // Bounded by sentStart so the scan never crosses into a previous sentence — a
    // "Friday." ending the prior sentence must not qualify a word in this one.
    private static bool QualifiedByOtherDay(string prose, int sentStart, int wordStart)
    {
        int i = wordStart - 1;
        while (i >= sentStart && !char.IsLetter(prose[i]))
            i--;
        if (i < sentStart)
            return false;
        int end = i + 1;
        while (i >= sentStart && char.IsLetter(prose[i]))
            i--;
        var prev = prose.Substring(i + 1, end - (i + 1));
        return DayQualifiers.Contains(prev, StringComparer.OrdinalIgnoreCase);
    }

    // WX-152: the closing ("In summary:") is the one narrative section the other
    // checks don't reconcile against the snapshot — ValidateChangeSnapshotConsistency
    // works on changes[], and the {q:time} / {chN}-anchored prose checks cover only the
    // changeSummary band. So the closing can assert a precipitation/storm EVENT the
    // final_snapshot doesn't carry (send 1995: "a modest chance of a storm tonight" over
    // a snapshot dry on every block from this evening on). This catches the clear case:
    // a sentence that ASSERTS a precip phenomenon (not negated) at a resolvable local
    // time the snapshot leaves entirely dry. Conservative by design — negated ("stays
    // dry", "no rain expected"), un-timed ("any storm that develops"), and weekday-pinned
    // references are skipped and lean on the prompt rule (the WX-149/151 residual policy).
    // Precip-vs-dry only: a wrong-phenomenon claim (snow vs rain) at a wet time is not
    // caught. Distinct from WX-139 (synoptic mechanisms/fronts the schema can't carry) —
    // here the schema CAN represent the event and the blocks contradict it. Fail-closed
    // via JsonException → retry → tier-aware degrade.
    //
    // ENGLISH-ONLY by design: the phenomenon/negation/time lexicons below are English,
    // so a non-English (es) closing matches no precipitation word and is never
    // evaluated — safe (no false reject) but unguarded, leaning on the language-agnostic
    // prompt rule. Deterministic Spanish parity is harder ("mañana" = tomorrow OR
    // morning, gendered "seco/seca", "esta noche/tarde") and is the standing
    // WX-149/151/152 multi-language residual, deferred to that work.
    private static void ValidateClosingClaims(StructuredReportBody report, ForecastSnapshotBody finalSnapshot, TimeZoneInfo tz)
    {
        if (finalSnapshot.Blocks.Count == 0)
            return;
        // Reference local day for relative words ("tonight"/"today"): the first block's
        // local date (≈ the cycle's "now", no separate wall-clock dependency).
        var firstUtc = finalSnapshot.Blocks.Min(b => b.StartUtc);
        var refDate = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(firstUtc, DateTimeKind.Utc), tz));

        foreach (var (lang, sections) in report.Narrative)
        {
            // WX-189: check BOTH judgment sections. The closing was the original WX-152
            // target; the changeSummary band — now that the structural changes[] are
            // computed deterministically but the band PROSE is still Claude's — can
            // likewise assert a precip event the snapshot leaves dry, so it gets the
            // same precip-vs-dry guard (the residual prose-phantom surface Option C left).
            CheckProseClaims(lang, NarrativeSection.ChangeSummary, sections.ChangeSummary, refDate, finalSnapshot, tz);
            CheckProseClaims(lang, NarrativeSection.Closing, sections.Closing, refDate, finalSnapshot, tz);
        }
    }

    // Scans one prose section sentence-by-sentence for a precip/storm assertion at a
    // local time the snapshot leaves entirely dry. Section-agnostic, so the WX-152
    // closing check and the WX-189 changeSummary check share one body.
    private static void CheckProseClaims(
        string lang, NarrativeSection section, string? prose, DateOnly refDate, ForecastSnapshotBody finalSnapshot, TimeZoneInfo tz)
    {
        if (string.IsNullOrEmpty(prose))
            return;
        // Mask {...} tokens (a {q:time} instant is the WX-149 check's job, not this one's).
        var masked = BraceToken.Replace(prose, m => new string(' ', m.Length));
        int from = 0;
        while (from < masked.Length)
        {
            int end = SentenceEnd(masked, from);
            CheckClosingSentence(lang, section, masked, from, end, refDate, finalSnapshot, tz);
            from = end + 1;
        }
    }

    private static void CheckClosingSentence(
        string lang, NarrativeSection section, string masked, int start, int end, DateOnly refDate, ForecastSnapshotBody finalSnapshot, TimeZoneInfo tz)
    {
        // Must assert a precipitation/storm phenomenon...
        if (!ContainsAnyWord(masked, start, end, ClosingPrecipWords))
            return;
        // ...not in a negated / "stays dry" context, nor a CESSATION one where the
        // time word is a deadline by which precip ENDS rather than where it occurs
        // ("rain tapers off by evening", "showers ending tonight"). Both are
        // conservative skips — a few missed catches beats a false reject of a real send.
        if (ContainsAnyWord(masked, start, end, ClosingNegationCues)
            || ContainsAnyWord(masked, start, end, ClosingCessationCues)
            || masked.IndexOf("n't", start, end - start, StringComparison.OrdinalIgnoreCase) >= 0)
            return;
        // ...at exactly one resolvable local time reference.
        var window = ResolveClosingTime(masked, start, end, refDate);
        if (window is null)
            return;

        // Reject only when the snapshot HAS blocks in that window and they are ALL dry
        // (a window past the horizon matches no blocks → can't verify → skip).
        bool any = false, anyWet = false;
        foreach (var b in finalSnapshot.Blocks)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(b.StartUtc, DateTimeKind.Utc), tz);
            if (window(DateOnly.FromDateTime(local), local.Hour))
            {
                any = true;
                if (b.PrecipExpectation != PrecipExpectation.None)
                    anyWet = true;
            }
        }
        if (any && !anyWet)
            throw new NarrativeProseException(section,
                $"structured_report narrative '{lang}' asserts precipitation/storm activity at a local time "
                + "the final_snapshot leaves entirely dry; the prose summarizes the conditions, day-grid, and "
                + "changes and must not introduce a forecast the snapshot does not carry.");
    }

    // Precipitation/storm phenomenon words the closing could assert. (A precip word
    // in an idiom — "weather the storm", "calm before the storm" — is a rare residual:
    // it can't be told from a literal claim deterministically, and the literal-forecast
    // prompt makes it unlikely.)
    private static readonly string[] ClosingPrecipWords =
    {
        "rain", "rains", "raining", "rainy", "shower", "showers", "thundershower", "thundershowers",
        "storm", "storms", "stormy", "thunderstorm", "thunderstorms", "thunder", "snow", "snows",
        "snowing", "snowy", "snowfall", "snowstorm", "snowstorms", "flurries", "flurry", "wintry",
        "sleet", "drizzle", "hail", "downpour", "downpours",
    };

    // Sentence-level negation / "dry" cues — their presence makes the sentence too
    // likely a "stays dry" / "no rain" statement to safely treat as an assertion.
    private static readonly string[] ClosingNegationCues =
    {
        "no", "not", "without", "dry", "nothing", "none", "absent", "lacking",
    };

    // Cessation cues — the sentence describes precip ENDING ("tapers off this evening",
    // "showers ending by tonight", "rain clears this afternoon"), so the named time is
    // a deadline, not where the precip lives; skip rather than read it as a wet-time
    // claim against a (correctly) dry block.
    private static readonly string[] ClosingCessationCues =
    {
        "ending", "ends", "ended", "taper", "tapers", "tapering", "tapered", "clearing", "clears",
        "cleared", "diminishing", "diminishes", "subsiding", "subsides", "departing", "exiting", "fading",
    };

    // Resolves the sentence's local time reference to a (localDate, localHour) block
    // predicate, or null when there is none, more than one (ambiguous), or it is pinned
    // to a specific weekday/other day (residual — lean on the prompt). refDate is "today".
    // The hour ranges match StructuredReportRenderer.PartOf and the predicate buckets a
    // block by its LOCAL START hour — deliberately, so the closing is checked against the
    // very day-part the reader's grid places the block in (a block whose start hour lands
    // in "afternoon" is the grid's afternoon even if it spills an hour into "evening").
    private static Func<DateOnly, int, bool>? ResolveClosingTime(string masked, int start, int end, DateOnly refDate)
    {
        var next = refDate.AddDays(1);
        Func<DateOnly, int, bool>? found = null;
        int matches = 0;
        void Take(Func<DateOnly, int, bool> p)
        {
            found = p;
            matches++;
        }

        if (HasWord(masked, start, end, "tonight"))
            Take((d, h) => (d == refDate && h >= 18) || (d == next && h < 6));
        if (HasWord(masked, start, end, "today"))
            Take((d, _) => d == refDate);
        if (HasWord(masked, start, end, "tomorrow"))
            Take((d, _) => d == next);
        TakeDayPart(masked, start, end, "morning", refDate, 6, 12, Take);
        TakeDayPart(masked, start, end, "afternoon", refDate, 12, 18, Take);
        TakeDayPart(masked, start, end, "evening", refDate, 18, 24, Take);

        return matches == 1 ? found : null;
    }

    // A bare day-part word maps to TODAY's bucket — unless it is pinned to a weekday or
    // another relative day ("Saturday afternoon", "tomorrow morning"), which we can't
    // localize confidently, so we skip it (residual).
    private static void TakeDayPart(
        string masked, int start, int end, string word, DateOnly refDate, int loHour, int hiHour, Action<Func<DateOnly, int, bool>> take)
    {
        int idx = IndexOfWord(masked, start, end, word);
        if (idx < 0 || QualifiedByOtherDay(masked, start, idx))
            return;
        take((d, h) => d == refDate && h >= loHour && h < hiHour);
    }

    private static bool HasWord(string s, int start, int end, string word) => IndexOfWord(s, start, end, word) >= 0;

    private static bool ContainsAnyWord(string s, int start, int end, string[] words)
    {
        foreach (var w in words)
            if (IndexOfWord(s, start, end, w) >= 0)
                return true;
        return false;
    }

    // First whole-word, case-insensitive occurrence of word in [start, end), or -1.
    private static int IndexOfWord(string s, int start, int end, string word)
    {
        int from = start;
        while (from < end)
        {
            int idx = s.IndexOf(word, from, end - from, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return -1;
            int after = idx + word.Length;
            bool wholeWord = (idx == 0 || !char.IsLetter(s[idx - 1]))
                && (after >= s.Length || !char.IsLetter(s[after]));
            if (wholeWord)
                return idx;
            from = after;
        }
        return -1;
    }

    // A change window endpoint must land on a snapshot block boundary. Blocks are
    // anchored to the locality's local day-parts (WX-155), so alignment is checked
    // in LOCAL time: the endpoint must be a local 00/06/12/18 boundary.
    private static bool IsBlockAligned(DateTime utc, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
        return local is { Minute: 0, Second: 0, Millisecond: 0 } && local.Hour % 6 == 0;
    }

    // A 6-hour block [StartUtc, StartUtc+6h) overlaps the half-open window [start, end).
    private static bool BlockOverlapsWindow(DateTime blockStartUtc, ChangeWindow w) =>
        blockStartUtc < w.EndUtc && blockStartUtc.AddHours(6) > w.StartUtc;

    // The precipitation ChangePhenomenon values share names 1:1 with PrecipPhenomenon
    // (StructuredReportBody documents the mirror), so match by NAME rather than a
    // hand-maintained switch: a sixth precip phenomenon added to both enums is covered
    // automatically, and a non-precip ChangePhenomenon (Wind / WindShift / Fog / Haze /
    // Smoke / Dust / Temperature / Severe) simply doesn't parse. IsDefined guards the
    // edge where p is an undefined value cast from int (ToString() yields a number).
    private static bool TryMapPrecip(ChangePhenomenon p, out PrecipPhenomenon precip) =>
        Enum.TryParse(p.ToString(), out precip) && Enum.IsDefined(precip);

    // WX-151: highest precipExpectation across blocks overlapping the window whose
    // phenomenon is p — None when no such block (a block carrying a phenomenon
    // always has a non-None expectation by the snapshot invariant). Used to compare
    // the prior and new snapshots' in-window intensity for a phenomenon.
    private static PrecipExpectation MaxExpect(ForecastSnapshotBody body, PrecipPhenomenon p, ChangeWindow w)
    {
        var max = PrecipExpectation.None;
        foreach (var b in body.Blocks)
            if (b.PrecipPhenomenon == p && BlockOverlapsWindow(b.StartUtc, w) && (int)b.PrecipExpectation > (int)max)
                max = b.PrecipExpectation;
        return max;
    }

    // WX-151: whether any block overlapping the window carries the safety severeFlag.
    private static bool AnySevereInWindow(ForecastSnapshotBody body, ChangeWindow w) =>
        body.Blocks.Any(b => b.SevereFlag && BlockOverlapsWindow(b.StartUtc, w));

    // WX-151: severe within the window on a block carrying phenomenon p — the
    // per-phenomenon severe axis, so an unrelated severe block does not vouch for a
    // different phenomenon's change.
    private static bool SevereForPhenomenon(ForecastSnapshotBody body, PrecipPhenomenon p, ChangeWindow w) =>
        body.Blocks.Any(b => b.PrecipPhenomenon == p && b.SevereFlag && BlockOverlapsWindow(b.StartUtc, w));

    // WX-151: true when the prior has a block at EVERY 6-hour step of the window.
    // Weakening/clearing is only verifiable against a prior that fully covers the
    // window — a prior reaching only part of a multi-block window (e.g. near its
    // horizon edge) is incomplete evidence, so we treat it as uncoverable and do not
    // reject (never reject what we can't prove wrong). Window endpoints are
    // block-aligned (validated above) and blocks sit on local day-part boundaries.
    private static bool PriorFullyCoversWindow(ForecastSnapshotBody prior, ChangeWindow w)
    {
        for (var u = w.StartUtc; u < w.EndUtc; u = u.AddHours(6))
            if (!prior.Blocks.Any(b => b.StartUtc == u))
                return false;
        return true;
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
        ReportKind reportKind, string? previousMetarIcao, bool allowSkip)
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

        var changeAlertInstruction = reportKind switch
        {
            ReportKind.Unscheduled =>
                "This is an unscheduled update — conditions have changed since the last report. "
                + "For the changeSummary, write one or two sentences summarising "
                + "what has changed (e.g. a forecast risk that has appeared, or a significant temperature shift). ",
            // WX-178: a scheduled report's "What's changed" band rides ONLY a newly-appearing
            // near-term severe hazard; everything else belongs in the grid + closing, not a band.
            // WX-165: the Diagnostic (startup verification) kind gets the SAME suppression — it
            // previously fell through to the empty default below, the one report kind that never
            // received this "an empty changes array is the correct answer" coaching, so against a
            // stale prior it filled the band and the resulting phantom degraded the send (and a
            // diagnostic degrade is a hard abort, suppressing the deploy verification entirely).
            ReportKind.Scheduled or ReportKind.Diagnostic =>
                (reportKind == ReportKind.Diagnostic
                    ? "This is a diagnostic (startup verification) report. "
                    : "This is a scheduled report. ")
                + "Show a \"What's changed\" band ONLY when a NEW severe hazard "
                + "appears in the near term — a block that was not previously severe becoming severe within "
                + "the next three local days (today through the day after tomorrow). In that case emit the "
                + "change(s) and a one- or two-sentence changeSummary naming the hazard and its timing. For "
                + "every other case — ordinary or non-severe changes, or nothing material — emit an EMPTY "
                + "changes array and a null changeSummary; that context belongs in the per-day grid and the "
                + "closing, not a change band. ",
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
        // SnapshotDescriber's default display units (US customary). For the
        // forecast bodies this is harmless — the final_snapshot schema fixes
        // canonical °C/kt and the structured_report's change quantities derive
        // from that canonical snapshot, so the renderer converts per recipient.
        // The exception worth knowing: live OBSERVATION values appear here ONLY
        // in US-customary text, so if Claude cites a current-observation quantity
        // directly in narrative prose it must convert it to the canonical
        // {q:...} unit by hand (the token grammar accepts any number, so a
        // copy-the-displayed-figure slip would not fail validation). This is a
        // pre-existing WX-128 property, not introduced here; a follow-up could
        // give SnapshotDescriber a canonical mode so the payload units match the
        // token convention end-to-end.
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