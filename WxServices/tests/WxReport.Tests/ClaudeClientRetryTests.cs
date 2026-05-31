using System.Net;
using System.Text;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

// WX-100: classification of failures in ClaudeClient's send loop.
//   * a host-shutdown cancellation (request token signalled) aborts immediately
//     and is NOT retried;
//   * an HttpClient timeout (TaskCanceledException, token NOT signalled) is NOT
//     retried either — the dedicated Claude client's generous timeout makes a
//     timeout a real stall, so the pass fails and recovers next cycle rather than
//     retrying a multi-minute call up to 3x (code-review finding on the first cut,
//     which had reclassified timeouts as transient).
// Each test carries a wall-clock Timeout so a propagation regression fails fast
// instead of hanging the suite.

public class ClaudeClientRetryTests
{
    private const string ValidToolUseResponse = """
        {
          "id": "msg_test",
          "type": "message",
          "role": "assistant",
          "content": [
            { "type": "tool_use", "id": "toolu_x", "name": "submit_reconciled_report", "input": { "ok": true } }
          ],
          "model": "claude-sonnet-4-6",
          "stop_reason": "tool_use",
          "usage": { "input_tokens": 1, "output_tokens": 1 }
        }
        """;

    private const string SkipSendResponse = """
        {
          "id": "msg_test",
          "type": "message",
          "role": "assistant",
          "content": [
            { "type": "tool_use", "id": "toolu_x", "name": "skip_send", "input": { "reasoning_trace": "no news" } }
          ],
          "model": "claude-sonnet-4-6",
          "stop_reason": "tool_use",
          "usage": { "input_tokens": 1, "output_tokens": 1 }
        }
        """;

    [Fact(Timeout = 10_000)]
    public async Task SkipSend_OnNonSkippableCycle_IsRejectedAtBoundary()
    {
        // WX-80: skip_send is only offered when allowSkip is true. A skip_send
        // returned on a guaranteed (allowSkip=false) cycle violates the contract
        // ClaudeClient itself set via tool_choice, so the parse layer rejects it
        // rather than letting an un-offered tool propagate to the reconciler.
        var handler = new ScriptedHandler((req, ct) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SkipSendResponse, Encoding.UTF8, "application/json"),
            }));
        var client = new ClaudeClient(new HttpClient(handler), apiKey: "k", model: "claude-sonnet-4-6", personaPrefix: "persona");

        var result = await client.InvokeReconciliationAsync("rules", "payload", allowSkip: false, CancellationToken.None);

        Assert.Null(result); // un-offered tool rejected at the boundary
    }

    [Fact(Timeout = 10_000)]
    public async Task SkipSend_OnSkippableCycle_IsAccepted()
    {
        // The same skip_send on a cycle that offered it (allowSkip=true) is valid
        // and flows through as the skip_send tool result.
        var handler = new ScriptedHandler((req, ct) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SkipSendResponse, Encoding.UTF8, "application/json"),
            }));
        var client = new ClaudeClient(new HttpClient(handler), apiKey: "k", model: "claude-sonnet-4-6", personaPrefix: "persona");

        var result = await client.InvokeReconciliationAsync("rules", "payload", allowSkip: true, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("skip_send", result!.ToolName);
    }

    private const string DualToolUseResponse = """
        {
          "id": "msg_test",
          "type": "message",
          "role": "assistant",
          "content": [
            { "type": "tool_use", "id": "toolu_a", "name": "skip_send", "input": { "reasoning_trace": "no news" } },
            { "type": "tool_use", "id": "toolu_b", "name": "submit_reconciled_report", "input": { "ok": true } }
          ],
          "model": "claude-sonnet-4-6",
          "stop_reason": "tool_use",
          "usage": { "input_tokens": 1, "output_tokens": 1 }
        }
        """;

    [Fact(Timeout = 10_000)]
    public async Task MultipleToolUseBlocks_AreRejected_NotOrderDependent()
    {
        // WX-80: a self-contradictory response carrying BOTH submit_reconciled_report
        // and skip_send is malformed — the parser must reject it (exactly-one-block
        // contract) rather than arbitrarily resolve the conflict by block ordering.
        // allowSkip:true so both names are individually permitted; the rejection is
        // purely about the block count.
        var handler = new ScriptedHandler((req, ct) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(DualToolUseResponse, Encoding.UTF8, "application/json"),
            }));
        var client = new ClaudeClient(new HttpClient(handler), apiKey: "k", model: "claude-sonnet-4-6", personaPrefix: "persona");

        var result = await client.InvokeReconciliationAsync("rules", "payload", allowSkip: true, CancellationToken.None);

        Assert.Null(result); // ambiguous multi-block response rejected at the boundary
    }

    [Fact(Timeout = 10_000)]
    public async Task Timeout_IsNotRetried_AndFailsThePass()
    {
        var calls = 0;
        // Handler stalls past the 100ms client timeout -> HttpClient raises a
        // TaskCanceledException with the request token NOT signalled (the WX-100
        // timeout shape).  This must fail the pass without a second attempt.
        var handler = new ScriptedHandler(async (req, ct) =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(TimeSpan.FromSeconds(5), ct); // cancelled by the client timeout
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ValidToolUseResponse, Encoding.UTF8, "application/json"),
            };
        });
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(100) };
        var client = new ClaudeClient(http, apiKey: "k", model: "claude-sonnet-4-6", personaPrefix: "persona");

        var result = await client.InvokeReconciliationAsync("rules", "payload", allowSkip: false, CancellationToken.None);

        Assert.Null(result); // timeout fails the reconciliation
        Assert.Equal(1, calls); // not retried — a single attempt
    }

    [Fact(Timeout = 10_000)]
    public async Task ConnectionError_IsRetried_ThenSucceeds()
    {
        var calls = 0;
        // First attempt throws a connection-level HttpRequestException (transient);
        // second attempt answers.  Confirms connection errors are still retried.
        var handler = new ScriptedHandler((req, ct) =>
        {
            var attempt = Interlocked.Increment(ref calls);
            if (attempt == 1)
                throw new HttpRequestException("simulated connection reset");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ValidToolUseResponse, Encoding.UTF8, "application/json"),
            });
        });
        var client = new ClaudeClient(new HttpClient(handler), apiKey: "k", model: "claude-sonnet-4-6", personaPrefix: "persona");

        var result = await client.InvokeReconciliationAsync("rules", "payload", allowSkip: false, CancellationToken.None);

        Assert.NotNull(result); // recovered on retry
        Assert.Equal(2, calls); // first attempt failed, second succeeded
    }

    [Fact(Timeout = 10_000)]
    public async Task HostShutdownCancellation_IsNotRetried()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var calls = 0;
        // Handler blocks until the request token is cancelled, simulating an
        // in-flight call when the host stops.
        var handler = new ScriptedHandler(async (req, ct) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct); // cancelled by host shutdown
            return new HttpResponseMessage(HttpStatusCode.OK); // unreachable
        });
        var client = new ClaudeClient(new HttpClient(handler), apiKey: "k", model: "claude-sonnet-4-6", personaPrefix: "persona");

        var call = client.InvokeReconciliationAsync("rules", "payload", allowSkip: false, cts.Token);
        await started.Task;
        cts.Cancel();
        var result = await call;

        Assert.Null(result); // shutdown surfaces as a failed reconciliation
        Assert.Equal(1, calls); // aborted immediately — no retry attempts
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        public ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _respond(request, cancellationToken);
    }
}