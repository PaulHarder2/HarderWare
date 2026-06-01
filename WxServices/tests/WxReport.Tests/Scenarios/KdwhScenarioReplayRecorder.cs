using System.Runtime.CompilerServices;

using WxInterp;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests.Scenarios;

// Opt-in recorder: captures the live Anthropic responses for the KDWH replay
// fixtures. A no-op in CI; runs only when WX_RECORD_KDWH=1 and ANTHROPIC_API_KEY
// are set. On a dev box (from the repo's WxServices directory):
//
//   WX_RECORD_KDWH=1 ANTHROPIC_API_KEY=sk-ant-... \
//     dotnet test WxServices.CI.slnf --filter FullyQualifiedName~KdwhScenarioReplayRecorder
//
// It overwrites two committed fixtures with genuine Claude output:
//   Fixtures/kdwh-853-skip.recorded.json        (expected: skip_send)
//   Fixtures/kdwh-853-storm-send.recorded.json  (expected: submit_reconciled_report)
// and writes Fixtures/kdwh-853-skip.trace.txt — the real skip reasoning_trace,
// which is also WX-81 AC#4's evidence (tier-aware language in a real trace).
//
// Re-run after any material change to the reconciliation prompt or these fixtures.
public class KdwhScenarioReplayRecorder
{
    [Fact]
    public async Task Record_KdwhFixtures()
    {
        if (Environment.GetEnvironmentVariable("WX_RECORD_KDWH") != "1")
            return; // opt-in; no-op in CI and in normal local runs

        // Opted in but misconfigured: fail loudly rather than no-op and let the
        // operator commit stale fixtures believing they were freshly captured.
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(apiKey),
            "WX_RECORD_KDWH=1 but ANTHROPIC_API_KEY is not set — cannot record. Set the key and re-run.");

        var model = Environment.GetEnvironmentVariable("WX_RECORD_MODEL") ?? "claude-sonnet-4-6";

        // Negative case: the real 8:53 cycle — expect skip_send.
        var skip = await RecordOne("kdwh-853-skip.recorded.json", apiKey!, model,
            Kdwh20260421Fixture.BuildSnapshot0853());
        var notNews = Assert.IsType<ReconcileResult.NotNews>(skip);
        await File.WriteAllTextAsync(FixturePath("kdwh-853-skip.trace.txt"), notNews.ReasoningTrace);

        // Positive control: the off-forecast severe-storm mutation — expect a send.
        var send = await RecordOne("kdwh-853-storm-send.recorded.json", apiKey!, model,
            Kdwh20260421Fixture.BuildSnapshot0853StormMutation());
        Assert.IsType<ReconcileResult.Success>(send);
    }

    private static async Task<ReconcileResult> RecordOne(string fixtureName, string apiKey, string model, WeatherSnapshot snapshot)
    {
        var http = new HttpClient(new RecordingHandler(FixturePath(fixtureName), new SocketsHttpHandler()));
        var claude = new ClaudeClient(http, apiKey: apiKey, model: model,
            personaPrefix: "You write friendly, accurate weather emails for a general audience.");
        return await Kdwh20260421Fixture.Reconcile(claude, snapshot);
    }

    private static string FixturePath(string name, [CallerFilePath] string thisFile = "")
        => Path.Combine(Path.GetDirectoryName(thisFile)!, "Fixtures", name);
}