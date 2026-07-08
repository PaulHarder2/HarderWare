// Characterization ("golden") tests for one MonitorWorker cycle.
//
// These pin the *current* alerting behavior of the monitor — which alerts fire, to
// whom, with what subject, and how state advances — BEFORE the WX-275 refactor onto
// the IWatcher/ISink framework. The same tests are re-run unchanged AFTER the refactor;
// identical results are the proof the restructure preserved behavior. They intentionally
// exercise the whole cycle through the injected seams (IEmailer, clock, IMonitorStateStore)
// rather than any single detector.
//
// Note the injected clock governs everything the worker itself times (cooldown, METAR age,
// body footers); heartbeat staleness is judged by HeartbeatChecker against the real wall
// clock (it is not seamed), so heartbeat files are written relative to DateTime.UtcNow.

using MetarParser.Data;
using MetarParser.Data.Entities;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

using WxMonitor.Svc;

using WxServices.Common;

using Xunit;

namespace WxMonitor.Tests;

public sealed class MonitorCycleCharacterizationTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);

    private readonly List<string> _tempFiles = [];
    private readonly SqliteConnection _conn = new("DataSource=:memory:");

    public void Dispose()
    {
        _conn.Dispose();
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { /* best-effort cleanup */ }
        }
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private sealed class FakeEmailer : IEmailer
    {
        public List<(string To, string Subject, string Body)> Sent { get; } = [];

        public Task<bool> SendAsync(
            string toAddress, string subject, string plainBody,
            string? htmlBody = null,
            IReadOnlyDictionary<string, string>? inlineImages = null,
            string? toName = null,
            CancellationToken ct = default)
        {
            Sent.Add((toAddress, subject, plainBody));
            return Task.FromResult(true);
        }
    }

    private sealed class InMemoryStateStore(MonitorState seed) : IMonitorStateStore
    {
        public MonitorState State { get; private set; } = seed;
        public MonitorState Load() => State;
        public void Save(MonitorState state) => State = state;
    }

    /// <summary>Builds a SQLite in-memory WeatherData schema (remapping nvarchar(max) → TEXT, per WX-210).</summary>
    private DbContextOptions<WeatherDataContext> NewDb()
    {
        _conn.Open();
        var options = new DbContextOptionsBuilder<WeatherDataContext>().UseSqlite(_conn).Options;
        using var ctx = new WeatherDataContext(options);
        var script = ctx.Database.GenerateCreateScript().Replace("nvarchar(max)", "TEXT");
        ctx.Database.ExecuteSqlRaw(script);
        return options;
    }

    private void SeedMostRecentMetar(DbContextOptions<WeatherDataContext> db, DateTime observationUtc)
    {
        using var ctx = new WeatherDataContext(db);
        ctx.Metars.Add(new MetarRecord
        {
            StationIcao = "KTST",
            ReportType = "METAR",
            ObservationUtc = observationUtc,
        });
        ctx.SaveChanges();
    }

    private string NewTempFile(string content)
    {
        var path = Path.GetTempFileName();
        _tempFiles.Add(path);
        File.WriteAllText(path, content);
        return path;
    }

    private static string LogLine(DateTime ts, string level, string message)
        => $"{ts:yyyy-MM-dd HH:mm:ss.fff} {level,-5} [Test.cs::Method:1] {message}";

    private static IConfiguration Config(IDictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private MonitorWorker NewWorker(
        IConfiguration config, DbContextOptions<WeatherDataContext> db,
        FakeEmailer emailer, IMonitorStateStore state)
        => new(config, db, _ => emailer, state, () => Now);

    /// <summary>One watched service; caller supplies log + heartbeat file paths. METAR check disabled unless overridden.</summary>
    private static Dictionary<string, string?> BaseConfig(string logFile, string heartbeatFile) => new()
    {
        ["Monitor:AlertEmail"] = "alerts@example.com",
        ["Monitor:AlertOnSeverity"] = "ERROR",
        ["Monitor:AlertCooldownMinutes"] = "60",
        ["Monitor:MetarStalenessThresholdMinutes"] = "0",
        ["Monitor:WatchedServices:0:Name"] = "WxParser.Svc",
        ["Monitor:WatchedServices:0:LogFile"] = logFile,
        ["Monitor:WatchedServices:0:HeartbeatFile"] = heartbeatFile,
        ["Monitor:WatchedServices:0:HeartbeatMaxAgeMinutes"] = "30",
    };

    private static MonitorState SeededLogBaseline(DateTime lastSeen, DateTime? lastAlert = null)
    {
        var state = new MonitorState();
        var svc = state.GetOrCreate("WxParser.Svc");
        svc.LastSeenLogTimestamp = lastSeen;
        svc.LastLogAlertSentUtc = lastAlert;
        return state;
    }

    // ── log-scan ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task LogError_NewEntryPastWatermark_NotOnCooldown_SendsOneAlert()
    {
        var db = NewDb();
        var log = NewTempFile(LogLine(Now.AddMinutes(-5), "ERROR", "boom disk full") + Environment.NewLine);
        var hb = NewTempFile(DateTime.UtcNow.ToString("O")); // fresh → no heartbeat alert
        var emailer = new FakeEmailer();
        var state = new InMemoryStateStore(SeededLogBaseline(Now.AddMinutes(-10)));

        await NewWorker(Config(BaseConfig(log, hb)), db, emailer, state).RunCycleAsync(CancellationToken.None);

        var msg = Assert.Single(emailer.Sent);
        Assert.Equal("alerts@example.com", msg.To);
        Assert.Equal("[WxMonitor] WxParser.Svc — 1 new log error(s)", msg.Subject);
        Assert.Contains("boom disk full", msg.Body);

        var svc = state.State.GetOrCreate("WxParser.Svc");
        Assert.Equal(Now.AddMinutes(-5), svc.LastSeenLogTimestamp);
        Assert.Equal(Now, svc.LastLogAlertSentUtc);
    }

    [Fact]
    public async Task LogError_WithinCooldown_Suppressed_ButWatermarkStillAdvances()
    {
        var db = NewDb();
        var log = NewTempFile(LogLine(Now.AddMinutes(-5), "ERROR", "boom disk full") + Environment.NewLine);
        var hb = NewTempFile(DateTime.UtcNow.ToString("O"));
        var emailer = new FakeEmailer();
        // Last alert sent 5 minutes ago; cooldown is 60 → suppressed.
        var state = new InMemoryStateStore(SeededLogBaseline(Now.AddMinutes(-10), lastAlert: Now.AddMinutes(-5)));

        await NewWorker(Config(BaseConfig(log, hb)), db, emailer, state).RunCycleAsync(CancellationToken.None);

        Assert.Empty(emailer.Sent);
        Assert.Equal(Now.AddMinutes(-5), state.State.GetOrCreate("WxParser.Svc").LastSeenLogTimestamp);
    }

    // ── heartbeat ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Heartbeat_Stale_SendsOneAlert()
    {
        var db = NewDb();
        var log = NewTempFile(LogLine(Now.AddMinutes(-5), "INFO", "just info") + Environment.NewLine);
        var hb = NewTempFile(DateTime.UtcNow.AddMinutes(-60).ToString("O")); // age ~60 > 30 → stale
        var emailer = new FakeEmailer();
        var state = new InMemoryStateStore(SeededLogBaseline(Now.AddMinutes(-10)));

        await NewWorker(Config(BaseConfig(log, hb)), db, emailer, state).RunCycleAsync(CancellationToken.None);

        var msg = Assert.Single(emailer.Sent);
        Assert.Equal("[WxMonitor] WxParser.Svc — service may be stopped", msg.Subject);
        Assert.Contains("stopped, crashed, or is hung", msg.Body);
        Assert.Equal(Now, state.State.GetOrCreate("WxParser.Svc").LastHeartbeatAlertSentUtc);
    }

    [Fact]
    public async Task Heartbeat_Fresh_NoAlert()
    {
        var db = NewDb();
        var log = NewTempFile(LogLine(Now.AddMinutes(-5), "INFO", "just info") + Environment.NewLine);
        var hb = NewTempFile(DateTime.UtcNow.ToString("O")); // fresh
        var emailer = new FakeEmailer();
        var state = new InMemoryStateStore(SeededLogBaseline(Now.AddMinutes(-10)));

        await NewWorker(Config(BaseConfig(log, hb)), db, emailer, state).RunCycleAsync(CancellationToken.None);

        Assert.Empty(emailer.Sent);
    }

    // ── METAR staleness ──────────────────────────────────────────────────────

    [Fact]
    public async Task Metar_Stale_SendsOneAlert()
    {
        var db = NewDb();
        SeedMostRecentMetar(db, Now.AddMinutes(-200)); // > 120 threshold → stale
        var log = NewTempFile(LogLine(Now.AddMinutes(-5), "INFO", "just info") + Environment.NewLine);
        var hb = NewTempFile(DateTime.UtcNow.ToString("O")); // fresh → service alerts silent
        var cfg = BaseConfig(log, hb);
        cfg["Monitor:MetarStalenessThresholdMinutes"] = "120";
        var emailer = new FakeEmailer();
        var state = new InMemoryStateStore(SeededLogBaseline(Now.AddMinutes(-10)));

        await NewWorker(Config(cfg), db, emailer, state).RunCycleAsync(CancellationToken.None);

        var msg = Assert.Single(emailer.Sent);
        Assert.Equal("[WxMonitor] METAR data is stale — no recent observations", msg.Subject);
        Assert.Contains("stale METAR data", msg.Body);
        Assert.Equal(Now, state.State.LastMetarStalenessAlertSentUtc);
    }

    [Fact]
    public async Task Metar_Fresh_NoAlert()
    {
        var db = NewDb();
        SeedMostRecentMetar(db, Now.AddMinutes(-10)); // < 120 threshold → fresh
        var log = NewTempFile(LogLine(Now.AddMinutes(-5), "INFO", "just info") + Environment.NewLine);
        var hb = NewTempFile(DateTime.UtcNow.ToString("O"));
        var cfg = BaseConfig(log, hb);
        cfg["Monitor:MetarStalenessThresholdMinutes"] = "120";
        var emailer = new FakeEmailer();
        var state = new InMemoryStateStore(SeededLogBaseline(Now.AddMinutes(-10)));

        await NewWorker(Config(cfg), db, emailer, state).RunCycleAsync(CancellationToken.None);

        Assert.Empty(emailer.Sent);
    }
}