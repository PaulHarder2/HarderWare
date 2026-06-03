using System.Net;
using System.Runtime.CompilerServices;
using System.Text;

using WxInterp;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests.Scenarios;

// Scenario replay for the 2026-04-21 KDWH double-send (WX-82, WX-47 epic). The
// automated guarantee that the motivating bug stays fixed: the 8:53 unscheduled
// update must NOT be sent, because the observed light rain matches the rainy
// forecast the 4:53 report already committed.
//
// Decision-seam altitude (per design): drive ForecastReconciler.ReconcileAsync
// with the KDWH fixtures and a *recorded* Claude response, and assert the send
// decision (NotNews / Success). The recorded responses are captured once on a
// dev box by KdwhScenarioReplayRecorder; CI replays them deterministically.
public class KdwhScenarioReplayTests
{
    // ── pre-filter: the new METAR routes the cycle TO Claude (no early skip) ──

    // The original framing assumed the pre-filter would catch the duplicate. It
    // does not: at 8:53 the weather has materially changed — light rain has begun
    // since the rain-free 4:53 baseline — so the WX-110 material signature advances
    // and the pre-filter passes the cycle through to Claude's gate (which then
    // judges the rain matches the committed rainy forecast → not news). This asserts
    // that mechanic: a genuine material change still routes to Claude, even though
    // an unchanged hourly re-observation now pre-filter-skips.
    [Fact]
    public void PreFilter_NewMetarArrival_RoutesToClaude()
    {
        var changed = Kdwh20260421Fixture.Identity0853()
            .ChangedSourcesSince(Kdwh20260421Fixture.PriorInputHash());

        Assert.Contains(TriggerSource.Metar, changed);
    }

    // ── negative assertion: matching-forecast case produces no unscheduled send ──

    [Fact]
    public async Task MatchingForecast_ClaudeSkips_NoUnscheduledSend()
    {
        var recorded = ReadRecorded("kdwh-853-skip.recorded.json");

        var result = await Replay(recorded, Kdwh20260421Fixture.BuildSnapshot0853());

        var notNews = Assert.IsType<ReconcileResult.NotNews>(result);
        Assert.False(string.IsNullOrWhiteSpace(notNews.ReasoningTrace));
    }

    // ── positive control: a genuinely off-forecast case DOES send ────────────

    [Fact]
    public async Task OffForecastStorm_Sends()
    {
        var recorded = ReadRecorded("kdwh-853-storm-send.recorded.json");

        var result = await Replay(recorded, Kdwh20260421Fixture.BuildSnapshot0853StormMutation());

        Assert.IsType<ReconcileResult.Success>(result);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<ReconcileResult> Replay(string anthropicResponseJson, WeatherSnapshot snapshot)
    {
        var http = new HttpClient(new CannedResponseHandler(anthropicResponseJson));
        var claude = new ClaudeClient(http, apiKey: "test-key", model: "claude-sonnet-4-6", personaPrefix: "Test persona.");
        return await Kdwh20260421Fixture.Reconcile(claude, snapshot);
    }

    private static string ReadRecorded(string name, [CallerFilePath] string thisFile = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(thisFile)!, "Fixtures", name);
        if (!File.Exists(path))
            Assert.Fail($"Recorded fixture not found: {name}. Run KdwhScenarioReplayRecorder " +
                        "(WX_RECORD_KDWH=1 with ANTHROPIC_API_KEY set) on a dev box to capture it.");
        return File.ReadAllText(path);
    }

    private sealed class CannedResponseHandler : HttpMessageHandler
    {
        private readonly string _json;
        public CannedResponseHandler(string json) => _json = json;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
    }
}