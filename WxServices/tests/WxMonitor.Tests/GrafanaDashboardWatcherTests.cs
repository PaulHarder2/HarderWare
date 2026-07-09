// Tests for the WX-279 grafana zero/low-dashboard watcher: probes grafana's served-dashboard count
// and alerts — after a consecutive-reading debounce that rides over the boot/provisioning window —
// when the count falls below the configured floor. The probe is injected, so no HTTP or DB is needed.

using MetarParser.Data;

using Microsoft.EntityFrameworkCore;

using WxMonitor.Svc;
using WxMonitor.Svc.Watchers;

using WxServices.Common;

using Xunit;

namespace WxMonitor.Tests;

public sealed class GrafanaDashboardWatcherTests
{
    private static readonly DateTime Now = new(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

    // The watcher never opens the DB — a provider-less options object satisfies the required context field.
    private static readonly DbContextOptions<WeatherDataContext> NoDb =
        new DbContextOptionsBuilder<WeatherDataContext>().Options;

    private static WatcherContext Ctx(MonitorState state, string url = "http://localhost:3000", int floor = 4) => new()
    {
        Config = new MonitorConfig { GrafanaUrl = url, GrafanaDashboardFloor = floor, GrafanaServiceAccountToken = "tok" },
        UtcNow = Now,
        DbOptions = NoDb,
        Paths = new WxPaths(Path.GetTempPath()),
        State = state,
    };

    /// <summary>A probe that replays a fixed sequence of readings; the last value repeats once exhausted.</summary>
    private sealed class FakeProbe(params int?[] readings) : IGrafanaDashboardProbe
    {
        private int _i;
        public int Calls { get; private set; }

        public Task<int?> GetDashboardCountAsync(string baseUrl, string token, CancellationToken ct)
        {
            Calls++;
            var value = readings[Math.Min(_i, readings.Length - 1)];
            _i++;
            return Task.FromResult(value);
        }
    }

    // ── disabled ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Disabled_WhenUrlBlank_NeverProbes_NoFinding()
    {
        var probe = new FakeProbe(0);
        var state = new MonitorState();

        var findings = await new GrafanaDashboardWatcher(probe).RunAsync(Ctx(state, url: ""), CancellationToken.None);

        Assert.Empty(findings);
        Assert.Equal(0, probe.Calls);                 // no probe when disabled
        Assert.Equal(0, state.GrafanaSubFloorStreak);
    }

    [Fact]
    public async Task Disabled_WhenFloorNotPositive_NeverProbes_NoFinding()
    {
        var probe = new FakeProbe(0);

        var findings = await new GrafanaDashboardWatcher(probe).RunAsync(Ctx(new MonitorState(), floor: 0), CancellationToken.None);

        Assert.Empty(findings);
        Assert.Equal(0, probe.Calls);
    }

    // ── healthy ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Healthy_AtOrAboveFloor_NoFinding()
    {
        var state = new MonitorState();

        var findings = await new GrafanaDashboardWatcher(new FakeProbe(4)).RunAsync(Ctx(state), CancellationToken.None);

        Assert.Empty(findings);
        Assert.Equal(0, state.GrafanaSubFloorStreak);
    }

    [Fact]
    public async Task Healthy_ResetsAnAccumulatedStreak()
    {
        var state = new MonitorState { GrafanaSubFloorStreak = 1 };

        var findings = await new GrafanaDashboardWatcher(new FakeProbe(5)).RunAsync(Ctx(state), CancellationToken.None);

        Assert.Empty(findings);
        Assert.Equal(0, state.GrafanaSubFloorStreak);   // a healthy reading clears the shortfall streak
    }

    // ── debounce + alert ─────────────────────────────────────────────────────

    [Fact]
    public async Task FirstShortfall_IsDebounced_NoFindingYet()
    {
        var state = new MonitorState();

        var findings = await new GrafanaDashboardWatcher(new FakeProbe(0)).RunAsync(Ctx(state), CancellationToken.None);

        Assert.Empty(findings);                          // one reading is not enough — rules out the boot window
        Assert.Equal(1, state.GrafanaSubFloorStreak);
    }

    [Fact]
    public async Task SecondConsecutiveShortfall_Alerts_WithCooldownWiredToState()
    {
        var state = new MonitorState();
        var watcher = new GrafanaDashboardWatcher(new FakeProbe(0));

        Assert.Empty(await watcher.RunAsync(Ctx(state), CancellationToken.None));   // streak 1
        var findings = await watcher.RunAsync(Ctx(state), CancellationToken.None);  // streak 2 → alert

        var f = Assert.Single(findings);
        Assert.Equal(GrafanaDashboardWatcher.WatcherId, f.WatcherId);
        Assert.Contains("too few dashboards", f.Subject);
        Assert.Equal(GrafanaDashboardWatcher.AlertThreshold, state.GrafanaSubFloorStreak);

        // The cooldown slot reads/writes the grafana-specific state field (email-rate-limits repeats).
        Assert.NotNull(f.Cooldown);
        Assert.Null(f.Cooldown!.LastSentUtc);
        f.Cooldown.MarkSent(Now);
        Assert.Equal(Now, state.LastGrafanaDashboardAlertSentUtc);
    }

    [Fact]
    public async Task PartialShortfall_BelowFloorButNonZero_AlsoAlerts()
    {
        var state = new MonitorState();
        var watcher = new GrafanaDashboardWatcher(new FakeProbe(3));   // 3 of an expected 4

        Assert.Empty(await watcher.RunAsync(Ctx(state), CancellationToken.None));
        Assert.Single(await watcher.RunAsync(Ctx(state), CancellationToken.None));
    }

    [Fact]
    public async Task Streak_CapsAtThreshold_AndKeepsAlerting()
    {
        var state = new MonitorState();
        var watcher = new GrafanaDashboardWatcher(new FakeProbe(0));

        Assert.Empty(await watcher.RunAsync(Ctx(state), CancellationToken.None));    // 1
        Assert.Single(await watcher.RunAsync(Ctx(state), CancellationToken.None));   // 2 → alert
        Assert.Single(await watcher.RunAsync(Ctx(state), CancellationToken.None));   // 3 → still alerting

        Assert.Equal(GrafanaDashboardWatcher.AlertThreshold, state.GrafanaSubFloorStreak);   // capped
    }

    // ── probe failure is inconclusive, not a zero ────────────────────────────

    [Fact]
    public async Task ProbeFailure_IsInconclusive_NoFinding_LeavesStreak()
    {
        var state = new MonitorState { GrafanaSubFloorStreak = 1 };

        var findings = await new GrafanaDashboardWatcher(new FakeProbe((int?)null)).RunAsync(Ctx(state), CancellationToken.None);

        Assert.Empty(findings);                          // a down/unreachable grafana is not a confirmed shortfall
        Assert.Equal(1, state.GrafanaSubFloorStreak);    // streak neither advanced nor reset
    }

    // ── ParseDashboardCount ──────────────────────────────────────────────────

    [Theory]
    [InlineData("[]", 0)]
    [InlineData("[{\"uid\":\"a\"}]", 1)]
    [InlineData("[{\"uid\":\"a\"},{\"uid\":\"b\"},{\"uid\":\"c\"},{\"uid\":\"d\"}]", 4)]
    public void ParseDashboardCount_ReturnsArrayLength(string json, int expected)
        => Assert.Equal(expected, GrafanaDashboardWatcher.ParseDashboardCount(json));

    [Theory]
    [InlineData("{\"message\":\"unauthorized\"}")]   // an object, not an array (e.g. a 401 body)
    [InlineData("not json at all")]
    [InlineData("")]
    public void ParseDashboardCount_NonArrayOrMalformed_ReturnsNull(string json)
        => Assert.Null(GrafanaDashboardWatcher.ParseDashboardCount(json));
}