// Tests for the WX-273 report-error watcher: forward-only scanning of shipped report bodies
// against operator-defined, language-scoped patterns, with findings written to a JSONL file.

using System.Text.Json;

using MetarParser.Data;
using MetarParser.Data.Entities;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

using WxMonitor.Svc;
using WxMonitor.Svc.Watchers;

using WxServices.Common;

using Xunit;

namespace WxMonitor.Tests;

public sealed class ReportErrorWatcherTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly string _installRoot =
        Path.Combine(Path.GetTempPath(), "wx273-" + Guid.NewGuid().ToString("N"));

    public ReportErrorWatcherTests() => Directory.CreateDirectory(Path.Combine(_installRoot, "Logs"));

    public void Dispose()
    {
        _conn.Dispose();
        try { Directory.Delete(_installRoot, recursive: true); } catch { /* best-effort */ }
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private DbContextOptions<WeatherDataContext> NewDb()
    {
        _conn.Open();
        var options = new DbContextOptionsBuilder<WeatherDataContext>().UseSqlite(_conn).Options;
        using var ctx = new WeatherDataContext(options);
        var script = ctx.Database.GenerateCreateScript().Replace("nvarchar(max)", "TEXT");
        ctx.Database.ExecuteSqlRaw(script);
        return options;
    }

    private void WritePatterns(params ReportErrorPattern[] patterns)
        => File.WriteAllText(
            Path.Combine(_installRoot, "report-error-patterns.json"),
            JsonSerializer.Serialize(patterns));

    private static long SeedLanguage(WeatherDataContext db, string iso)
    {
        var lang = new Language { IsoCode = iso, DisplayName = iso, IsEnabled = true };
        db.Languages.Add(lang);
        db.SaveChanges();
        return lang.Id;
    }

    private static void SeedRecipient(WeatherDataContext db, string recipientId, long? languageId)
    {
        db.Recipients.Add(new Recipient { RecipientId = recipientId, Email = $"{recipientId}@x", Name = recipientId, LanguageId = languageId });
        db.SaveChanges();
    }

    private int _seq;

    private int SeedSend(WeatherDataContext db, string recipientId, string? body, bool sent = true, bool diagnostic = false)
    {
        // CommittedSend has a required FK to ForecastSnapshot (enforced in this SQLite harness);
        // seed a minimal snapshot per send, unique on (StationIcao, GeneratedAtUtc).
        var snapshot = new ForecastSnapshot { StationIcao = "KTST", GeneratedAtUtc = Now.AddSeconds(_seq++), Body = "{}" };
        db.ForecastSnapshots.Add(snapshot);
        db.SaveChanges();

        var send = new CommittedSend
        {
            ForecastSnapshotId = snapshot.Id,
            RecipientId = recipientId,
            EmailBody = body,
            CreatedAtUtc = Now,
            SentAtUtc = sent ? Now : null,
            IsDiagnostic = diagnostic,
        };
        db.CommittedSends.Add(send);
        db.SaveChanges();
        return send.Id;
    }

    private WatcherContext Ctx(DbContextOptions<WeatherDataContext> db, MonitorState state) => new()
    {
        Config = new MonitorConfig(),
        UtcNow = Now,
        DbOptions = db,
        Paths = new WxPaths(_installRoot),
        State = state,
    };

    private static ReportErrorPattern Pattern(string id, string language, string regex)
        => new() { Id = id, Language = language, Regex = regex, Description = $"catches {id}", Severity = "warn" };

    // ── forward-monitoring baseline ──────────────────────────────────────────

    [Fact]
    public async Task FirstRun_Baselines_NoFindings_SetsWatermark()
    {
        var db = NewDb();
        using (var ctx = new WeatherDataContext(db))
            SeedSend(ctx, "paulh", "contains BADSTRING");
        WritePatterns(Pattern("p1", "*", "BADSTRING"));

        var state = new MonitorState();   // LastReportScanId == null → first run
        var findings = await new ReportErrorWatcher().RunAsync(Ctx(db, state), CancellationToken.None);

        Assert.Empty(findings);                         // history is not scanned
        Assert.NotNull(state.LastReportScanUtc);        // but the watermark is baselined forward
    }

    // ── scan + finding shape ─────────────────────────────────────────────────

    [Fact]
    public async Task Match_PastWatermark_ProducesFindingWithFields_AdvancesWatermark()
    {
        var db = NewDb();
        int id;
        using (var ctx = new WeatherDataContext(db))
            id = SeedSend(ctx, "paulh", "the sky is BADSTRING today");
        WritePatterns(Pattern("p1", "*", "BADSTRING"));

        var state = new MonitorState { LastReportScanUtc = Now.AddSeconds(-1) };   // not first run
        var findings = await new ReportErrorWatcher().RunAsync(Ctx(db, state), CancellationToken.None);

        var f = Assert.Single(findings);
        Assert.Equal(ReportErrorWatcher.WatcherId, f.WatcherId);
        Assert.Null(f.Cooldown);                        // JSONL findings are not rate-limited
        Assert.NotNull(f.Fields);
        Assert.Equal(id.ToString(), f.Fields!["reportId"]);
        Assert.Equal("p1", f.Fields["patternId"]);
        Assert.Equal("paulh", f.Fields["recipient"]);
        Assert.Contains("BADSTRING", f.Fields["snippet"]);
        Assert.Equal("catches p1", f.Fields["description"]);   // operator description is in the record
        Assert.Equal(Now, state.LastReportScanUtc);
    }

    [Fact]
    public async Task Benign_NoMatch_NoFinding_WatermarkStillAdvances()
    {
        var db = NewDb();
        int id;
        using (var ctx = new WeatherDataContext(db))
            id = SeedSend(ctx, "paulh", "a perfectly fine forecast");
        WritePatterns(Pattern("p1", "*", "BADSTRING"));

        var state = new MonitorState { LastReportScanUtc = Now.AddSeconds(-1) };
        var findings = await new ReportErrorWatcher().RunAsync(Ctx(db, state), CancellationToken.None);

        Assert.Empty(findings);
        Assert.Equal(Now, state.LastReportScanUtc);       // advanced so it won't be re-scanned
    }

    [Fact]
    public async Task SecondCycle_OverSameSends_ReLogsNothing()
    {
        var db = NewDb();
        int id;
        using (var ctx = new WeatherDataContext(db))
            id = SeedSend(ctx, "paulh", "BADSTRING");
        WritePatterns(Pattern("p1", "*", "BADSTRING"));

        var state = new MonitorState { LastReportScanUtc = Now.AddSeconds(-1) };
        var watcher = new ReportErrorWatcher();
        Assert.Single(await watcher.RunAsync(Ctx(db, state), CancellationToken.None));
        Assert.Empty(await watcher.RunAsync(Ctx(db, state), CancellationToken.None));  // watermark past it now
    }

    // ── live language attribution ────────────────────────────────────────────

    [Fact]
    public async Task LanguageAttribution_AppliesRecipientLanguagePatterns_NotOtherLanguages()
    {
        var db = NewDb();
        int id;
        using (var ctx = new WeatherDataContext(db))
        {
            var esId = SeedLanguage(ctx, "es");
            SeedRecipient(ctx, "paulh", esId);
            id = SeedSend(ctx, "paulh", "el cielo MALO hoy");
        }
        // An es-scoped pattern that matches, and a de-scoped pattern that also would match the text —
        // only the es one should fire for an es recipient.
        WritePatterns(
            Pattern("es-bad", "es", "MALO"),
            Pattern("de-bad", "de", "MALO"));

        var state = new MonitorState { LastReportScanUtc = Now.AddSeconds(-1) };
        var findings = await new ReportErrorWatcher().RunAsync(Ctx(db, state), CancellationToken.None);

        var f = Assert.Single(findings);
        Assert.Equal("es-bad", f.Fields!["patternId"]);
        Assert.Equal("es", f.Fields["language"]);
    }

    [Fact]
    public async Task Esperanto_DiacriticsMatchCorrectly()
    {
        var db = NewDb();
        int id;
        using (var ctx = new WeatherDataContext(db))
        {
            var eoId = SeedLanguage(ctx, "eo");
            SeedRecipient(ctx, "paulh", eoId);
            id = SeedSend(ctx, "paulh", "la ĉielo estas malĝusta");   // contains ĉ / ĝ
        }
        WritePatterns(Pattern("eo-bad", "eo", "malĝusta"));

        var state = new MonitorState { LastReportScanUtc = Now.AddSeconds(-1) };
        var findings = await new ReportErrorWatcher().RunAsync(Ctx(db, state), CancellationToken.None);

        Assert.Single(findings);
    }

    // ── malformed patterns degrade gracefully ────────────────────────────────

    [Fact]
    public async Task InvalidAndDuplicatePatterns_SkippedGracefully_ValidStillMatches()
    {
        var db = NewDb();
        using (var ctx = new WeatherDataContext(db))
            SeedSend(ctx, "paulh", "the sky is BADSTRING");
        // p1 valid; p2 has an invalid regex (unbalanced paren); p1 duplicated with a different regex.
        WritePatterns(
            Pattern("p1", "*", "BADSTRING"),
            Pattern("p2", "*", "("),
            Pattern("p1", "*", "OTHER"));

        var state = new MonitorState { LastReportScanUtc = Now.AddSeconds(-1) };
        var findings = await new ReportErrorWatcher().RunAsync(Ctx(db, state), CancellationToken.None);

        // Invalid regex and the duplicate id are dropped at load; the one valid p1 still fires once.
        var f = Assert.Single(findings);
        Assert.Equal("p1", f.Fields!["patternId"]);
    }

    // ── §13 end-to-end: a full cycle writes a matching findings.jsonl line ─────

    [Fact]
    public async Task FullCycle_WritesMatchingFindingToJsonlFile()
    {
        var db = NewDb();
        int id;
        using (var ctx = new WeatherDataContext(db))
            id = SeedSend(ctx, "paulh", "the sky is BADSTRING");
        WritePatterns(Pattern("p1", "*", "BADSTRING"));

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InstallRoot"] = _installRoot,
            ["Monitor:AlertEmail"] = "alerts@example.com",
            ["Monitor:MetarStalenessThresholdMinutes"] = "0",
        }).Build();

        var state = new InMemoryStateStore(new MonitorState { LastReportScanUtc = Now.AddSeconds(-1) });
        var worker = new MonitorWorker(config, db, _ => new NoopEmailer(), state, () => Now);

        await worker.RunCycleAsync(CancellationToken.None);

        var findingsFile = Path.Combine(_installRoot, "Logs", "findings.jsonl");
        Assert.True(File.Exists(findingsFile), "findings.jsonl should have been written");
        var line = Assert.Single(File.ReadAllLines(findingsFile));
        Assert.Contains("\"patternId\":\"p1\"", line);
        Assert.Contains($"\"reportId\":\"{id}\"", line);
        Assert.Contains("\"watcher\":\"report-errors\"", line);
    }

    private sealed class NoopEmailer : IEmailer
    {
        public Task<bool> SendAsync(string toAddress, string subject, string plainBody,
            string? htmlBody = null, IReadOnlyDictionary<string, string>? inlineImages = null,
            string? toName = null, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class InMemoryStateStore(MonitorState seed) : IMonitorStateStore
    {
        public MonitorState State { get; private set; } = seed;
        public MonitorState Load() => State;
        public void Save(MonitorState state) => State = state;
    }
}