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

        var result = await client.InvokeReconciliationAsync("rules", "payload", CancellationToken.None);

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

        var result = await client.InvokeReconciliationAsync("rules", "payload", CancellationToken.None);

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

        var call = client.InvokeReconciliationAsync("rules", "payload", cts.Token);
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