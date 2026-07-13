using System.Net;
using System.Text;
using System.Text.Json;

using MetarParser.Data.Entities;

using WxInterp;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

// Drives ForecastReconciler against a stubbed Anthropic Messages API endpoint.
// Each test wires a StubHandler that returns predetermined JSON; the reconciler
// posts, parses, validates, and either lands in ReconcileResult.Success with
// the parsed THREE artifacts (final_snapshot, structured_report, reasoning_trace)
// or in ReconcileResult.Failure with a reason. WX-130: the email_body artifact is
// gone (a deterministic renderer builds each recipient's email from the structured
// report), and the structured report is the LIVE, fatal artifact again — a
// validation failure on it routes through the retry → skip/Failure path, exactly
// like a final_snapshot schema violation.
// The HTTP-failure test deliberately returns a 400-class status so the
// ClaudeClient retry loop (which retries only 429/529/5xx) exits immediately.

public class ForecastReconcilerTests
{
    // One seed-backed template store for the whole harness — the reconciler only reads
    // Tok.ClosingFallback from it, so rebuilding it (and re-parsing the migration) per test
    // is wasted work (CodeRabbit).
    private static readonly LanguageTemplateStore Templates = SeedTemplateStore.Build();

    // A valid WX-128/130 structured_report whose 'en' narrative clears the
    // per-language degeneracy floor (MinVisibleNarrativeChars). The narrative
    // carries only the two judgment sections (changeSummary + closing); the
    // current-conditions table and per-day grid are rendered deterministically.
    private const string ValidStructuredReportJson = """
        {
          "schemaVersion": 5,
          "changes": [],
          "narrative": {
            "en": {
              "changeSummary": null,
              "closing": "A wet stretch ahead — keep an umbrella handy through the weekend."
            }
          }
        }
        """;

    // A final_snapshot whose 11–17Z block carries rain — backs the appearing-rain change.
    private const string RainBlockSnapshotJson = """
        {"schemaVersion":5,"blocks":[{"startUtc":"2026-06-09T11:00:00Z","skyState":"partly_cloudy","obscuration":"none","temperatureCelsius":{"min":22,"max":30},"windKt":{"min":5,"max":12},"precipExpectation":"possible","precipPhenomenon":"rain","severeFlag":false}]}
        """;

    // Schema-valid (closing is non-blank) but below the per-language visible floor:
    // the WX-120 fall-safe degeneracy case the reconciler turns into a skip/Failure.
    private const string DegenerateStructuredReportJson = """
        {
          "schemaVersion": 5,
          "changes": [],
          "narrative": {
            "en": { "changeSummary": null, "closing": "ok" }
          }
        }
        """;

    // ── WX-149 prose-hygiene fixtures ─────────────────────────────────────────

    // Defect 3 (send 1938): raw internal block notation leaked into the closing.
    private const string RawUtcLeakReportJson = """
        {
          "schemaVersion": 5,
          "changes": [],
          "narrative": {
            "en": { "changeSummary": null, "closing": "The Wednesday afternoon block (12-18Z) has shifted from dry to wet." }
          }
        }
        """;

    // A lower-cased leak ("12-18z") must be caught too (case-insensitive, PR #87).
    private const string RawUtcLeakLowercaseReportJson = """
        {
          "schemaVersion": 5,
          "changes": [],
          "narrative": {
            "en": { "changeSummary": null, "closing": "The afternoon block (12-18z) has shifted from dry to wet." }
          }
        }
        """;

    // Defect 2 (send 1927): prose says "afternoon" next to a {q:time} token at
    // 11:00Z, which renders to 6:00 AM local (CDT) — the morning, not afternoon.
    private const string ProseTimeMismatchReportJson = """
        {
          "schemaVersion": 5,
          "narrative": {
            "en": { "changeSummary": "Rain is now expected to develop Saturday afternoon, {q:time:2026-06-13T11:00:00Z}.", "closing": "A wet start to the weekend." }
          }
        }
        """;

    // The same shape but the prose word AGREES with the token's local rendering
    // (11:00Z is 6:00 AM CDT — morning) — must NOT be rejected.
    private const string ProseTimeAgreesReportJson = """
        {
          "schemaVersion": 5,
          "narrative": {
            "en": { "changeSummary": "Rain develops this morning, {q:time:2026-06-09T11:00:00Z}.", "closing": "A wet start to the day." }
          }
        }
        """;

    // A 6/13 11-17Z block carrying rain — backs the prose-token-mismatch change.
    private const string RainBlock613SnapshotJson = """
        {"schemaVersion":5,"blocks":[{"startUtc":"2026-06-13T11:00:00Z","skyState":"partly_cloudy","obscuration":"none","temperatureCelsius":{"min":22,"max":30},"windKt":{"min":5,"max":12},"precipExpectation":"possible","precipPhenomenon":"rain","severeFlag":false}]}
        """;

    // ── happy path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task HappyPath_ReturnsSuccess_WithThreeArtifactsAndTokens()
    {
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: """{"schemaVersion":5,"blocks":[]}""",
            reasoningTrace: "All three steps clean.",
            inputTokens: 100, outputTokens: 50,
            cacheReadInputTokens: 80, cacheCreationInputTokens: 10);

        var result = await RunReconciler(responseJson);

