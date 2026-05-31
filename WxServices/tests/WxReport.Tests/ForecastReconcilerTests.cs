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
// the parsed three artifacts or in ReconcileResult.Failure with a reason.
// The HTTP-failure test deliberately returns a 400-class status so the
// ClaudeClient retry loop (which retries only 429/529/5xx) exits immediately,
// keeping the test fast.

public class ForecastReconcilerTests
{
    // ── happy path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task HappyPath_ReturnsSuccess_WithThreeArtifactsAndTokens()
    {
        var responseJson = BuildClaudeResponseJson(
            emailBody: "<p>Test report</p>",
            finalSnapshotJson: """{"schemaVersion":1,"blocks":[]}""",
            reasoningTrace: "All three steps clean.",
            inputTokens: 100, outputTokens: 50,
            cacheReadInputTokens: 80, cacheCreationInputTokens: 10);

        var result = await RunReconciler(responseJson);

        var success = Assert.IsType<ReconcileResult.Success>(result);
        Assert.Equal("<p>Test report</p>", success.EmailBody);
        Assert.Equal("All three steps clean.", success.ReasoningTrace);
        Assert.Empty(success.FinalSnapshot.Blocks);
        Assert.Equal(100, success.Tokens.InputTokens);
        Assert.Equal(50, success.Tokens.OutputTokens);
        Assert.Equal(80, success.Tokens.CacheReadInputTokens);
        Assert.Equal(10, success.Tokens.CacheCreationInputTokens);
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
        // skip_send with no reasoning_trace fails schema validation rather than
        // silently suppressing a send with no recorded rationale.
        var responseJson = BuildClaudeResponseJsonWithRawInput(
            """{ }""",
            toolName: "skip_send");

        var result = await RunReconciler(responseJson, allowSkip: true);

        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("Schema validation failed", failure.Reason);
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

    // ── failure: tool input missing email_body ──────────────────────────────

    [Fact]
    public async Task ToolInputMissingEmailBody_ReturnsFailure()
    {
        var responseJson = BuildClaudeResponseJsonWithRawInput("""
            {
              "final_snapshot": { "schemaVersion": 1, "blocks": [] },
              "reasoning_trace": "trace"
            }
            """);

        var result = await RunReconciler(responseJson);

        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("Schema validation failed", failure.Reason);
    }

    // ── failure: tool input missing final_snapshot ──────────────────────────

    [Fact]
    public async Task ToolInputMissingFinalSnapshot_ReturnsFailure()
    {
        var responseJson = BuildClaudeResponseJsonWithRawInput("""
            {
              "email_body": "<p>body</p>",
              "reasoning_trace": "trace"
            }
            """);

        var result = await RunReconciler(responseJson);

        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("Schema validation failed", failure.Reason);
    }

    // ── failure: tool input missing reasoning_trace ─────────────────────────

    [Fact]
    public async Task ToolInputMissingReasoningTrace_ReturnsFailure()
    {
        var responseJson = BuildClaudeResponseJsonWithRawInput("""
            {
              "email_body": "<p>body</p>",
              "final_snapshot": { "schemaVersion": 1, "blocks": [] }
            }
            """);

        var result = await RunReconciler(responseJson);

        var failure = Assert.IsType<ReconcileResult.Failure>(result);
        Assert.Contains("Schema validation failed", failure.Reason);
    }

    // ── failure: garbage final_snapshot JSON ────────────────────────────────

    [Fact]
    public async Task FinalSnapshotIsGarbageString_ReturnsFailure()
    {
        // final_snapshot is a string where ForecastSnapshotBody expects an
        // object — JsonElement.GetRawText returns the quoted string, which
        // ForecastSnapshotBody.Deserialize will fail on.
        var responseJson = BuildClaudeResponseJsonWithRawInput("""
            {
              "email_body": "<p>body</p>",
              "final_snapshot": "this is not a snapshot body",
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
              "gustOutlook": "none",
              "precipExpectation": "none",
              "precipPhenomenon": "rain",
              "severeFlag": false,
              "visibilityExpectation": "good"
            }
            """;
        var responseJson = BuildClaudeResponseJsonWithRawInput($$"""
            {
              "email_body": "<p>body</p>",
              "final_snapshot": { "schemaVersion": 1, "blocks": [{{blockJson}}] },
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

    private static async Task<ReconcileResult> RunReconciler(Func<HttpRequestMessage, HttpResponseMessage> respond, bool allowSkip = false)
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
            language: "English",
            recipientName: "Test User",
            tz: TimeZoneInfo.Utc,
            isFirstReport: false,
            scheduledHour: 7,
            units: null,
            changeSeverity: ChangeSeverity.None,
            previousMetarIcao: null,
            allowSkip: allowSkip);
    }

    private static WeatherSnapshot BuildSnapshot() => new()
    {
        ObservationAvailable = true,
        StationIcao = "KTEST",
        LocalityName = "Test Locality",
        ObservationTimeUtc = new DateTime(2026, 5, 28, 12, 0, 0, DateTimeKind.Utc),
    };

    private static string BuildClaudeResponseJson(
        string emailBody,
        string finalSnapshotJson,
        string reasoningTrace,
        int inputTokens, int outputTokens,
        int cacheReadInputTokens, int cacheCreationInputTokens)
    {
        var input = new
        {
            email_body = emailBody,
            final_snapshot = JsonDocument.Parse(finalSnapshotJson).RootElement,
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
        string toolName = "submit_reconciled_report")
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
              "stop_reason": "tool_use",
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