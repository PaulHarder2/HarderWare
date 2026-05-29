using System.Net;
using System.Text;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

// WX-100: the reconciliation HttpClient timeout used to surface as a
// TaskCanceledException that skipped ClaudeClient's retry loop (it caught only
// HttpRequestException) and failed the whole pass.  These tests pin the
// corrected classification:
//   * a real HttpClient timeout (user token NOT signalled) is treated as a
//     transient failure and retried, and
//   * a genuine host-shutdown cancellation (user token signalled) aborts
//     immediately and is NOT retried.

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

    [Fact]
    public async Task Timeout_IsClassifiedTransient_AndRetried()
    {
        var calls = 0;
        // First attempt stalls past the 100ms client timeout -> HttpClient
        // raises a TaskCanceledException with the user token NOT signalled
        // (the WX-100 timeout shape).  Second attempt answers immediately.
        var handler = new ScriptedHandler(async (req, ct) =>
        {
            var attempt = Interlocked.Increment(ref calls);
            if (attempt == 1)
                await Task.Delay(TimeSpan.FromSeconds(5), ct); // cancelled by the client timeout
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ValidToolUseResponse, Encoding.UTF8, "application/json"),
            };
        });
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(100) };
        var client = new ClaudeClient(http, apiKey: "k", model: "claude-sonnet-4-6", personaPrefix: "persona");

        var result = await client.InvokeReconciliationAsync("rules", "payload", CancellationToken.None);

        Assert.NotNull(result); // recovered on retry rather than failing the pass
        Assert.Equal(2, calls); // first attempt timed out, second succeeded
    }

    [Fact]
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