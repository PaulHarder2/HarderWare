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
    // One seed-backed template store for the whole harness — the reconciler reads Tok.ClosingFallback
    // AND (WX-336) the per-language validator DayPartWords from it, so rebuilding it (and re-parsing the
    // migration) per test is wasted work (CodeRabbit). The en seed marks DayPart1–4 ValidatorUse=Yes
    // (SeedTemplateStore); we append es DayPart1=madrugada (Yes) so the es {q:time}↔day-part check
    // (SpanishDayPart_MadrugadaContradictsToken) still has its one validator-safe word — mirroring the
    // WX-335 prod flag, since the en-only seed carries no es row.
    private static readonly LanguageTemplateStore Templates = BuildHarnessTemplates();

    private static LanguageTemplateStore BuildHarnessTemplates()
    {
        var rows = SeedTemplateStore.SeedRows().ToList();
        var es = new Language { Id = 2, IsoCode = "es", DisplayName = "Spanish", CultureName = "es-US" };
        rows.Add(new LanguageTemplate
        {
            LanguageId = es.Id,
            Language = es,
            Token = Tok.DayPart1,
            Phrase = "madrugada",
            ValidatorUse = ValidatorUse.Yes,
            Representable = true,
        });
        return new LanguageTemplateStore(() => rows);
    }

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

    // A structured report carrying only an English closing, for the closing prose-check tests above.
    private static string ClosingOnlyReport(string closing) =>
        "{\"schemaVersion\":5,\"changes\":[],\"narrative\":{\"en\":{\"changeSummary\":null,\"closing\":"
        + JsonSerializer.Serialize(closing) + "}}}";

    // ── WX-506: the change band is a second call, written from the computed changes ──
    // The band scenario: the prior forecast had the 11Z block dry, the reconciled one has rain possible there,
    // so the detector computes one Rain-appearing change and an unscheduled send shows a band. Call 1 is the
    // reconciliation; every later call is the change-band call. The prose checks (WX-139/149/168/264/284) now
    // run on the band call's output, so these tests drive that call rather than the first call's prose.

    private static readonly DateTime BandNowUtc = new(2026, 6, 9, 6, 0, 0, DateTimeKind.Utc);

    private const string BandPriorDryJson = """
        {"schemaVersion":5,"blocks":[{"startUtc":"2026-06-09T11:00:00Z","skyState":"partly_cloudy","obscuration":"none","temperatureCelsius":{"min":22,"max":30},"windKt":{"min":5,"max":12},"precipExpectation":"none","severeFlag":false}]}
        """;

    private const string BandClosing = "A wet start to the day, then a calmer stretch settles in.";

    private static string BandReportJson(IReadOnlyList<string> languages, string? firstCallChangeSummary = null, string closing = BandClosing) =>
        "{\"schemaVersion\":5,\"narrative\":{"
        + string.Join(",", languages.Select(l =>
            $"\"{l}\":{{\"changeSummary\":{JsonSerializer.Serialize(firstCallChangeSummary)},\"closing\":{JsonSerializer.Serialize(closing)}}}"))
        + "}}";

    private static string BandToolResponse(IReadOnlyDictionary<string, string> summaries, string stopReason = "tool_use") =>
        BuildClaudeResponseJsonWithRawInput(
            JsonSerializer.Serialize(new { changeSummary = summaries }),
            inputTokens: 7, outputTokens: 3, toolName: "submit_change_summary", stopReason: stopReason);

    private static Dictionary<string, string> En(string text) => new() { ["en"] = text };

    // Runs the band scenario. bandResponse is keyed by the 1-based band attempt; null returns HTTP 400.
    private static async Task<(ReconcileResult Result, List<string> Requests)> RunBand(
        Func<int, string?> bandResponse,
        string[]? languages = null,
        ReportKind reportKind = ReportKind.Unscheduled,
        Func<int, string>? reconcileResponse = null,
        int reconcileCalls = 1)
    {
        languages ??= ["en"];
        reconcileResponse ??= _ => BuildClaudeResponseJson(RainBlockSnapshotJson, "trace", 10, 10, 0, 0, BandReportJson(languages));
        var requests = new List<string>();
        int call = 0;
        var result = await RunReconciler(
            req =>
            {
                call++;
                requests.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                var body = call <= reconcileCalls ? reconcileResponse(call) : bandResponse(call - reconcileCalls);
                return body is null
                    ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}") }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            },
            narrativeLanguages: languages, tz: Cdt, prior: PriorOf(BandPriorDryJson),
            reportKind: reportKind, nowUtc: BandNowUtc);
        return (result, requests);
    }

    [Fact]
    public async Task Band_Unscheduled_WrittenBySecondCall_FromComputedChanges()
    {
        var (result, requests) = await RunBand(_ => BandToolResponse(En("Rain is now possible this morning, where the prior forecast was dry.")));

        var success = Assert.IsType<ReconcileResult.Success>(result);
        Assert.Equal(2, requests.Count);
        Assert.Contains("\"name\":\"submit_change_summary\"", requests[1]);
        Assert.Single(success.StructuredReport.Changes);
        Assert.Equal("Rain is now possible this morning, where the prior forecast was dry.", success.StructuredReport.Narrative["en"].ChangeSummary);
        Assert.Equal(BandClosing, success.StructuredReport.Narrative["en"].Closing);
        // Both calls are billed.
        Assert.Equal(17, success.Tokens.InputTokens);
        Assert.Equal(13, success.Tokens.OutputTokens);
    }

    [Fact]
    public async Task Band_Request_CarriesOnlyTheComputedFacts_NoSecondForecast()
    {
        // The defect: the band was written beside the provisional snapshot and the TAF, and send 9341
        // reported the provisional's value as the prior's. The band call must see neither.
        var (_, requests) = await RunBand(_ => BandToolResponse(En("Rain is now possible this morning.")));

        var band = requests[1];
        Assert.Contains("computed_changes", band);
        Assert.Contains("precipitation was none, now possible rain", band);
        Assert.Contains("block_local_labels", band);
        Assert.DoesNotContain("provisional_snapshot.body", band);
        Assert.DoesNotContain("prior_snapshot.body", band);
        Assert.DoesNotContain("current_forecast", band);
        Assert.DoesNotContain("current_observation", band);
    }

    [Fact]
    public async Task Band_FirstCallChangeSummary_IsDiscarded()
    {
        // The first call's schema no longer offers changeSummary. Text it sends anyway never reaches the band:
        // the band call's output replaces it, and a failed band call leaves null, not the first call's text.
        var (result, requests) = await RunBand(
            _ => null,
            reconcileResponse: _ => BuildClaudeResponseJson(RainBlockSnapshotJson, "trace", 10, 10, 0, 0,
                BandReportJson(["en"], firstCallChangeSummary: "The afternoon has been upgraded — the prior forecast was dry.")));

        var success = Assert.IsType<ReconcileResult.Success>(result);
        Assert.Equal(2, requests.Count);
        Assert.Null(success.StructuredReport.Narrative["en"].ChangeSummary);
        Assert.DoesNotContain("changeSummary", ExtractFirstToolSchema(requests[0]));
    }

    [Fact]
    public async Task Band_ApiFailure_FallsBackToDeterministicBand_StillSends()
    {
        var (result, requests) = await RunBand(_ => null);

        var success = Assert.IsType<ReconcileResult.Success>(result);
        Assert.Equal(2, requests.Count);                                   // no retry on a transport failure
        Assert.Null(success.StructuredReport.Narrative["en"].ChangeSummary);
        Assert.Single(success.StructuredReport.Changes);                   // the fallback band renders from these
    }

    [Fact]
    public async Task Band_UnexpectedException_StaysInTheBandStep_NoReconciliationRetry()
    {
        // A tool input that is not an object makes TryGetProperty throw InvalidOperationException, which the band
        // call's own catch does not cover. The band step runs inside the reconciliation call's retry block, so
        // unless it is contained there it re-runs the whole reconciliation.
        var (result, requests) = await RunBand(_ => BuildClaudeResponseJsonWithRawInput("\"not an object\"", toolName: "submit_change_summary"));

        var success = Assert.IsType<ReconcileResult.Success>(result);
        Assert.Equal(2, requests.Count);
        Assert.Null(success.StructuredReport.Narrative["en"].ChangeSummary);
        // The band call was billed (10 input tokens, on top of the reconciliation's 10) before the step threw;
        // its tokens still count.
        Assert.Equal(20, success.Tokens.InputTokens);
    }

    [Fact]
    public async Task Band_Truncated_FallsBackToDeterministicBand()
    {
        var (result, requests) = await RunBand(_ => BandToolResponse(En("Rain is now"), stopReason: "max_tokens"));

        var success = Assert.IsType<ReconcileResult.Success>(result);
        Assert.Equal(2, requests.Count);
        Assert.Null(success.StructuredReport.Narrative["en"].ChangeSummary);
    }

    [Fact]
    public async Task Band_RejectedOnce_RetriesWithFeedback_ThenAccepts()
    {
        var (result, requests) = await RunBand(attempt => attempt == 1
            ? BandToolResponse(En("The latest TAF adds rain this morning."))
            : BandToolResponse(En("Rain is now possible this morning.")));

        var success = Assert.IsType<ReconcileResult.Success>(result);
        Assert.Equal(3, requests.Count);
        Assert.Contains("\"is_error\":true", requests[2]);
        Assert.Contains("Your previous change band was rejected", requests[2]);
        Assert.Equal("Rain is now possible this morning.", success.StructuredReport.Narrative["en"].ChangeSummary);
    }

    [Fact]
    public async Task Band_MissingLanguage_IsRejected_ThenFallsBack()
    {
        var (result, requests) = await RunBand(
            _ => BandToolResponse(En("Rain is now possible this morning.")),
            languages: ["en", "es"]);

        var success = Assert.IsType<ReconcileResult.Success>(result);
        Assert.Equal(3, requests.Count);
        Assert.All(success.StructuredReport.Narrative.Values, n => Assert.Null(n.ChangeSummary));
    }

    [Fact]
    public async Task Band_NotCalled_WhenNoChanges()
    {
        // Prior == final: nothing changed, so there is no band to write.
        var (result, requests) = await RunBand(
            _ => throw new InvalidOperationException("the band call must not run"),
            reconcileResponse: _ => BuildClaudeResponseJson(BandPriorDryJson, "trace", 10, 10, 0, 0, BandReportJson(["en"])));

        var success = Assert.IsType<ReconcileResult.Success>(result);
        Assert.Single(requests);
        Assert.Empty(success.StructuredReport.Changes);
    }

    [Fact]
    public async Task Band_Called_ScheduledWithNearTermSevereOnset()
    {
        // The other side of the scheduled rule: a block going severe in the near term keeps the band, so the band
        // call runs. Without this, a band step that always suppressed scheduled reports would pass.
        const string severe = """
            {"schemaVersion":5,"blocks":[{"startUtc":"2026-06-09T11:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":22,"max":30},"windKt":{"min":5,"max":12},"precipExpectation":"likely","precipPhenomenon":"thunderstorm","severeFlag":true}]}
            """;
        var (result, requests) = await RunBand(
            _ => BandToolResponse(En("Severe storms are now possible this morning.")),
            reportKind: ReportKind.Scheduled,
            reconcileResponse: _ => BuildClaudeResponseJson(severe, "trace", 10, 10, 0, 0, BandReportJson(["en"])));

        var success = Assert.IsType<ReconcileResult.Success>(result);
        Assert.Equal(2, requests.Count);
        Assert.Equal("Severe storms are now possible this morning.", success.StructuredReport.Narrative["en"].ChangeSummary);
    }

    [Fact]
    public async Task Band_NotCalled_ScheduledWithoutSevereOnset()
    {
        // A scheduled report shows a band only for a near-term severe onset; ReportWorker strips any other,
        // so the band call is not paid for.
        var (result, requests) = await RunBand(
            _ => throw new InvalidOperationException("the band call must not run"),
            reportKind: ReportKind.Scheduled);

        var success = Assert.IsType<ReconcileResult.Success>(result);
        Assert.Single(requests);
        Assert.Single(success.StructuredReport.Changes);
        Assert.Null(success.StructuredReport.Narrative["en"].ChangeSummary);
    }

    // The prose checks, now applied to the band call's output. `reject` = both band attempts return this prose
    // and the band falls back to null; `accept` = it is kept verbatim.
    [Theory]
    // WX-139 synoptic mechanism
    [InlineData("Winds turn gusty with rain arriving this evening as a frontal boundary pushes through.", "attributes a synoptic mechanism")]
    [InlineData("Breezy conditions build this evening, with the drier air sitting just in front of the coast.", null)]
    [InlineData("Breezy conditions build this evening, with the drier air sitting just in  front of the coast.", null)]
    // WX-284 register and likelihood
    [InlineData("Breezy conditions build with a few showers this evening.", "uses the precipitation register")]
    [InlineData("Widespread rain is likely.", "renders a precipitation likelihood")]
    [InlineData("Snow showers are possible through the afternoon.", null)]
    // WX-149 / WX-264 day-part word vs {q:time} (11:00Z = 06:00 CDT, morning; 05:00Z = 00:00 CDT, early hours).
    // The "around" row has no directly adjacent day-part word, so only the WX-264 connective binding can reject it.
    [InlineData("Rain is now possible Saturday afternoon, {q:time:2026-06-13T11:00:00Z}.", "prose time-of-day word contradicts the token")]
    [InlineData("Rain is now possible Saturday afternoon around {q:time:2026-06-13T11:00:00Z}.", "prose time-of-day word contradicts the token")]
    [InlineData("Rain develops this morning, {q:time:2026-06-09T11:00:00Z}.", null)]
    [InlineData("Rain is possible Monday evening into {q:time:2026-07-07T05:00:00Z}, easing thereafter.", null)]
    // WX-149 raw UTC and jargon
    [InlineData("Rain is now possible in the 12-18Z block.", "leaks raw UTC block notation")]
    [InlineData("The latest TAF adds rain this morning.", "uses the internal/aviation term")]
    public async Task Band_ProseChecks_ApplyToTheBandCall(string prose, string? rejectedBecause)
    {
        var (result, requests) = await RunBand(_ => BandToolResponse(En(prose)));

        var success = Assert.IsType<ReconcileResult.Success>(result);
        if (rejectedBecause is null)
        {
            Assert.Equal(2, requests.Count);
            Assert.Equal(prose, success.StructuredReport.Narrative["en"].ChangeSummary);
        }
        else
        {
            // A rejection retries once, and the retry carries the rule that fired — so each row proves ITS rule.
            Assert.Equal(3, requests.Count);
            Assert.Contains(rejectedBecause, requests[2]);
            Assert.Null(success.StructuredReport.Narrative["en"].ChangeSummary);
        }
    }

    [Theory]
    // WX-139 es mechanism vs the positional "frente a"; WX-168 es day-part (18:00Z = 13:00 CDT, afternoon)
    [InlineData("Los vientos se vuelven racheados con algo de lluvia esta tarde mientras avanza un frente frío.", "attributes a synoptic mechanism")]
    [InlineData("Los vientos aumentan esta tarde, con el aire más seco justo frente a la costa.", null)]
    [InlineData("Lluvia en la madrugada, {q:time:2026-06-09T18:00:00Z}.", "prose time-of-day word contradicts the token")]
    public async Task Band_SpanishProseChecks_ApplyToTheBandCall_AndDropEveryLanguage(string esProse, string? rejectedBecause)
    {
        const string en = "Rain is now possible this morning.";
        bool accepted = rejectedBecause is null;
        var (result, requests) = await RunBand(
            _ => BandToolResponse(new Dictionary<string, string> { ["en"] = en, ["es"] = esProse }),
            languages: ["en", "es"]);

        var success = Assert.IsType<ReconcileResult.Success>(result);
        Assert.Equal(accepted ? 2 : 3, requests.Count);
        if (!accepted)
            Assert.Contains(rejectedBecause!, requests[2]);
        Assert.Equal(accepted ? esProse : null, success.StructuredReport.Narrative["es"].ChangeSummary);
        Assert.Equal(accepted ? en : null, success.StructuredReport.Narrative["en"].ChangeSummary);
    }

    [Fact]
    public async Task Band_Survives_WhenTheClosingIsDropped()
    {
        // WX-189 independent-section degrade still keeps the good section: a jargon closing that never converges
        // drops to the fallback, and the band call still runs on the cleaned report.
        var (result, requests) = await RunBand(
            _ => BandToolResponse(En("Rain is now possible this morning.")),
            reconcileResponse: _ => BuildClaudeResponseJson(RainBlockSnapshotJson, "trace", 10, 10, 0, 0,
                BandReportJson(["en"], closing: "The latest TAF backs the wetter trend this morning.")),
            reconcileCalls: 3);

        var success = Assert.IsType<ReconcileResult.Success>(result);
        Assert.Equal(4, requests.Count);
        Assert.Equal("Rain is now possible this morning.", success.StructuredReport.Narrative["en"].ChangeSummary);
        Assert.Equal("See the forecast above for the full outlook.", success.StructuredReport.Narrative["en"].Closing);
    }

    // The first tool definition's input_schema in a captured request, as raw JSON.
    private static string ExtractFirstToolSchema(string requestJson)
    {
        using var doc = JsonDocument.Parse(requestJson);
        return doc.RootElement.GetProperty("tools")[0].GetProperty("input_schema").GetRawText();
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
    public async Task DiagnosticKind_KeepsDiagnosticLeadIn_AndNoBandInstruction()
    {
        // WX-506: the band is written by a separate call, so the reconciliation prompt no longer instructs on
        // changeSummary for any kind — only the kind's own lead-in remains.
        var (result, requests) = await RunReconcilerCapturing(
            _ => BuildClaudeResponseJson("""{"schemaVersion":5,"blocks":[]}""", "trace", 10, 10, 0, 0),
            reportKind: ReportKind.Diagnostic);

        Assert.IsType<ReconcileResult.Success>(result);
        Assert.Contains("diagnostic (startup verification) report", requests[0]);
        Assert.DoesNotContain("a NEW severe hazard", requests[0]);
        Assert.DoesNotContain("changeSummary", ExtractFirstToolSchema(requests[0]));
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

    private static async Task<ReconcileResult> RunReconciler(Func<HttpRequestMessage, HttpResponseMessage> respond, bool allowSkip = false, string[]? narrativeLanguages = null, TimeZoneInfo? tz = null, ForecastSnapshot? prior = null, ForecastSnapshotBody? provisional = null, WeatherSnapshot? snapshot = null, ReportKind reportKind = ReportKind.Scheduled, DateTime? nowUtc = null)
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
            nowUtc: nowUtc ?? DateTime.UtcNow,
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