        var success = Assert.IsType<ReconcileResult.Success>(result);
        Assert.Equal("All three steps clean.", success.ReasoningTrace);
        Assert.Empty(success.FinalSnapshot.Blocks);
        Assert.True(success.StructuredReport.Narrative.ContainsKey("en"));
        Assert.Empty(success.StructuredReport.Changes);
        Assert.Equal(100, success.Tokens.InputTokens);
        Assert.Equal(50, success.Tokens.OutputTokens);
        Assert.Equal(80, success.Tokens.CacheReadInputTokens);
        Assert.Equal(10, success.Tokens.CacheCreationInputTokens);
    }

    // ── WX-130 structured_report contract — now the LIVE, fatal artifact ───────
    // The structured report is the rendering source, so a validation failure on it
    // is fatal (retry → skip/Failure), not best-effort as it was during the WX-144
    // additive transition.

    [Fact]
    public async Task StructuredReportMissing_IsFatal_ReturnsFailure()
    {
        var responseJson = BuildClaudeResponseJsonWithRawInput("""
            {
              "final_snapshot": { "schemaVersion": 5, "blocks": [] },
              "reasoning_trace": "trace"
            }
            """);

        var result = await RunReconciler(responseJson);

        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("missing required field", failure.Reason);
        Assert.Contains("structured_report", failure.Reason);
    }

    [Fact]
    public async Task StructuredReportMissingRequestedLanguage_Degrades_AfterRetries()
    {
        // The cycle requests en AND es; the narrative is internally valid but carries
        // en only. The per-call contract failure is retried (bounded); since the
        // final_snapshot itself parsed cleanly, the exhausted result DEGRADES (WX-148)
        // rather than failing outright — the snapshot is still usable for a hazard
        // report, only the suspect narrative is dropped.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: """{"schemaVersion":5,"blocks":[]}""",
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0);

        int calls = 0;
        var result = await RunReconciler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }, narrativeLanguages: new[] { "en", "es" });

        var degraded = Assert.IsType<ReconcileResult.Degraded>(result);
        Assert.Contains("missing requested language", degraded.Reason);
        Assert.Equal(3, calls); // retried (bounded) like any malformed structured artifact
    }

    [Fact]
    public async Task StructuredReportWithExtraLanguage_Degrades_AfterRetries()
    {
        // Exact-set contract: an unrequested language is unvalidated content for no
        // recipient — retried, then DEGRADES (WX-148) because the final_snapshot parsed.
        var withExtra = ValidStructuredReportJson.Replace(
            "\"narrative\": {",
            "\"narrative\": { \"es\": { \"changeSummary\": null, \"closing\": \"Un cierre razonable y suficientemente largo.\" },");
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: """{"schemaVersion":5,"blocks":[]}""",
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: withExtra);

        var result = await RunReconciler(responseJson);

        var degraded = Assert.IsType<ReconcileResult.Degraded>(result);
        Assert.Contains("unrequested language", degraded.Reason);
    }

    [Fact]
    public async Task DegenerateNarrative_WhenGuaranteedSend_ReturnsFailure()
    {
        // Schema-valid but near-blank narrative (below the per-language floor): on a
        // guaranteed send it cannot become a skip — it fails closed so the
        // provisional stays and the next cycle self-heals (WX-120 carried forward).
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: """{"schemaVersion":5,"blocks":[]}""",
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: DegenerateStructuredReportJson);

        var result = await RunReconciler(responseJson, allowSkip: false);

        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("content-less narrative", failure.Reason);
    }

    [Fact]
    public async Task DegenerateNarrative_WhenAllowSkip_RetriesThenSkips()
    {
        // A near-blank narrative on a skippable cycle matches Claude's (skip-leaning)
        // reasoning: after bounded retries it becomes a NotNews and keeps the trace.
        var degenerate = BuildClaudeResponseJson(
            finalSnapshotJson: """{"schemaVersion":5,"blocks":[]}""",
            reasoningTrace: "Confirms the prior forecast — not news. SKIP.",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: DegenerateStructuredReportJson);

        int calls = 0;
        var result = await RunReconciler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(degenerate, Encoding.UTF8, "application/json"),
            };
        }, allowSkip: true);

        var notNews = Assert.IsType<ReconcileResult.NotNews>(result);
        Assert.Equal("Confirms the prior forecast — not news. SKIP.", notNews.ReasoningTrace);
        Assert.Equal(3, calls); // bounded at maxAttempts
    }

    [Fact]
    public async Task FreshFinalSnapshotWithStaleVersion_FailsClosed()
    {
        // Deserialize stays lenient for old persisted priors, but a FRESH
        // reconciled snapshot must carry the current version — Claude copying
        // the prior's older digit fails closed (WX-128 review).
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: """{"schemaVersion":2,"blocks":[]}""",
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0);

        var result = await RunReconciler(responseJson);

        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("not the current version", failure.Reason);
    }

    // ── invalidation gate: skip_send ────────────────────────────────────────

    [Fact]
    public async Task SkipSend_WhenAllowed_ReturnsNotNews_WithTraceAndTokens()
    {
        var responseJson = BuildClaudeResponseJsonWithRawInput(
            """{ "reasoning_trace": "Observed rain matches the prior forecast — not news." }""",
            inputTokens: 70, outputTokens: 12,
            cacheReadInputTokens: 60, cacheCreationInputTokens: 0,
            toolName: "skip_send");

        var result = await RunReconciler(responseJson, allowSkip: true);

        var notNews = Assert.IsType<ReconcileResult.NotNews>(result);
        Assert.Equal("Observed rain matches the prior forecast — not news.", notNews.ReasoningTrace);
        Assert.Equal(70, notNews.Tokens.InputTokens);
        Assert.Equal(12, notNews.Tokens.OutputTokens);
        Assert.Equal(60, notNews.Tokens.CacheReadInputTokens);
    }

    [Fact]
    public async Task SkipSend_WhenNotAllowed_ReturnsFailure_NotNotNews()
    {
        // A guaranteed send (scheduled / first / startup) passes allowSkip:false
        // and forces submit_reconciled_report. If Claude nonetheless returns
        // skip_send, the send must never be silently suppressed — the result must
        // be Failure, not NotNews.
        //
        // WX-80: enforcement now lives primarily at the API boundary — ClaudeClient
        // rejects the un-offered skip_send (returns null) before it reaches the
        // reconciler, which then surfaces a Failure. The reconciler's own
        // !allowSkip guard remains as documented defense in depth. Either path
        // yields the same end-to-end contract this test pins, so we assert the
        // behavior (Failure, not NotNews) rather than a specific reason string.
        var responseJson = BuildClaudeResponseJsonWithRawInput(
            """{ "reasoning_trace": "trying to skip a guaranteed send" }""",
            toolName: "skip_send");

        var result = await RunReconciler(responseJson, allowSkip: false);

        Assert.IsType<ReconcileResult.Failure>(result); // never silently a NotNews
    }

    [Fact]
    public async Task SkipSend_MissingReasoningTrace_ReturnsFailure()
    {
        // skip_send with no reasoning_trace fails — naming the missing field —
        // rather than silently suppressing a send with no recorded rationale.
        var responseJson = BuildClaudeResponseJsonWithRawInput(
            """{ }""",
            toolName: "skip_send");

        var result = await RunReconciler(responseJson, allowSkip: true);

        // WX-104: a missing key is reported by name, not mislabelled "schema validation failed".
        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("missing required field", failure.Reason);
        Assert.Contains("reasoning_trace", failure.Reason);
        Assert.DoesNotContain("Schema validation failed", failure.Reason);
    }

    // ── failure: transport ──────────────────────────────────────────────────

    [Fact]
    public async Task HttpStatus400_ReturnsFailure()
    {
        // 400 is outside the retry-on (429/529/5xx) window, so the
        // ClaudeClient retry loop exits on the first attempt — keeps the
        // test fast without sleeping through the backoff.
        var result = await RunReconciler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":{\"type\":\"invalid_request_error\"}}"),
            });

        Assert.IsType<ReconcileResult.Failure>(result);
    }

    // ── failure: no tool_use block ──────────────────────────────────────────

    [Fact]
    public async Task ResponseWithoutToolUseBlock_ReturnsFailure()
    {
        // Claude responded with text only — no tool_use block.  Should hit
        // the "no submit_reconciled_report tool_use block" branch.
        var responseJson = """
            {
              "id": "msg_x",
              "type": "message",
              "role": "assistant",
              "content": [
                { "type": "text", "text": "Sorry, I cannot reconcile right now." }
              ],
              "model": "claude-sonnet-4-6",
              "stop_reason": "end_turn",
              "usage": { "input_tokens": 10, "output_tokens": 10 }
            }
            """;

        var result = await RunReconciler(responseJson);

        Assert.IsType<ReconcileResult.Failure>(result);
    }

    // ── failure: tool input missing final_snapshot ──────────────────────────

    [Fact]
    public async Task ToolInputMissingFinalSnapshot_ReturnsFailure()
    {
        var responseJson = BuildClaudeResponseJsonWithRawInput($$"""
            {
              "structured_report": {{ValidStructuredReportJson}},
              "reasoning_trace": "trace"
            }
            """);

        var result = await RunReconciler(responseJson);

        // WX-104: names the missing field; no KeyNotFoundException, no "schema validation failed".
        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("missing required field", failure.Reason);
        Assert.Contains("final_snapshot", failure.Reason);
        Assert.DoesNotContain("Schema validation failed", failure.Reason);
    }

    // ── failure: tool input missing reasoning_trace ─────────────────────────

    [Fact]
    public async Task ToolInputMissingReasoningTrace_ReturnsFailure()
    {
        // final_snapshot is present and valid so the failure isolates the missing
        // reasoning_trace (the reconciler checks final_snapshot, then reasoning_trace).
        var responseJson = BuildClaudeResponseJsonWithRawInput($$"""
            {
              "final_snapshot": { "schemaVersion": 5, "blocks": [] },
              "structured_report": {{ValidStructuredReportJson}}
            }
            """);

        var result = await RunReconciler(responseJson);

        // WX-104: names the missing field; no KeyNotFoundException, no "schema validation failed".
        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("missing required field", failure.Reason);
        Assert.Contains("reasoning_trace", failure.Reason);
        Assert.DoesNotContain("Schema validation failed", failure.Reason);
    }

    // ── failure: response truncated at the output-token cap (WX-109) ─────────

    [Fact]
    public async Task ResponseTruncatedAtTokenCap_ReturnsTruncationFailure_NotMissingField()
    {
        // stop_reason "max_tokens" means generation was cut at the output-token
        // cap, so the tool_use input is a truncated partial object with a trailing
        // required field dropped. The reconciler must report truncation, not
        // mislabel it a missing field — it keys on stop_reason, before reading fields.
        var responseJson = BuildClaudeResponseJsonWithRawInput("""
            {
              "final_snapshot": { "schemaVersion": 5, "blocks": [] },
              "reasoning_trace": "trace"
            }
            """,
            stopReason: "max_tokens");

        var result = await RunReconciler(responseJson);

        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("truncated", failure.Reason);
        Assert.Contains("max_tokens", failure.Reason);
        Assert.DoesNotContain("missing required field", failure.Reason);
    }

    [Fact]
    public async Task ResponseTruncatedAtTokenCap_DetectedEvenWhenPartialInputLooksComplete()
    {
        // Defense-in-depth: a "max_tokens" stop_reason is authoritative. Even if the
        // truncated partial input happens to still carry all three artifacts, we do
        // not trust a capped generation — the result is the truncation Failure.
        var responseJson = BuildClaudeResponseJsonWithRawInput($$"""
            {
              "final_snapshot": { "schemaVersion": 5, "blocks": [] },
              "structured_report": {{ValidStructuredReportJson}},
              "reasoning_trace": "trace"
            }
            """,
            stopReason: "max_tokens");

        var result = await RunReconciler(responseJson);

        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("truncated", failure.Reason);
    }

    // ── WX-110: bounded retry on a retryable-malformed (non-truncation) response ─

    [Fact]
    public async Task RetryableMalformed_ThenValid_RetriesAndSucceeds()
    {
        // First response omits structured_report (a complete, non-truncated tool_use
        // that simply dropped the advisory-required field); the second is complete.
        // The reconciler retries within the cycle and lands on Success.
        var malformed = BuildClaudeResponseJsonWithRawInput("""
            { "final_snapshot": { "schemaVersion": 5, "blocks": [] }, "reasoning_trace": "trace" }
            """);
        var valid = BuildClaudeResponseJson(
            finalSnapshotJson: """{"schemaVersion":5,"blocks":[]}""",
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0);

        int calls = 0;
        var result = await RunReconciler(_ =>
        {
            calls++;
            var body = calls == 1 ? malformed : valid;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        });

        Assert.IsType<ReconcileResult.Success>(result);
        Assert.Equal(2, calls); // exactly one retry was needed
    }

    [Fact]
    public async Task RetryableMalformed_ThenValid_SumsTokensAcrossAttempts()
    {
        // The failed attempt is real billable spend; the returned Tokens must include
        // it so the cost dashboards don't undercount retried cycles.
        var malformed = BuildClaudeResponseJsonWithRawInput("""
            { "final_snapshot": { "schemaVersion": 5, "blocks": [] }, "reasoning_trace": "trace" }
            """); // default tokens: 10 in / 10 out / 0 / 0
        var valid = BuildClaudeResponseJson(
            finalSnapshotJson: """{"schemaVersion":5,"blocks":[]}""",
            reasoningTrace: "trace",
            inputTokens: 100, outputTokens: 50, cacheReadInputTokens: 80, cacheCreationInputTokens: 10);

        int calls = 0;
        var result = await RunReconciler(_ =>
        {
            calls++;
            var body = calls == 1 ? malformed : valid;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        });

        var success = Assert.IsType<ReconcileResult.Success>(result);
        Assert.Equal(110, success.Tokens.InputTokens);          // 10 + 100
        Assert.Equal(60, success.Tokens.OutputTokens);           // 10 + 50
        Assert.Equal(80, success.Tokens.CacheReadInputTokens);   // 0 + 80
        Assert.Equal(10, success.Tokens.CacheCreationInputTokens); // 0 + 10
    }

    [Fact]
    public async Task DegenerateNarrative_ThenValid_RetriesAndSucceeds()
    {
        // A near-blank narrative first response recovers on retry to a real report:
        // Success, exactly one retry.
        var degenerate = BuildClaudeResponseJson(
            finalSnapshotJson: """{"schemaVersion":5,"blocks":[]}""",
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: DegenerateStructuredReportJson);
        var valid = BuildClaudeResponseJson(
            finalSnapshotJson: """{"schemaVersion":5,"blocks":[]}""",
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0);

        int calls = 0;
        var result = await RunReconciler(_ =>
        {
            calls++;
            var body = calls == 1 ? degenerate : valid;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        });

        Assert.IsType<ReconcileResult.Success>(result);
        Assert.Equal(2, calls); // one retry to recover
    }

    [Fact]
    public async Task RetryableMalformed_AllAttemptsFail_ReturnsFailureBoundedAtThreeCalls()
    {
        var malformed = BuildClaudeResponseJsonWithRawInput("""
            { "final_snapshot": { "schemaVersion": 5, "blocks": [] }, "reasoning_trace": "trace" }
            """);

        int calls = 0;
        var result = await RunReconciler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(malformed, Encoding.UTF8, "application/json"),
            };
        });

        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("missing required field", failure.Reason);
        Assert.Contains("structured_report", failure.Reason);
        Assert.Equal(3, calls); // bounded at maxAttempts — no runaway retry
    }

    [Fact]
    public async Task MaxTokensTruncation_IsNotRetried()
    {
        // A max_tokens stop_reason is authoritative: re-calling at the same cap would
        // just re-truncate, so the truncation Failure is returned on the first call.
        var truncated = BuildClaudeResponseJsonWithRawInput("""
            { "final_snapshot": { "schemaVersion": 5, "blocks": [] }, "reasoning_trace": "trace" }
            """, stopReason: "max_tokens");

        int calls = 0;
        var result = await RunReconciler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(truncated, Encoding.UTF8, "application/json"),
            };
        });

        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("truncated", failure.Reason);
        Assert.Equal(1, calls); // not retried
    }

    // ── failure: garbage final_snapshot JSON ────────────────────────────────

    [Fact]
    public async Task FinalSnapshotIsGarbageString_ReturnsFailure()
    {
        // final_snapshot is a string where ForecastSnapshotBody expects an
        // object — JsonElement.GetRawText returns the quoted string, which
        // ForecastSnapshotBody.Deserialize will fail on.
        var responseJson = BuildClaudeResponseJsonWithRawInput($$"""
            {
              "final_snapshot": "this is not a snapshot body",
              "structured_report": {{ValidStructuredReportJson}},
              "reasoning_trace": "trace"
            }
            """);

        var result = await RunReconciler(responseJson);

        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("Schema validation failed", failure.Reason);
    }

    // ── failure: precipPhenomenon-iff-non-none invariant ────────────────────

    [Fact]
    public async Task FinalSnapshotViolatesPrecipPhenomenonInvariant_ReturnsFailure()
    {
        // Block has precipExpectation = "none" but precipPhenomenon = "rain".
        // ForecastSnapshotBody.Validate enforces the invariant on deserialize.
        var blockJson = """
            {
              "startUtc": "2026-05-28T00:00:00Z",
              "skyState": "clear",
              "obscuration": "none",
              "temperatureCelsius": { "min": 10.0, "max": 20.0 },
              "windKt": { "min": 5, "max": 10 },
              "precipExpectation": "none",
              "precipPhenomenon": "rain",
              "severeFlag": false
            }
            """;
        var responseJson = BuildClaudeResponseJsonWithRawInput($$"""
            {
              "final_snapshot": { "schemaVersion": 5, "blocks": [{{blockJson}}] },
              "structured_report": {{ValidStructuredReportJson}},
              "reasoning_trace": "trace"
            }
            """);

        var result = await RunReconciler(responseJson);

        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("Schema validation failed", failure.Reason);
    }

    // ── WX-148 change ↔ snapshot consistency validator ───────────────────────

    // ── WX-149 prose-hygiene validator assertions ────────────────────────────

    [Fact]
    public async Task RawUtcBlockNotation_InClosing_DropsClosingOnly()
    {
        // Defect 3 (Watonga 6/10, send 1938): internal "(12-18Z)" shorthand leaked into
        // the reader-facing closing. WX-189 independent-section degrade: retries exhaust,
        // the closing is dropped to the safe fallback, and the rest of the report still
        // sends (no longer a wholesale narrative degrade).
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: RawUtcLeakReportJson);

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson));
        Assert.Equal("See the forecast above for the full outlook.", success.StructuredReport.Narrative["en"].Closing);
    }

    [Fact]
    public async Task JargonInProse_AviationTerm_DropsClosingOnly()
    {
        // WX-154: naming an internal data source ("TAF") in reader-facing prose is rejected
        // — the 2026-06-10 closing "the afternoon TAF carries a 30% chance". WX-189: the
        // closing is dropped to the fallback and the rest still sends.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("The afternoon TAF carries a 30% chance of light showers."));

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson));
        Assert.Equal("See the forecast above for the full outlook.", success.StructuredReport.Narrative["en"].Closing);
    }

    [Fact]
    public async Task JargonInProse_LowerCaseSource_DropsClosingOnly()
    {
        // The homograph-free acronyms (taf/metar/gfs/icao) are caught case-insensitively,
        // so a lowercased leak slips no more than an upper-cased one ("cape" stays exempt).
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("The latest metar shows light rain at the field."));

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson));
        Assert.Equal("See the forecast above for the full outlook.", success.StructuredReport.Narrative["en"].Closing);
    }

    [Fact]
    public async Task RawUtcBlockNotation_LowerCase_InClosing_DropsClosingOnly()
    {
        // Case-insensitive: a lower-cased "12-18z" leak is caught too (PR #87); the closing
        // is dropped to the fallback and the rest still sends (WX-189).
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: RawUtcLeakLowercaseReportJson);

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson));
        Assert.Equal("See the forecast above for the full outlook.", success.StructuredReport.Narrative["en"].Closing);
    }

    // WX-139: a two-language structured report body — used by the synoptic-mechanism
    // tests to exercise the en and es validator arms in one report.
    private static string EnEsReport(string? enChange, string enClosing, string? esChange, string esClosing) =>
        "{\"schemaVersion\":5,\"changes\":[],\"narrative\":{"
        + "\"en\":{\"changeSummary\":" + JsonSerializer.Serialize(enChange) + ",\"closing\":" + JsonSerializer.Serialize(enClosing) + "},"
        + "\"es\":{\"changeSummary\":" + JsonSerializer.Serialize(esChange) + ",\"closing\":" + JsonSerializer.Serialize(esClosing) + "}}}";

    [Fact]
    public async Task SynopticMechanism_FrontInClosing_DropsClosingOnly()
    {
        // WX-139 headline repro (send 1643): "...a chance of showers ... as a front pushes
        // through." The gusts/showers are grounded in the TAF but the front is invented — the
        // model cannot evidence a cause from single-point data. WX-189: closing dropped, rest sends.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("Expect gusty winds and a few showers this evening as a front pushes through."));

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson));
        Assert.Equal("See the forecast above for the full outlook.", success.StructuredReport.Narrative["en"].Closing);
    }

    [Fact]
    public async Task SynopticMechanism_FrontalBoundaryInChangeSummary_DropsChangeSummaryOnly()
    {
        // The change-band form of the same defect (send 1637: "...as a frontal boundary pushes
        // through"). The changeSummary is dropped to the deterministic band and the closing sends.
        const string report = """
            {
              "schemaVersion": 5,
              "narrative": {
                "en": { "changeSummary": "Winds turn gusty with showers arriving this evening as a frontal boundary pushes through.", "closing": "Conditions settle down as the week goes on." }
              }
            }
            """;
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: report);

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson));
        Assert.Null(success.StructuredReport.Narrative["en"].ChangeSummary);
        Assert.Equal("Conditions settle down as the week goes on.", success.StructuredReport.Narrative["en"].Closing);
    }

    [Fact]
    public async Task SynopticMechanism_MultiWordPressureSystem_DropsClosingOnly()
    {
        // A multi-word term ("low pressure") must be caught while its ambiguous bare stem
        // ("low", a temperature) is left alone — the list is spelled out for exactly this.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("A broad area of low pressure keeps the unsettled pattern going into the weekend."));

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson));
        Assert.Equal("See the forecast above for the full outlook.", success.StructuredReport.Narrative["en"].Closing);
    }

    [Fact]
    public async Task SynopticMechanism_HyphenatedCompound_DropsClosingOnly()
    {
        // A hyphenated compound ("upper-level low") is caught as well as the spaced form —
        // the inter-word separator is [ -], so adjectival hyphenation doesn't slip the backstop.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("An upper-level low keeps the unsettled pattern going into the weekend."));

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson));
        Assert.Equal("See the forecast above for the full outlook.", success.StructuredReport.Narrative["en"].Closing);
    }

    [Fact]
    public async Task SynopticMechanism_EnglishInFrontOf_IsLegal_Succeeds()
    {
        // The positional idiom "in front of" is NOT a synoptic mechanism — the "front"/"frontal"
        // arm is lookbehind-guarded so a legal report is never falsely suppressed.
        const string report = """
            {
              "schemaVersion": 5,
              "narrative": {
                "en": { "changeSummary": "Breezy conditions build this evening, with the drier air sitting just in front of the coast.", "closing": "A quieter stretch settles in for the rest of the week." }
              }
            }
            """;
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: report);

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson));
        Assert.Equal("Breezy conditions build this evening, with the drier air sitting just in front of the coast.", success.StructuredReport.Narrative["en"].ChangeSummary);
    }

    [Fact]
    public async Task SynopticMechanism_InFrontOf_WhitespaceRun_IsLegal_Succeeds()
    {
        // The "in front of" guard tolerates a whitespace RUN between "in" and "front"
        // (CodeRabbit, PR #182): the lookbehind is (?<!\bin\s+), so a double space still
        // reads as the positional idiom, not a flagged mechanism.
        const string report = """
            {
              "schemaVersion": 5,
              "narrative": {
                "en": { "changeSummary": "Breezy conditions build this evening, with the drier air sitting just in  front of the coast.", "closing": "A quieter stretch settles in for the rest of the week." }
              }
            }
            """;
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: report);

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson));
        Assert.Equal("Breezy conditions build this evening, with the drier air sitting just in  front of the coast.", success.StructuredReport.Narrative["en"].ChangeSummary);
    }

    [Fact]
    public async Task SynopticMechanism_SpanishFrenteFrio_DropsChangeSummary()
    {
        // The es arm catches "un frente frío" (article + noun, and the weather-adjective form).
        // The changeSummary is dropped across every language (WX-189 uniform section degrade).
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: EnEsReport(
                enChange: "Winds turn gusty with a few showers this evening.",
                enClosing: "Conditions settle down as the week goes on.",
                esChange: "Los vientos se vuelven racheados con algunas lluvias esta tarde mientras avanza un frente frío.",
                esClosing: "Las condiciones se calman durante la semana."));

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson, Encoding.UTF8, "application/json") },
            narrativeLanguages: new[] { "en", "es" }));
        Assert.Null(success.StructuredReport.Narrative["es"].ChangeSummary);
        Assert.Null(success.StructuredReport.Narrative["en"].ChangeSummary);
    }

    [Fact]
    public async Task SynopticMechanism_SpanishFrenteA_IsLegal_Succeeds()
    {
        // Ticket-required guard: the positional "frente a" (= off/facing, "frente a la costa"),
        // which never takes an article, must stay legal — only "frente" + a weather adjective or
        // article is a mechanism. A legal es report survives intact.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: EnEsReport(
                enChange: "Winds pick up this evening with rain around.",
                enClosing: "A calmer stretch follows for the rest of the week.",
                esChange: "Los vientos aumentan esta tarde, con el aire más seco justo frente a la costa.",
                esClosing: "Sigue un periodo más tranquilo durante la semana."));

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson, Encoding.UTF8, "application/json") },
            narrativeLanguages: new[] { "en", "es" }));
        Assert.Equal("Los vientos aumentan esta tarde, con el aire más seco justo frente a la costa.", success.StructuredReport.Narrative["es"].ChangeSummary);
    }

    // ── WX-168 Spanish deterministic timing/claim validator parity ────────────

    // One DRY block on the reference local day (06-09): "hoy" resolves to it, and it carries no precip.
    private const string DryTodaySnapshotJson = """
        {"schemaVersion":5,"blocks":[{"startUtc":"2026-06-09T18:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":20,"max":28},"windKt":{"min":5,"max":12},"precipExpectation":"none","precipPhenomenon":null,"severeFlag":false}]}
        """;

    // One WET block on the reference local day (06-09): "hoy" resolves to it, and it carries rain.
    private const string WetTodaySnapshotJson = """
        {"schemaVersion":5,"blocks":[{"startUtc":"2026-06-09T18:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":20,"max":28},"windKt":{"min":5,"max":12},"precipExpectation":"likely","precipPhenomenon":"rain","severeFlag":false}]}
        """;

    [Fact]
    public async Task SpanishClosing_PrecipAtADryTime_IsCaught_Degrades()
    {
        // WX-168: the es lexicon plugin gives the closing precip-at-a-dry-time check (WX-152) real
        // deterministic coverage — "Lluvia hoy" (rain today) over a snapshot that is dry today is caught,
        // fails closed through the WX-189 retry, and (the fixed fixture can't fix it) the report degrades
        // with the es-attributed reason. Before this ticket the en-only validator matched no es word and
        // never fired.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: DryTodaySnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: EnEsReport(
                enChange: "A quiet day on tap.",
                enClosing: "Calm conditions hold through the day.",
                esChange: "Un día tranquilo por delante.",
                esClosing: "Lluvia hoy."));

        var result = await RunReconciler(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson, Encoding.UTF8, "application/json") },
            narrativeLanguages: new[] { "en", "es" });
        var degraded = Assert.IsType<ReconcileResult.Degraded>(result);
        Assert.Contains("narrative 'es'", degraded.Reason);   // the es closing check fired, not en's
        Assert.Contains("dry", degraded.Reason);
    }

    [Fact]
    public async Task SpanishClosing_AmbiguousManana_IsSkipped_Succeeds()
    {
        // WX-168 residual policy: es "mañana" = morning OR tomorrow — ambiguous, so it is NOT a
        // deterministic time trigger (TomorrowWords is empty). "Lluvia mañana" over a dry snapshot must
        // therefore be SKIPPED, never false-rejected — the es closing survives intact.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: DryTodaySnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: EnEsReport(
                enChange: "A quiet day on tap.",
                enClosing: "Calm conditions hold through the day.",
                esChange: "Un día tranquilo por delante.",
                esClosing: "Lluvia mañana."));

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson, Encoding.UTF8, "application/json") },
            narrativeLanguages: new[] { "en", "es" }));
        Assert.Equal("Lluvia mañana.", success.StructuredReport.Narrative["es"].Closing);   // ambiguous time → skipped, not rejected
    }

    [Fact]
    public async Task SpanishDayPart_MadrugadaContradictsToken_IsCaught_Degrades()
    {
        // WX-168: the es day-part {q:time} agreement check (WX-149) fires on the one unambiguous es
        // day-part word — "madrugada" (pre-dawn, part 0) next to a {q:time} that renders to the
        // afternoon/evening contradicts the token's local hour. es-only: en's lexicon has no "madrugada",
        // so this proves the es day-part wiring, not just en's.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: WetTodaySnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: EnEsReport(
                enChange: "Rain around today.",
                enClosing: "Keep an umbrella handy.",
                esChange: "Lluvia en la madrugada, {q:time:2026-06-09T18:00:00Z}.",
                esClosing: "Manténgase al tanto."));

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson, Encoding.UTF8, "application/json") },
            narrativeLanguages: new[] { "en", "es" }));
        // The madrugada(pre-dawn)/afternoon-token contradiction degrades the es changeSummary; the
        // offending word is gone (en unaffected).
        Assert.DoesNotContain("madrugada", success.StructuredReport.Narrative["es"].ChangeSummary ?? "");
        Assert.Equal("Keep an umbrella handy.", success.StructuredReport.Narrative["en"].Closing);
    }

    [Fact]
    public async Task SpanishClosing_PrecipAtAWetTime_IsLegal_Succeeds()
    {
        // WX-168: "Lluvia hoy" over a snapshot that IS wet today is a true claim — it must pass.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: WetTodaySnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: EnEsReport(
                enChange: "Rain around today.",
                enClosing: "Keep an umbrella handy.",
                esChange: "Lluvia por la zona hoy.",
                esClosing: "Lluvia hoy."));

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson, Encoding.UTF8, "application/json") },
            narrativeLanguages: new[] { "en", "es" }));
        Assert.Equal("Lluvia hoy.", success.StructuredReport.Narrative["es"].Closing);   // true claim over a wet block → passes
    }

    // ── WX-284 recipient precipitation vocabulary collapse ────────────────────

    // A severe (convective) block — the one case where storm wording is legitimate.
    private const string SevereStormSnapshotJson = """
        {"schemaVersion":5,"blocks":[{"startUtc":"2026-06-09T21:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":24,"max":31},"windKt":{"min":10,"max":30},"precipExpectation":"likely","precipPhenomenon":"thunderstorm","severeFlag":true}]}
        """;

    // A non-severe SNOW block — frozen precip keeps its own words (snow showers, winter storm), which
    // the liquid-only vocabulary bans must NOT reject.
    private const string SnowBlockSnapshotJson = """
        {"schemaVersion":5,"blocks":[{"startUtc":"2026-01-09T21:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":-6,"max":-1},"windKt":{"min":6,"max":14},"precipExpectation":"likely","precipPhenomenon":"snow","severeFlag":false}]}
        """;

    // Two blocks straddling the local-day boundary (CDT): a SEVERE thunderstorm TODAY (2026-06-09) and
    // non-severe rain TOMORROW (2026-06-10). refDate = the first block's local date (06-09), so "today"
    // resolves to the severe window and "tomorrow" to the non-severe one — the WX-284 CR #3 case where
    // storm wording is legitimate only for the window that actually carries the severe block.
    private const string SevereTodayRainTomorrowSnapshotJson = """
        {"schemaVersion":5,"blocks":[{"startUtc":"2026-06-09T18:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":24,"max":31},"windKt":{"min":12,"max":34},"precipExpectation":"possible","precipPhenomenon":"thunderstorm","severeFlag":true},{"startUtc":"2026-06-10T18:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":22,"max":29},"windKt":{"min":8,"max":16},"precipExpectation":"possible","precipPhenomenon":"rain","severeFlag":false}]}
        """;

    // A severe NON-convective block: SevereFlag set by high wind (>= threshold), no convection —
    // precipPhenomenon null. WX-284 renders this as "severe weather", NOT "severe storms"; the validator
    // must not let SevereFlag alone validate storm wording (WX-293 CR round 3).
    private const string SevereWindNonConvectiveSnapshotJson = """
        {"schemaVersion":5,"blocks":[{"startUtc":"2026-06-09T21:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":18,"max":25},"windKt":{"min":20,"max":42},"precipExpectation":"none","precipPhenomenon":null,"severeFlag":true}]}
        """;

    [Fact]
    public async Task NonSeverePrecipRegister_ShowersInChangeSummary_DropsChangeSummaryOnly()
    {
        // WX-284: "showers" reads as ordinary rain to the recipient, so the register must not reach
        // prose. The changeSummary is dropped to the deterministic band and the closing still sends.
        const string report = """
            {
              "schemaVersion": 5,
              "narrative": {
                "en": { "changeSummary": "Breezy conditions build with a few showers this evening.", "closing": "A calmer stretch follows later this week." }
              }
            }
            """;
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: report);

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson));
        Assert.Null(success.StructuredReport.Narrative["en"].ChangeSummary);
        Assert.Equal("A calmer stretch follows later this week.", success.StructuredReport.Narrative["en"].Closing);
    }

    [Fact]
    public async Task SevereStormVocabulary_StormWithoutSevereBlock_DropsClosingOnly()
    {
        // WX-284: storm wording is reserved for a severe block; this snapshot carries none, so a
        // "storm" in the closing is provably wrong (a non-severe thunderstorm reads as "rain"). The
        // closing degrades to the fallback and the report still sends.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("A stray storm cannot be ruled out."));

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson));
        Assert.Equal("See the forecast above for the full outlook.", success.StructuredReport.Narrative["en"].Closing);
    }

    [Fact]
    public async Task SevereStormVocabulary_SevereStormsWithSevereBlock_IsLegal_Succeeds()
    {
        // WX-284: with a severe block in the snapshot, "severe storms" wording is legitimate and must
        // NOT be rejected — the severe escalation is exactly what earns storm language.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: SevereStormSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("Severe storms are the main concern this period."));

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson));
        Assert.Equal("Severe storms are the main concern this period.", success.StructuredReport.Narrative["en"].Closing);
    }

    [Fact]
    public async Task PrecipLikelihood_LikelyInChangeSummary_DropsChangeSummaryOnly()
    {
        // WX-284 step 2 (CR #4a): "likely" is the RETIRED recipient precip-likelihood word — the hedge
        // collapsed to "possible". It must not reach prose; the changeSummary drops to the deterministic
        // band and the closing still sends. (No time reference, so only the likelihood ban can fire.)
        const string report = """
            {
              "schemaVersion": 5,
              "narrative": {
                "en": { "changeSummary": "Widespread rain is likely.", "closing": "A calmer stretch follows later this week." }
              }
            }
            """;
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: report);

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson));
        Assert.Null(success.StructuredReport.Narrative["en"].ChangeSummary);
        Assert.Equal("A calmer stretch follows later this week.", success.StructuredReport.Narrative["en"].Closing);
    }

    [Fact]
    public async Task SevereStormVocabulary_StormAtNonSevereWindow_WithSevereElsewhere_DropsClosingOnly()
    {
        // WX-284 (CR #3): storm wording is scoped to the window the prose names. Severe is TODAY, but
        // the closing places "severe storms" TOMORROW — a window the snapshot leaves non-severe (rain).
        // The old snapshot-wide "any severe block" gate let this pass; the window-scoped check rejects it.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: SevereTodayRainTomorrowSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("Severe storms move in tomorrow."));

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson));
        Assert.Equal("See the forecast above for the full outlook.", success.StructuredReport.Narrative["en"].Closing);
    }

    [Fact]
    public async Task SevereStormVocabulary_StormAtSevereWindow_IsLegal_Succeeds()
    {
        // WX-284 (CR #3): the mirror of the above — storm wording at the window that DOES carry the
        // severe block ("today") is legitimate and must not be rejected.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: SevereTodayRainTomorrowSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("Severe storms are the main concern today."));

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson));
        Assert.Equal("Severe storms are the main concern today.", success.StructuredReport.Narrative["en"].Closing);
    }

    [Fact]
    public async Task SevereStormVocabulary_SevereNonConvectiveWind_StormWording_DropsClosingOnly()
    {
        // WX-293 (CR round 3): SevereFlag can be set by a wind-only event (DeriveSevereFlag trips on
        // wind >= threshold), which is "severe weather", not "severe storms" (WX-284). The validator now
        // requires a severe CONVECTIVE window (SevereFlag + precipPhenomenon thunderstorm) to allow storm
        // wording, so "severe storms" over a severe NON-convective (wind) block is rejected and the closing
        // degrades — SevereFlag alone can no longer validate storm language. (The mirror case — a severe
        // THUNDERSTORM block — stays legal via SevereStormsWithSevereBlock_IsLegal_Succeeds, so this is a
        // new reject with no new false-reject.)
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: SevereWindNonConvectiveSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("Severe storms are the main concern this period."));

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson));
        Assert.Equal("See the forecast above for the full outlook.", success.StructuredReport.Narrative["en"].Closing);
    }

    [Fact]
    public async Task SevereStormVocabulary_MultiWindowClosing_StormsInLaterNonSevereWindow_DropsClosingOnly()
    {
        // WX-293: the production repro — a multi-window Closing that opens with "rain" and then varies to
        // "storms" for a later day-part of the same non-severe day ("rain ... through the morning, then
        // storms ... through the afternoon and evening"). The snapshot carries no severe block, so "storms"
        // is provably wrong. The sentence names several day-parts, so the time is unresolvable (multiple
        // matches → null, ResolveClosingTime) and the check falls back to the snapshot-wide "any severe
        // block" gate; with none severe the closing degrades to the fallback rather than shipping "storms".
        // This pins the exact production case. The WX-293 fix is upstream (strengthened prompt + sharpened
        // retry feedback that steer the model off this wording within the retry budget); the deterministic
        // backstop asserted here is unchanged and still fails closed — its correctness is the invariant.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport(
                "Rain looks possible through the morning, then storms possible through the afternoon and evening."));

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson));
        Assert.Equal("See the forecast above for the full outlook.", success.StructuredReport.Narrative["en"].Closing);
    }

    [Fact]
    public async Task SevereStormVocabulary_SevereRenderedAsExpected_DropsClosingOnly()
    {
        // WX-284 (CR follow-up): severe is ALWAYS "possible", never "expected"/"certain". Even with a
        // severe block in the referenced window, "severe storms are expected" over-hedges — rejected.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: SevereTodayRainTomorrowSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("Severe storms are expected today."));

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson));
        Assert.Equal("See the forecast above for the full outlook.", success.StructuredReport.Narrative["en"].Closing);
    }

    [Fact]
    public async Task SevereStormVocabulary_ExpectedToVerb_IsLegal_Succeeds()
    {
        // The over-hedge ban must NOT catch the trend construction "expected TO <verb>" — "severe storms
        // expected to weaken" describes the trend, not the severe likelihood, and stays legal.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: SevereTodayRainTomorrowSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("Severe storms are expected to weaken today."));

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson));
        Assert.Equal("Severe storms are expected to weaken today.", success.StructuredReport.Narrative["en"].Closing);
    }

    [Fact]
    public async Task NonSeverePrecipRegister_SnowShowers_IsLegal_Succeeds()
    {
        // WX-284 frozen guard: the liquid "showers" ban must NOT catch "snow showers" — frozen precip
        // keeps its own words. A snowy report survives intact.
        const string report = """
            {
              "schemaVersion": 5,
              "narrative": {
                "en": { "changeSummary": "Snow showers are possible through the afternoon.", "closing": "Bundle up and allow extra travel time." }
              }
            }
            """;
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: SnowBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: report);

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson));
        Assert.Equal("Snow showers are possible through the afternoon.", success.StructuredReport.Narrative["en"].ChangeSummary);
    }

    [Fact]
    public async Task SevereStormVocabulary_WinterStormWithoutSevereBlock_IsLegal_Succeeds()
    {
        // WX-284 frozen guard: "winter storm" is a legitimate frozen-precip term that does NOT set the
        // convective severeFlag, so the storm-vocabulary ban must not reject it even with no severe block.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: SnowBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("A winter storm is shaping up for the region."));

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson));
        Assert.Equal("A winter storm is shaping up for the region.", success.StructuredReport.Narrative["en"].Closing);
    }

    [Fact]
    public async Task ProseTimeWord_ContradictsTokenLocalRendering_DropsChangeSummaryOnly()
    {
        // Defect 2 (Spring 6/13, send 1927): the changeSummary says "afternoon" beside a
        // {q:time} token at 11:00Z that renders to 6:00 AM local (CDT) — morning. WX-189
        // independent-section degrade: the changeSummary is dropped (→ null, so the renderer
        // falls back to the deterministic band) and the closing still sends.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlock613SnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ProseTimeMismatchReportJson);

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
        Assert.Null(success.StructuredReport.Narrative["en"].ChangeSummary);
        Assert.Equal("A wet start to the weekend.", success.StructuredReport.Narrative["en"].Closing);
    }

    [Fact]
    public async Task ProseTimeWord_AgreesWithTokenLocalRendering_Succeeds()
    {
        // The conservative check must not false-reject: "morning" beside a token
        // that renders to 6:00 AM local (CDT) agrees, so the report sends cleanly.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ProseTimeAgreesReportJson);

        Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
    }

    // ── WX-264: day-part connective + cross-boundary day labels ───────────────

    // 4a: the day-part word binds to the {q:time} token across a single connective
    // ("afternoon AROUND {q:time}") — the hole that shipped the paul_en bug. 11:00Z = 6:00 AM
    // CDT (morning), so "afternoon" contradicts it.
    private const string ProseTimeConnectiveMismatchReportJson = """
        {
          "schemaVersion": 5,
          "narrative": {
            "en": { "changeSummary": "Rain is now expected to develop Saturday afternoon around {q:time:2026-06-13T11:00:00Z}.", "closing": "A wet start to the weekend." }
          }
        }
        """;

    // 4b: a window crossing local midnight named with the TAIL day only — a Mon-evening token
    // (2026-07-06T23:00Z) reaches into a Tue-morning token, but the prose never names Monday.
    private const string ProseCrossMidnightDropsStartDayReportJson = """
        {
          "schemaVersion": 5,
          "narrative": {
            "en": { "changeSummary": "Storms are likely Tuesday morning, {q:time:2026-07-07T11:00:00Z}, after developing from {q:time:2026-07-06T23:00:00Z}.", "closing": "Stay weather-aware." }
          }
        }
        """;

    // 4b acceptance: a cross-boundary window naming BOTH days is fine even with a day-only
    // terminus (the day-part is optional) — "Tuesday evening into the early hours of Wednesday".
    private const string ProseCrossMidnightNamesBothDaysReportJson = """
        {
          "schemaVersion": 5,
          "narrative": {
            "en": { "changeSummary": "Rain is possible Tuesday evening, {q:time:2026-07-07T23:00:00Z}, into the early hours of Wednesday, {q:time:2026-07-08T05:00:00Z}.", "closing": "Stay weather-aware." }
          }
        }
        """;

    // A Mon-evening → Wed-early storm span backing the cross-boundary prose above.
    private const string StormMonWedSnapshotJson = """
        {"schemaVersion":5,"blocks":[
          {"startUtc":"2026-07-06T23:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":22,"max":28},"windKt":{"min":6,"max":14},"precipExpectation":"likely","precipPhenomenon":"thunderstorm","severeFlag":false},
          {"startUtc":"2026-07-07T11:00:00Z","skyState":"partly_cloudy","obscuration":"none","temperatureCelsius":{"min":23,"max":31},"windKt":{"min":5,"max":12},"precipExpectation":"likely","precipPhenomenon":"thunderstorm","severeFlag":false},
          {"startUtc":"2026-07-07T23:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":22,"max":28},"windKt":{"min":6,"max":14},"precipExpectation":"likely","precipPhenomenon":"thunderstorm","severeFlag":false},
          {"startUtc":"2026-07-08T05:00:00Z","skyState":"partly_cloudy","obscuration":"none","temperatureCelsius":{"min":20,"max":24},"windKt":{"min":4,"max":9},"precipExpectation":"likely","precipPhenomenon":"thunderstorm","severeFlag":false}]}
        """;

    [Fact]
    public async Task ProseDaypartWord_BoundAcrossConnective_ContradictsToken_DropsChangeSummary()
    {
        // WX-264 4a: "afternoon around {q:time:11:00Z}" — 6:00 AM CDT is morning; the connective
        // "around" must not let the contradiction slip (the pre-WX-264 no-letters rule did).
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlock613SnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ProseTimeConnectiveMismatchReportJson);

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
        Assert.Null(success.StructuredReport.Narrative["en"].ChangeSummary);
    }

    [Fact]
    public async Task ProseCrossMidnight_NamesTailDayOnly_DropsChangeSummary()
    {
        // WX-264 4b: the span reaches from a Mon-evening token to a Tue-morning token, but the prose
        // names only Tuesday — Monday is dropped, the original paul_en cross-midnight defect.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: StormMonWedSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ProseCrossMidnightDropsStartDayReportJson);

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
        Assert.Null(success.StructuredReport.Narrative["en"].ChangeSummary);
    }

    [Fact]
    public async Task ProseCrossMidnight_NamesBothDays_DayOnlyTerminus_Succeeds()
    {
        // WX-264 4b acceptance: "Tuesday evening into the early hours of Wednesday" names both
        // bounding days; the day-part is optional per terminus, so this must NOT be rejected.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: StormMonWedSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ProseCrossMidnightNamesBothDaysReportJson);

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
        Assert.NotNull(success.StructuredReport.Narrative["en"].ChangeSummary);
    }

    // 4a must NOT bind a day-part word across a RANGE connective to a far token of a different
    // part — "evening into {q:time:00:00 Tue}" is a valid compressed span (the prompt's own
    // both-ends phrasing), not a contradiction. (WX-264 review: range connectives were wrongly
    // in SpanConnectors and would have false-rejected this.)
    private const string ProseRangeConnectiveNotRejectedReportJson = """
        {
          "schemaVersion": 5,
          "narrative": {
            "en": { "changeSummary": "Rain is possible Monday evening into {q:time:2026-07-07T05:00:00Z}, easing thereafter.", "closing": "Stay weather-aware." }
          }
        }
        """;

    [Fact]
    public async Task ProseDaypartWord_RangeConnective_NotBoundToFarToken_Succeeds()
    {
        // "evening into {q:time}" where the token is 00:00 Tue (part 0) — "into" is a range
        // connective, so "evening" (the near terminus) must NOT bind to the far token's part; no
        // contradiction, must send clean. A single token → the 4b both-days check does not fire.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: StormMonWedSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ProseRangeConnectiveNotRejectedReportJson);

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
        Assert.NotNull(success.StructuredReport.Narrative["en"].ChangeSummary);
    }

    // 4b conservative skip: a cross-midnight window named only by relative-day cues (tonight /
    // tomorrow) with no explicit day name is NOT rejected — the validator can't pin the days, so it
    // leans on the prompt rather than risk a false reject into suppression.
    private const string ProseCrossMidnightRelativeDaysReportJson = """
        {
          "schemaVersion": 5,
          "narrative": {
            "en": { "changeSummary": "Rain is possible tonight, {q:time:2026-07-06T23:00:00Z}, into tomorrow morning, {q:time:2026-07-07T11:00:00Z}.", "closing": "Stay weather-aware." }
          }
        }
        """;

    [Fact]
    public async Task ProseCrossMidnight_RelativeDayWordsOnly_Skipped_Succeeds()
    {
        // Two local dates but named only by "tonight"/"tomorrow" — the relative-day escape skips the
        // both-days check (conservative), so the section sends clean rather than degrading.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: StormMonWedSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ProseCrossMidnightRelativeDaysReportJson);

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
        Assert.NotNull(success.StructuredReport.Narrative["en"].ChangeSummary);
    }

    // ── WX-152 closing-claim validation ──────────────────────────────────────
    // Snapshots: first block is 2026-06-09T17:00:00Z = Tue 12:00 CDT (afternoon), so
    // refDate = Tue 6/9; "tonight" = Tue 18:00 → Wed 06:00 local (the 23Z + 05Z blocks).

    // Tue afternoon thunderstorm, then dry Tue-evening and Wed-overnight.
    private const string ClosingStormAfternoonDrySnapshotJson = """
        {"schemaVersion":5,"blocks":[
          {"startUtc":"2026-06-09T17:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":24,"max":31},"windKt":{"min":8,"max":16},"precipExpectation":"possible","precipPhenomenon":"thunderstorm","severeFlag":false},
          {"startUtc":"2026-06-09T23:00:00Z","skyState":"partly_cloudy","obscuration":"none","temperatureCelsius":{"min":20,"max":26},"windKt":{"min":5,"max":10},"precipExpectation":"none","precipPhenomenon":null,"severeFlag":false},
          {"startUtc":"2026-06-10T05:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":18,"max":22},"windKt":{"min":3,"max":8},"precipExpectation":"none","precipPhenomenon":null,"severeFlag":false}
        ]}
        """;

    // WX-177 Defect A fixtures. Sat 2026-06-13 + Sun 2026-06-14 (both local in Cdt = -5).
    private const string WeekendSatDrySunWetSnapshotJson = """
        {"schemaVersion":5,"blocks":[
          {"startUtc":"2026-06-13T18:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":24,"max":32},"windKt":{"min":5,"max":10},"precipExpectation":"none","precipPhenomenon":null,"severeFlag":false},
          {"startUtc":"2026-06-14T18:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":23,"max":29},"windKt":{"min":6,"max":12},"precipExpectation":"likely","precipPhenomenon":"thunderstorm","severeFlag":false}
        ]}
        """;
    private const string WeekendAllDrySnapshotJson = """
        {"schemaVersion":5,"blocks":[
          {"startUtc":"2026-06-13T18:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":24,"max":32},"windKt":{"min":5,"max":10},"precipExpectation":"none","precipPhenomenon":null,"severeFlag":false},
          {"startUtc":"2026-06-14T18:00:00Z","skyState":"partly_cloudy","obscuration":"none","temperatureCelsius":{"min":23,"max":30},"windKt":{"min":6,"max":11},"precipExpectation":"none","precipPhenomenon":null,"severeFlag":false}
        ]}
        """;

    [Fact]
    public async Task AggregateDryClaim_WeekendDryOverStormySunday_DropsClosingOnly()
    {
        // WX-177 Defect A repro: "The weekend stays dry" while Sunday carries storms. The
        // aggregate word "weekend" resolves to {Sat, Sun}; a wet weekend day contradicts the
        // dry claim → closing dropped, rest sends (WX-189).
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: WeekendSatDrySunWetSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("The weekend stays dry, with just a few clouds around."));

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
        Assert.Equal("See the forecast above for the full outlook.", success.StructuredReport.Narrative["en"].Closing);
    }

    [Fact]
    public async Task AggregateDryClaim_WeekendDryAllDry_Succeeds()
    {
        // Not a false reject: "the weekend stays dry" over a genuinely dry Sat AND Sun is a
        // true claim and must survive intact.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: WeekendAllDrySnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("The weekend stays dry, with just a few clouds around."));

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
        Assert.Equal("The weekend stays dry, with just a few clouds around.", success.StructuredReport.Narrative["en"].Closing);
    }

    [Fact]
    public async Task AggregateDryClaim_NegatedDryClaim_NotFlagged_Succeeds()
    {
        // The negation guard: "the weekend won't stay dry" ASSERTS wet, so a wet Sunday agrees
        // with it — flagging it would be a false reject. Must survive intact.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: WeekendSatDrySunWetSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("The weekend won't stay dry the whole way through."));

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
        Assert.Equal("The weekend won't stay dry the whole way through.", success.StructuredReport.Narrative["en"].Closing);
    }

    [Fact]
    public async Task AggregateDryClaim_UnrelatedNegationAfterDryClaim_StillDrops()
    {
        // CodeRabbit PR #183: an unrelated negation elsewhere in the sentence ("an unlikely storm")
        // must NOT bypass the check — the negation is scoped to the dry expression, so "the weekend
        // stays dry" over a wet Sunday is still caught even though "unlikely" appears later.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: WeekendSatDrySunWetSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("The weekend stays dry, although an unlikely storm could develop Sunday."));

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
        Assert.Equal("See the forecast above for the full outlook.", success.StructuredReport.Narrative["en"].Closing);
    }

    [Fact]
    public async Task AggregateDryClaim_InChangeSummary_DropsChangeSummary()
    {
        // The check runs on the change band too (CheckProseClaims covers both sections): a
        // weekend-dry claim in the changeSummary over a wet Sunday drops the changeSummary, closing sends.
        const string report = """
            {
              "schemaVersion": 5,
              "narrative": {
                "en": { "changeSummary": "The weekend stays dry overall.", "closing": "Conditions settle down as the week goes on." }
              }
            }
            """;
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: WeekendSatDrySunWetSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: report);

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
        Assert.Null(success.StructuredReport.Narrative["en"].ChangeSummary);
        Assert.Equal("Conditions settle down as the week goes on.", success.StructuredReport.Narrative["en"].Closing);
    }

    // Tue afternoon dry, but a Tue-evening (tonight) thunderstorm — backs a "tonight" claim.
    private const string ClosingStormEveningSnapshotJson = """
        {"schemaVersion":5,"blocks":[
          {"startUtc":"2026-06-09T17:00:00Z","skyState":"partly_cloudy","obscuration":"none","temperatureCelsius":{"min":24,"max":31},"windKt":{"min":8,"max":16},"precipExpectation":"none","precipPhenomenon":null,"severeFlag":false},
          {"startUtc":"2026-06-09T23:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":20,"max":26},"windKt":{"min":5,"max":10},"precipExpectation":"possible","precipPhenomenon":"thunderstorm","severeFlag":false},
          {"startUtc":"2026-06-10T05:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":18,"max":22},"windKt":{"min":3,"max":8},"precipExpectation":"none","precipPhenomenon":null,"severeFlag":false}
        ]}
        """;

    // Only Tue afternoon — nothing for "tomorrow" (beyond-horizon guard).
    private const string ClosingTodayOnlySnapshotJson = """
        {"schemaVersion":5,"blocks":[
          {"startUtc":"2026-06-09T17:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":24,"max":31},"windKt":{"min":8,"max":16},"precipExpectation":"possible","precipPhenomenon":"thunderstorm","severeFlag":false}
        ]}
        """;

    private static string ClosingOnlyReport(string closing) =>
        "{\"schemaVersion\":5,\"changes\":[],\"narrative\":{\"en\":{\"changeSummary\":null,\"closing\":"
        + JsonSerializer.Serialize(closing) + "}}}";

    [Fact]
    public async Task ClosingClaim_StormTonight_DryEveningOvernight_DropsClosingOnly()
    {
        // The send-1995 repro: closing asserts a storm "tonight" while the snapshot is dry
        // every block from this evening on (the lone storm is this afternoon). WX-189: the
        // closing is dropped to the fallback and the rest still sends.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: ClosingStormAfternoonDrySnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("Tonight carries a modest chance of a storm before conditions settle, with the rest of the week looking calm."));

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
        Assert.Equal("See the forecast above for the full outlook.", success.StructuredReport.Narrative["en"].Closing);
    }

    [Fact]
    public async Task ClosingFault_WithValidChangeSummary_KeepsChangeSummary_DropsClosing()
    {
        // WX-189 independent-section degrade preserves the GOOD section: a clean
        // changeSummary survives while a jargon-leaking closing is dropped to the fallback.
        const string report = """
            {
              "schemaVersion": 5,
              "narrative": {
                "en": { "changeSummary": "Conditions are trending more active.", "closing": "The latest TAF backs the wetter trend." }
              }
            }
            """;
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: RainBlockSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: report);

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson));
        Assert.Equal("Conditions are trending more active.", success.StructuredReport.Narrative["en"].ChangeSummary);
        Assert.Equal("See the forecast above for the full outlook.", success.StructuredReport.Narrative["en"].Closing);
    }

    [Fact]
    public async Task ClosingClaim_StaysDry_NotRejected_Succeeds()
    {
        // "stays mostly dry … no organized rain expected" — negated, must not be flagged.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: ClosingStormAfternoonDrySnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("The week ahead stays mostly dry, with no organized rain expected through the weekend."));

        Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
    }

    [Fact]
    public async Task ClosingClaim_UntimedStorm_NotRejected_Succeeds()
    {
        // A storm named with no resolvable time can't be localized — residual, leans on
        // the prompt, must not be rejected.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: ClosingStormAfternoonDrySnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("Any storm that does develop this week could be lively, even if the odds of seeing one remain low."));

        Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
    }

    [Fact]
    public async Task ClosingClaim_PrecipAtCorrectTime_NotRejected_Succeeds()
    {
        // "a storm this afternoon" — the afternoon block carries a thunderstorm, so the
        // claim is supported and must not be rejected.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: ClosingStormAfternoonDrySnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("A storm is likely this afternoon before things quiet down."));

        Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
    }

    [Fact]
    public async Task ClosingClaim_StormTonight_EveningHasStorm_NotRejected_Succeeds()
    {
        // Same "storm tonight" prose, but here the snapshot's evening block backs it.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: ClosingStormEveningSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("Tonight carries a modest chance of a storm before conditions settle, with the rest of the week looking calm."));

        Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
    }

    [Fact]
    public async Task ClosingClaim_BeyondHorizon_NotRejected_Succeeds()
    {
        // "rain tomorrow" but the snapshot has no Wednesday block — can't verify, so
        // the check skips it (beyond-horizon guard) rather than false-rejecting.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: ClosingTodayOnlySnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("Rain is possible tomorrow across the area as a wet pattern arrives."));

        Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
    }

    [Fact]
    public async Task ClosingClaim_CessationPhrasing_NotRejected_Succeeds()
    {
        // "Showers tapering off this evening" — the evening time word is a deadline by
        // which precip ENDS, not where it occurs; the dry evening block is consistent,
        // so it must NOT be rejected.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: ClosingStormAfternoonDrySnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("Showers are tapering off this evening, leaving a calm and quiet night."));

        Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
    }

    [Fact]
    public async Task ClosingClaim_HedgedRainTonight_DryTonight_DropsClosingOnly()
    {
        // "A little rain tonight" is still an assertion — a dry-tonight snapshot
        // contradicts it. (Guards that "little" was dropped from the skip cues.) WX-189:
        // the closing is dropped to the fallback and the rest still sends.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: ClosingStormAfternoonDrySnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("A little rain is likely tonight before drier air arrives."));

        var success = Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
        Assert.Equal("See the forecast above for the full outlook.", success.StructuredReport.Narrative["en"].Closing);
    }

    [Fact]
    public async Task ClosingClaim_WeekdayPinned_NotRejected_Succeeds()
    {
        // "Saturday afternoon" is pinned to a specific weekday we don't localize — it
        // must not be mis-resolved to TODAY's (dry) afternoon and rejected. (Snapshot's
        // Tue afternoon is dry, so a mis-resolution would fire.)
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: ClosingStormEveningSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("A storm is likely Saturday afternoon as the next system arrives."));

        Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
    }

    [Fact]
    public async Task ClosingClaim_MultipleTimeWords_Ambiguous_NotRejected_Succeeds()
    {
        // Two resolvable time words in one sentence is ambiguous (which one does the
        // storm attach to?) — skip rather than risk a false reject. (Afternoon is dry
        // here, so picking it would fire; the ambiguity guard prevents that.)
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: ClosingStormEveningSnapshotJson,
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: ClosingOnlyReport("A storm is possible this afternoon or this evening."));

        Assert.IsType<ReconcileResult.Success>(await RunReconciler(responseJson, tz: Cdt));
    }

    // ── WX-165: generation-side invention reduction ──────────────────────────
    // Three coordinated fixes that attack invented "What's changed" items at the
    // source rather than only catching them downstream: a low sampling temperature,
    // the diagnostic report kind getting the WX-178 severe-onset band rule (it had
    // fallen through to an empty instruction), and prescriptive retry feedback that
    // names the offending change and says correct-or-remove (don't invent a new one).

    [Fact]
    public async Task ReconcilerRequest_PinsSamplingTemperatureLow()
    {
        // The call had been running at the Anthropic default (1.0); WX-165 pins it low
        // so the structural sampler is tight and retries converge instead of rolling a
        // fresh phantom each attempt.
        var (_, requests) = await RunReconcilerCapturing(
            _ => BuildClaudeResponseJson("""{"schemaVersion":5,"blocks":[]}""", "trace", 10, 10, 0, 0));

        Assert.Single(requests);
        Assert.Contains("\"temperature\":0.5", requests[0]);
    }

    [Fact]
    public async Task DiagnosticKind_ReceivesNearTermSevereOnsetBandInstruction()
    {
        // The Diagnostic kind previously fell through changeAlertInstruction's empty
        // default — the one report kind never given "an empty changes array is the
        // correct answer" coaching — so it filled the band against a stale prior and
        // the phantom degraded the (hard-aborting) startup verification. It now gets
        // the same near-term-severe-onset rule as a scheduled report.
        var (result, requests) = await RunReconcilerCapturing(
            _ => BuildClaudeResponseJson("""{"schemaVersion":5,"blocks":[]}""", "trace", 10, 10, 0, 0),
            reportKind: ReportKind.Diagnostic);

        Assert.IsType<ReconcileResult.Success>(result);
        Assert.Contains("diagnostic (startup verification) report", requests[0]);
        Assert.Contains("a NEW severe hazard", requests[0]);
        Assert.Contains("emit an EMPTY changes array", requests[0]);
    }

    [Fact]
    public async Task ScheduledKind_KeepsScheduledLeadIn_NotDiagnostic()
    {
        // The shared band rule is the same, but each kind keeps its own accurate
        // lead-in — a scheduled report must not be told it is a diagnostic.
        var (_, requests) = await RunReconcilerCapturing(
            _ => BuildClaudeResponseJson("""{"schemaVersion":5,"blocks":[]}""", "trace", 10, 10, 0, 0),
            reportKind: ReportKind.Scheduled);

        Assert.Contains("This is a scheduled report.", requests[0]);
        Assert.DoesNotContain("diagnostic (startup verification) report", requests[0]);
    }

    [Fact]
    public async Task ProseRejection_RetryFeedback_PinsSnapshotAndReauthorsProse()
    {
        // WX-189: the change set is now computed deterministically, so the only contract
        // faults left for a retry are PROSE faults. A raw-UTC-block leak in the closing is
        // a NarrativeProseException, so the replayed correction pins the snapshot byte-for-
        // byte and tells Claude to re-author ONLY the narrative prose — never the generic
        // "fix only that" line, and never any change-array removal text (changes are no
        // longer Claude-authored).
        var (result, requests) = await RunReconcilerCapturing(
            _ => BuildClaudeResponseJson(RainBlockSnapshotJson, "trace", 10, 10, 0, 0, RawUtcLeakReportJson));

        // WX-189 independent-section degrade: after the pinned-snapshot retries exhaust,
        // the offending closing is dropped to the fallback and the report still sends.
        var success = Assert.IsType<ReconcileResult.Success>(result);
        Assert.Equal("See the forecast above for the full outlook.", success.StructuredReport.Narrative["en"].Closing);
        Assert.Equal(3, requests.Count);
        Assert.Contains("Keep your final_snapshot EXACTLY as you submitted it", requests[1]);
        Assert.Contains("Re-author ONLY the narrative prose", requests[1]);
        Assert.DoesNotContain("REMOVE it from the changes array", requests[1]);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    // A fixed UTC-5 zone (US Central in June / CDT) used by the WX-149 prose-token
    // tests: a {q:time} token at 11:00Z renders to 6:00 AM local — "morning", not
    // "afternoon". A custom fixed-offset zone keeps the test deterministic across
    // platforms (no dependency on the host's IANA/Windows time-zone database).
    private static readonly TimeZoneInfo Cdt =
        TimeZoneInfo.CreateCustomTimeZone("Test-CDT", TimeSpan.FromHours(-5), "Test CDT", "Test CDT");

    // ── WX-160 windKt sustained-ceiling normalizer (clamp; WX-180) ────────────
    // windKt carries sustained wind only; a folded gust (windKt.max above every
    // sustained source for the block) is CLAMPED down to the ceiling (WX-180; was a
    // reject → retry → degrade under WX-160), so the contaminated max never reaches
    // the stored baseline AND a gusty forecast no longer degrades every cycle.

    private const string WindCeilingProvisionalJson =
        """{"schemaVersion":5,"blocks":[{"startUtc":"2026-06-09T11:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":18,"max":26},"windKt":{"min":4,"max":12},"precipExpectation":"none","severeFlag":false}]}""";

    private static string WindBlockSnapshotJson(int windMax) =>
        $$"""{"schemaVersion":5,"blocks":[{"startUtc":"2026-06-09T11:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":18,"max":26},"windKt":{"min":4,"max":{{windMax}}},"precipExpectation":"none","severeFlag":false}]}""";

    private static string WindBlockMinMaxSnapshotJson(int windMin, int windMax) =>
        $$"""{"schemaVersion":5,"blocks":[{"startUtc":"2026-06-09T11:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":18,"max":26},"windKt":{"min":{{windMin}},"max":{{windMax}}},"precipExpectation":"none","severeFlag":false}]}""";

    [Fact]
    public async Task WindKtSustained_FoldedGust_ClampedToCeiling_NoRetry()
    {
        // GFS forecasts sustained 12 kt for the block. Claude folds a gust into
        // windKt.max (20 kt), overshooting the sustained ceiling. WX-180: rather than
        // rejecting and retrying (which on a gusty forecast never converged and degraded
        // every cycle — the cost incident), the reconciler clamps windKt.max down to the
        // ceiling (12 kt) on the FIRST attempt — no retry, no degrade.
        var provisional = ForecastSnapshotBody.Deserialize(WindCeilingProvisionalJson);
        int call = 0;
        var result = await RunReconciler(
            _ =>
            {
                call++;
                var json = BuildClaudeResponseJson(WindBlockSnapshotJson(20), "trace", 10, 10, 0, 0);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
            },
            provisional: provisional);

        var success = Assert.IsType<ReconcileResult.Success>(result);
        Assert.Equal(12, success.FinalSnapshot.Blocks[0].WindKt.Max);  // folded gust clamped out
        Assert.Equal(1, call);   // corrected in place on the first attempt — no retry
    }

    [Fact]
    public async Task WindKtSustained_PersistentFold_ClampedAndAccepted()
    {
        // Even if Claude folds the gust on every attempt, WX-180 clamps windKt.max to the
        // sustained ceiling and accepts the result: the contaminated max never reaches the
        // baseline, and there is no degrade loop (the failure mode behind the cost incident).
        var provisional = ForecastSnapshotBody.Deserialize(WindCeilingProvisionalJson);
        var responseJson = BuildClaudeResponseJson(WindBlockSnapshotJson(20), "trace", 10, 10, 0, 0);
        var result = await RunReconciler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            },
            provisional: provisional);

        var success = Assert.IsType<ReconcileResult.Success>(result);
        Assert.Equal(12, success.FinalSnapshot.Blocks[0].WindKt.Max);
    }

    [Fact]
    public async Task WindKtSustained_FoldWithInflatedMin_ClampDoesNotInvertBand()
    {
        // Claude folds a gust AND reports an inflated sustained min (windKt {min:15,max:20})
        // for a block whose sustained ceiling is 12 kt. Clamping max down to 12 must also
        // lower min, so the band never inverts (min must stay <= max) — an inverted band
        // would corrupt the significance-gate baseline and ship on degrade.
        var provisional = ForecastSnapshotBody.Deserialize(WindCeilingProvisionalJson);
        var responseJson = BuildClaudeResponseJson(WindBlockMinMaxSnapshotJson(15, 20), "trace", 10, 10, 0, 0);
        var result = await RunReconciler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            },
            provisional: provisional);

        var success = Assert.IsType<ReconcileResult.Success>(result);
        var wind = success.FinalSnapshot.Blocks[0].WindKt;
        Assert.Equal(12, wind.Max);
        Assert.True(wind.Min <= wind.Max, $"band inverted: min={wind.Min} max={wind.Max}");
    }

    [Fact]
    public async Task WindKtSustained_WithinRoundingTolerance_Accepted()
    {
        // A windKt.max a couple of knots above the sustained ceiling is honest rounding,
        // not a fold (a gust exceeds its sustained wind by far more), and is accepted.
        var provisional = ForecastSnapshotBody.Deserialize(WindCeilingProvisionalJson);
        var responseJson = BuildClaudeResponseJson(WindBlockSnapshotJson(14), "trace", 10, 10, 0, 0);
        var result = await RunReconciler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            },
            provisional: provisional);

        Assert.IsType<ReconcileResult.Success>(result);
    }

    private static async Task<ReconcileResult> RunReconciler(string anthropicResponseJson, bool allowSkip = false, TimeZoneInfo? tz = null, ForecastSnapshot? prior = null)
        => await RunReconciler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(anthropicResponseJson, Encoding.UTF8, "application/json"),
        }, allowSkip, tz: tz, prior: prior);

    private static async Task<ReconcileResult> RunReconciler(Func<HttpRequestMessage, HttpResponseMessage> respond, bool allowSkip = false, string[]? narrativeLanguages = null, TimeZoneInfo? tz = null, ForecastSnapshot? prior = null, ForecastSnapshotBody? provisional = null, WeatherSnapshot? snapshot = null, ReportKind reportKind = ReportKind.Scheduled)
    {
        var http = new HttpClient(new StubHandler(respond));
        var claude = new ClaudeClient(http, apiKey: "test-key", model: "claude-sonnet-4-6", personaPrefix: "Persona text.");
        var reconciler = new ForecastReconciler(claude, Templates);

        return await reconciler.ReconcileAsync(
            snapshot: snapshot ?? BuildSnapshot(),
            provisional: provisional ?? new ForecastSnapshotBody(),
            gfsModelRunUtc: DateTime.UtcNow,
            tafIssuanceUtc: null,
            tafValidToUtc: null,
            prior: prior,
            narrativeLanguages: narrativeLanguages ?? new[] { "en" },
            tz: tz ?? Cdt,
            reportKind: reportKind,
            allowSkip: allowSkip,
            changedSinceLastSend: Array.Empty<TriggerSource>(),
            significanceCfg: new SignificanceGateConfig(),
            nowUtc: DateTime.UtcNow,
            ct: default);
    }

    // WX-165: like RunReconciler, but captures each outbound request body (the JSON
    // POSTed to the Messages API) so a test can assert on the system prompt and the
    // replayed retry corrections. Delegates to RunReconciler with a capturing respond
    // callback so the harness setup lives in one place. responsePerCall is keyed by the
    // 1-based attempt number.
    private static async Task<(ReconcileResult Result, List<string> Requests)> RunReconcilerCapturing(
        Func<int, string> responsePerCall,
        ReportKind reportKind = ReportKind.Scheduled,
        ForecastSnapshot? prior = null,
        TimeZoneInfo? tz = null)
    {
        var requests = new List<string>();
        int call = 0;
        var result = await RunReconciler(
            req =>
            {
                call++;
                requests.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responsePerCall(call), Encoding.UTF8, "application/json"),
                };
            },
            tz: tz, prior: prior, reportKind: reportKind);

        return (result, requests);
    }

    // WX-151: wrap a final_snapshot JSON as a prior ForecastSnapshot for the
    // prior-aware change-verification tests.
    private static ForecastSnapshot PriorOf(string bodyJson) => new()
    {
        StationIcao = "KTEST",
        GeneratedAtUtc = new DateTime(2026, 6, 9, 18, 0, 0, DateTimeKind.Utc),
        SchemaVersion = ForecastSnapshotBody.SchemaVersionCurrent,
        Body = bodyJson,
    };

    private static WeatherSnapshot BuildSnapshot() => new()
    {
        ObservationAvailable = true,
        StationIcao = "KTEST",
        LocalityName = "Test Locality",
        ObservationTimeUtc = new DateTime(2026, 5, 28, 12, 0, 0, DateTimeKind.Utc),
    };

    private static string BuildClaudeResponseJson(
        string finalSnapshotJson,
        string reasoningTrace,
        int inputTokens, int outputTokens,
        int cacheReadInputTokens, int cacheCreationInputTokens,
        string structuredReportJson = ValidStructuredReportJson)
    {
        var input = new
        {
            final_snapshot = JsonDocument.Parse(finalSnapshotJson).RootElement,
            structured_report = JsonDocument.Parse(structuredReportJson).RootElement,
            reasoning_trace = reasoningTrace,
        };
        var inputJson = JsonSerializer.Serialize(input);
        return BuildClaudeResponseJsonWithRawInput(inputJson, inputTokens, outputTokens, cacheReadInputTokens, cacheCreationInputTokens);
    }

    private static string BuildClaudeResponseJsonWithRawInput(
        string toolInputJson,
        int inputTokens = 10,
        int outputTokens = 10,
        int cacheReadInputTokens = 0,
        int cacheCreationInputTokens = 0,
        string toolName = "submit_reconciled_report",
        string stopReason = "tool_use")
    {
        return $$"""
            {
              "id": "msg_test",
              "type": "message",
              "role": "assistant",
              "content": [
                {
                  "type": "tool_use",
                  "id": "toolu_test",
                  "name": "{{toolName}}",
                  "input": {{toolInputJson}}
                }
              ],
              "model": "claude-sonnet-4-6",
              "stop_reason": "{{stopReason}}",
              "usage": {
                "input_tokens": {{inputTokens}},
                "output_tokens": {{outputTokens}},
                "cache_read_input_tokens": {{cacheReadInputTokens}},
                "cache_creation_input_tokens": {{cacheCreationInputTokens}}
              }
            }
            """;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_respond(request));
    }
}