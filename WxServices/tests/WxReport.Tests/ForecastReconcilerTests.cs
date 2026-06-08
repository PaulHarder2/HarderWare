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
    // A valid WX-128/130 structured_report whose 'en' narrative clears the
    // per-language degeneracy floor (MinVisibleNarrativeChars). The narrative
    // carries only the two judgment sections (changeSummary + closing); the
    // current-conditions table and per-day grid are rendered deterministically.
    private const string ValidStructuredReportJson = """
        {
          "schemaVersion": 4,
          "changes": [],
          "narrative": {
            "en": {
              "changeSummary": null,
              "closing": "A wet stretch ahead — keep an umbrella handy through the weekend."
            }
          }
        }
        """;

    // Schema-valid (closing is non-blank) but below the per-language visible floor:
    // the WX-120 fall-safe degeneracy case the reconciler turns into a skip/Failure.
    private const string DegenerateStructuredReportJson = """
        {
          "schemaVersion": 4,
          "changes": [],
          "narrative": {
            "en": { "changeSummary": null, "closing": "ok" }
          }
        }
        """;

    // ── happy path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task HappyPath_ReturnsSuccess_WithThreeArtifactsAndTokens()
    {
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: """{"schemaVersion":4,"blocks":[]}""",
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
              "final_snapshot": { "schemaVersion": 4, "blocks": [] },
              "reasoning_trace": "trace"
            }
            """);

        var result = await RunReconciler(responseJson);

        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("missing required field", failure.Reason);
        Assert.Contains("structured_report", failure.Reason);
    }

    [Fact]
    public async Task StructuredReportMissingRequestedLanguage_IsFatal_Retried()
    {
        // The cycle requests en AND es; the narrative is internally valid but
        // carries en only. The per-call contract failure is fatal now (WX-130) and
        // routes through the retry → Failure path like any schema violation.
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: """{"schemaVersion":4,"blocks":[]}""",
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

        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("Schema validation failed", failure.Reason);
        Assert.Equal(3, calls); // retried (bounded) like any malformed structured artifact
    }

    [Fact]
    public async Task StructuredReportWithExtraLanguage_IsFatal_Retried()
    {
        // Exact-set contract: an unrequested language is unvalidated content for no
        // recipient — fatal now (WX-130), retried then Failure.
        var withExtra = ValidStructuredReportJson.Replace(
            "\"narrative\": {",
            "\"narrative\": { \"es\": { \"changeSummary\": null, \"closing\": \"Un cierre razonable y suficientemente largo.\" },");
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: """{"schemaVersion":4,"blocks":[]}""",
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: withExtra);

        var result = await RunReconciler(responseJson);

        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("Schema validation failed", failure.Reason);
    }

    [Fact]
    public async Task DegenerateNarrative_WhenGuaranteedSend_ReturnsFailure()
    {
        // Schema-valid but near-blank narrative (below the per-language floor): on a
        // guaranteed send it cannot become a skip — it fails closed so the
        // provisional stays and the next cycle self-heals (WX-120 carried forward).
        var responseJson = BuildClaudeResponseJson(
            finalSnapshotJson: """{"schemaVersion":4,"blocks":[]}""",
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
            finalSnapshotJson: """{"schemaVersion":4,"blocks":[]}""",
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
              "final_snapshot": { "schemaVersion": 4, "blocks": [] },
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
              "final_snapshot": { "schemaVersion": 4, "blocks": [] },
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
              "final_snapshot": { "schemaVersion": 4, "blocks": [] },
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
            { "final_snapshot": { "schemaVersion": 4, "blocks": [] }, "reasoning_trace": "trace" }
            """);
        var valid = BuildClaudeResponseJson(
            finalSnapshotJson: """{"schemaVersion":4,"blocks":[]}""",
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
            { "final_snapshot": { "schemaVersion": 4, "blocks": [] }, "reasoning_trace": "trace" }
            """); // default tokens: 10 in / 10 out / 0 / 0
        var valid = BuildClaudeResponseJson(
            finalSnapshotJson: """{"schemaVersion":4,"blocks":[]}""",
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
            finalSnapshotJson: """{"schemaVersion":4,"blocks":[]}""",
            reasoningTrace: "trace",
            inputTokens: 10, outputTokens: 10, cacheReadInputTokens: 0, cacheCreationInputTokens: 0,
            structuredReportJson: DegenerateStructuredReportJson);
        var valid = BuildClaudeResponseJson(
            finalSnapshotJson: """{"schemaVersion":4,"blocks":[]}""",
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
            { "final_snapshot": { "schemaVersion": 4, "blocks": [] }, "reasoning_trace": "trace" }
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
            { "final_snapshot": { "schemaVersion": 4, "blocks": [] }, "reasoning_trace": "trace" }
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
              "final_snapshot": { "schemaVersion": 4, "blocks": [{{blockJson}}] },
              "structured_report": {{ValidStructuredReportJson}},
              "reasoning_trace": "trace"
            }
            """);

        var result = await RunReconciler(responseJson);

        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("Schema validation failed", failure.Reason);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static async Task<ReconcileResult> RunReconciler(string anthropicResponseJson, bool allowSkip = false)
        => await RunReconciler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(anthropicResponseJson, Encoding.UTF8, "application/json"),
        }, allowSkip);

    private static async Task<ReconcileResult> RunReconciler(Func<HttpRequestMessage, HttpResponseMessage> respond, bool allowSkip = false, string[]? narrativeLanguages = null)
    {
        var http = new HttpClient(new StubHandler(respond));
        var claude = new ClaudeClient(http, apiKey: "test-key", model: "claude-sonnet-4-6", personaPrefix: "Persona text.");
        var reconciler = new ForecastReconciler(claude);

        return await reconciler.ReconcileAsync(
            snapshot: BuildSnapshot(),
            provisional: new ForecastSnapshotBody(),
            gfsModelRunUtc: DateTime.UtcNow,
            tafIssuanceUtc: null,
            tafValidToUtc: null,
            prior: null,
            narrativeLanguages: narrativeLanguages ?? new[] { "en" },
            tz: TimeZoneInfo.Utc,
            changeSeverity: ChangeSeverity.None,
            previousMetarIcao: null,
            allowSkip: allowSkip,
            changedSinceLastSend: Array.Empty<TriggerSource>());
    }

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
