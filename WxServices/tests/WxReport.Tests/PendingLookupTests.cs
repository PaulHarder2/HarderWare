using MetarParser.Data;
using MetarParser.Data.Entities;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

// WX-527: which unsent rows make up the locality's pending report. The newest reconciled snapshot
// among the members' weather rows is the only candidate; its unsent rows, written since WX-527,
// are the recipients still owed — all of them after a total failure, some after a partial one.
public sealed class PendingLookupTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly DbContextOptions<WeatherDataContext> _db;

    private static readonly DateTime Now = new(2026, 9, 6, 15, 0, 0, DateTimeKind.Utc);
    private static readonly List<string> Members = ["paul_en", "alex_en", "steph_en"];
    private const string Report = "{}";   // any structured report: only its presence matters here
    private const string Evidence = "M:KAUS;W1;V1;S2;T16;P|T:none|G:none";

    public PendingLookupTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _db = new DbContextOptionsBuilder<WeatherDataContext>().UseSqlite(_conn).Options;
        using var ctx = new WeatherDataContext(_db);
        ctx.Database.ExecuteSqlRaw(ctx.Database.GenerateCreateScript().Replace("nvarchar(max)", "TEXT"));
    }

    public void Dispose() => _conn.Dispose();

    private ForecastSnapshot Snapshot(WeatherDataContext ctx, double minutesAgo)
    {
        var s = new ForecastSnapshot { StationIcao = "KAUS", GeneratedAtUtc = Now.AddMinutes(-minutesAgo), Body = "{}" };
        ctx.ForecastSnapshots.Add(s);
        return s;
    }

    private static CommittedSend Row(
        ForecastSnapshot s, string recipient, bool sent = false, string? report = Report,
        bool diagnostic = false, string? kind = "Scheduled", string? evidence = Evidence) => new()
        {
            ForecastSnapshot = s,
            RecipientId = recipient,
            StructuredReport = report,
            EmailBody = "<html/>",
            CreatedAtUtc = s.GeneratedAtUtc,
            SentAtUtc = sent ? s.GeneratedAtUtc.AddMinutes(1) : null,
            IsDiagnostic = diagnostic,
            ReportKind = kind,
            InputIdentity = evidence,
        };

    private async Task<(ForecastSnapshot Snapshot, List<CommittedSend> Owed, bool FirstDelivery)?> FindAsync(DateTime? now = null)
    {
        await using var ctx = new WeatherDataContext(_db);
        return await ReportWorker.FindPendingAsync(ctx, Members, now ?? Now, CancellationToken.None);
    }

    private void Seed(Action<WeatherDataContext> seed)
    {
        using var ctx = new WeatherDataContext(_db);
        seed(ctx);
        ctx.SaveChanges();
    }

    [Fact]
    public async Task ATotalFailure_OwesEveryRecipient()
    {
        Seed(ctx =>
        {
            var s = Snapshot(ctx, 30);
            ctx.CommittedSends.AddRange(Row(s, "paul_en"), Row(s, "alex_en"), Row(s, "steph_en"));
        });
        var pending = await FindAsync();
        Assert.NotNull(pending);
        Assert.Equal(["paul_en", "alex_en", "steph_en"], pending.Value.Owed.Select(r => r.RecipientId));
        Assert.True(pending.Value.FirstDelivery);
    }

    [Fact]
    public async Task APartialDelivery_OwesOnlyTheRecipientsMissed()
    {
        Seed(ctx =>
        {
            var s = Snapshot(ctx, 30);
            ctx.CommittedSends.AddRange(Row(s, "paul_en", sent: true), Row(s, "alex_en"), Row(s, "steph_en"));
        });
        var pending = (await FindAsync())!.Value;
        Assert.Equal(["alex_en", "steph_en"], pending.Owed.Select(r => r.RecipientId));
        Assert.False(pending.FirstDelivery);
    }

    [Fact]
    public async Task AFullyDeliveredReport_IsNotPending()
    {
        Seed(ctx =>
        {
            var s = Snapshot(ctx, 30);
            ctx.CommittedSends.AddRange(Row(s, "paul_en", sent: true), Row(s, "alex_en", sent: true));
        });
        Assert.Null(await FindAsync());
    }

    [Fact]
    public async Task AnOlderUnsentReport_SupersededByADeliveredOne_IsNotPending()
    {
        Seed(ctx =>
        {
            var old = Snapshot(ctx, 90);
            ctx.CommittedSends.Add(Row(old, "paul_en"));
            var newer = Snapshot(ctx, 20);
            ctx.CommittedSends.Add(Row(newer, "paul_en", sent: true));
        });
        Assert.Null(await FindAsync());
    }

    [Fact]
    public async Task WelcomeAndDiagnosticRows_AreNotPending()
    {
        Seed(ctx =>
        {
            var s = Snapshot(ctx, 30);
            ctx.CommittedSends.AddRange(Row(s, "paul_en", report: null, kind: null), Row(s, "alex_en", diagnostic: true));
        });
        Assert.Null(await FindAsync());
    }

    [Fact]
    public async Task ANewerWelcome_DoesNotHideAPendingReport()
    {
        Seed(ctx =>
        {
            var s = Snapshot(ctx, 30);
            ctx.CommittedSends.Add(Row(s, "paul_en"));
            var w = Snapshot(ctx, 10);
            ctx.CommittedSends.Add(Row(w, "alex_en", sent: true, report: null, kind: null, evidence: null));
        });
        Assert.Equal(["paul_en"], (await FindAsync())!.Value.Owed.Select(r => r.RecipientId));
    }

    [Fact]
    public async Task ANewerHazardAlert_SupersedesAPendingReport()
    {
        // A degraded hazard report delivered after the failed one must not be followed by the older report.
        Seed(ctx =>
        {
            var s = Snapshot(ctx, 60);
            ctx.CommittedSends.Add(Row(s, "paul_en"));
            var hazard = Snapshot(ctx, 10);
            ctx.CommittedSends.Add(Row(hazard, "paul_en", sent: true, report: null, kind: "Unscheduled"));
        });
        Assert.Null(await FindAsync());
    }

    [Fact]
    public async Task AnUnsentHazardAlert_IsNotPending() =>
        // It stores no structured report; the next cycle reconciles it.
        await AssertNullAfter(ctx => ctx.CommittedSends.Add(Row(Snapshot(ctx, 30), "paul_en", report: null, kind: "Unscheduled")));

    private async Task AssertNullAfter(Action<WeatherDataContext> seed)
    {
        Seed(seed);
        Assert.Null(await FindAsync());
    }

    [Fact]
    public async Task TwoUnsentRowsForOneRecipient_AreOwedOnce()
    {
        Seed(ctx =>
        {
            var s = Snapshot(ctx, 30);
            ctx.CommittedSends.AddRange(Row(s, "paul_en"), Row(s, "paul_en"));
        });
        Assert.Equal(["paul_en"], (await FindAsync())!.Value.Owed.Select(r => r.RecipientId));
    }

    [Fact]
    public async Task ARecipientAlreadyServedByAnotherRow_IsNotOwed()
    {
        // A WX-182 cached re-send can write a second row for the same snapshot and deliver it.
        Seed(ctx =>
        {
            var s = Snapshot(ctx, 30);
            ctx.CommittedSends.AddRange(Row(s, "paul_en"), Row(s, "paul_en", sent: true), Row(s, "alex_en"));
        });
        Assert.Equal(["alex_en"], (await FindAsync())!.Value.Owed.Select(r => r.RecipientId));
    }

    [Fact]
    public async Task RowsWrittenBeforeWx527_AreNotPending()
    {
        Seed(ctx =>
        {
            var s = Snapshot(ctx, 30);
            ctx.CommittedSends.AddRange(Row(s, "paul_en", kind: null), Row(s, "alex_en", evidence: null));
        });
        Assert.Null(await FindAsync());
    }

    [Fact]
    public async Task AReportThreeHoursOld_IsNotPending()
    {
        Seed(ctx =>
        {
            var s = Snapshot(ctx, 30);
            ctx.CommittedSends.Add(Row(s, "paul_en"));
        });
        Assert.NotNull(await FindAsync(Now.AddMinutes(-30).AddHours(3).AddMinutes(-1)));
        Assert.Null(await FindAsync(Now.AddMinutes(-30).AddHours(3)));
    }

    [Fact]
    public async Task ANonMembersRows_AreNotOwed()
    {
        Seed(ctx =>
        {
            var s = Snapshot(ctx, 30);
            ctx.CommittedSends.AddRange(Row(s, "someone_else"), Row(s, "paul_en"));
        });
        Assert.Equal(["paul_en"], (await FindAsync())!.Value.Owed.Select(r => r.RecipientId));
    }
}