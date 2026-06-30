using MetarParser.Data;
using MetarParser.Data.Entities;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using WxReport.Svc.TranslationQa;

using Xunit;

namespace WxReport.Tests;

/// <summary>
/// WX-235 — the claim / sweep / complete DB logic behind QaRerunWorker, exercised against a real
/// relational store (SQLite in-memory) because the atomic claim uses <c>ExecuteUpdate</c>, which the
/// EF in-memory provider does not support. Mirrors the MetarParser.Tests harness (remap nvarchar(max)
/// → TEXT so EnsureCreated-equivalent DDL parses; keep the connection open for the DB's lifetime).
/// </summary>
public class QaRerunStoreTests
{
    private static readonly DateTime T0 = new(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc);

    private static DbContextOptions<WeatherDataContext> NewDb(SqliteConnection conn)
    {
        var options = new DbContextOptionsBuilder<WeatherDataContext>().UseSqlite(conn).Options;
        using var ctx = new WeatherDataContext(options);
        var script = ctx.Database.GenerateCreateScript().Replace("nvarchar(max)", "TEXT");
        ctx.Database.ExecuteSqlRaw(script);
        return options;
    }

    private static QaRerunRequest Running(string iso, DateTime requestedAt, DateTime? startedAt = null) => new()
    {
        IsoCode = iso,
        Status = QaRerunStatus.Running,
        RequestedAtUtc = requestedAt,
        StartedAtUtc = startedAt,
    };

    [Fact]
    public async Task TryClaimNext_ClaimsOldestUnclaimed_AndStampsStartedAt()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = NewDb(conn);
        await using (var ctx = new WeatherDataContext(db))
        {
            ctx.QaRerunRequests.AddRange(Running("de", T0.AddMinutes(2)), Running("es", T0)); // es is older
            await ctx.SaveChangesAsync();
        }

        QaRerunStore.ClaimedRun? claim;
        await using (var ctx = new WeatherDataContext(db))
            claim = await QaRerunStore.TryClaimNextAsync(ctx, T0.AddMinutes(5), default);

        Assert.NotNull(claim);
        Assert.Equal("es", claim!.IsoCode); // oldest RequestedAtUtc first
        await using (var ctx = new WeatherDataContext(db))
        {
            var es = await ctx.QaRerunRequests.SingleAsync(r => r.IsoCode == "es");
            Assert.Equal(T0.AddMinutes(5), es.StartedAtUtc); // claimed
            Assert.Equal(QaRerunStatus.Running, es.Status);
            var de = await ctx.QaRerunRequests.SingleAsync(r => r.IsoCode == "de");
            Assert.Null(de.StartedAtUtc); // the younger row is left unclaimed
        }
    }

    [Fact]
    public async Task TryClaimNext_SecondClaim_ReturnsNull_OnePickupOnly()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = NewDb(conn);
        await using (var ctx = new WeatherDataContext(db))
        {
            ctx.QaRerunRequests.Add(Running("de", T0));
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = new WeatherDataContext(db))
            Assert.NotNull(await QaRerunStore.TryClaimNextAsync(ctx, T0.AddMinutes(1), default));
        await using (var ctx = new WeatherDataContext(db))
            Assert.Null(await QaRerunStore.TryClaimNextAsync(ctx, T0.AddMinutes(2), default)); // already claimed
    }

    [Fact]
    public async Task TryClaimNext_NoUnclaimedRunning_ReturnsNull()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = NewDb(conn);
        await using (var ctx = new WeatherDataContext(db))
        {
            ctx.QaRerunRequests.Add(Running("de", T0, startedAt: T0)); // already claimed
            ctx.QaRerunRequests.Add(new QaRerunRequest { IsoCode = "es", Status = QaRerunStatus.Succeeded, RequestedAtUtc = T0 });
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = new WeatherDataContext(db))
            Assert.Null(await QaRerunStore.TryClaimNextAsync(ctx, T0.AddMinutes(1), default));
    }

    [Fact]
    public async Task SweepStuck_FailsStaleClaimedRun_LeavesFreshAndUnclaimed()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = NewDb(conn);
        await using (var ctx = new WeatherDataContext(db))
        {
            ctx.QaRerunRequests.AddRange(
                Running("old", T0, startedAt: T0),                  // claimed long ago → stale
                Running("fresh", T0, startedAt: T0.AddMinutes(9)),  // claimed recently → not stale
                Running("queued", T0, startedAt: null));            // never claimed → leave for the worker
            await ctx.SaveChangesAsync();
        }

        int n;
        await using (var ctx = new WeatherDataContext(db))
            n = await QaRerunStore.SweepStuckAsync(ctx, cutoff: T0.AddMinutes(5), nowUtc: T0.AddMinutes(10), reason: "interrupted", ct: default);

        Assert.Equal(1, n);
        await using (var read = new WeatherDataContext(db))
        {
            var old = await read.QaRerunRequests.SingleAsync(r => r.IsoCode == "old");
            Assert.Equal(QaRerunStatus.Failed, old.Status);
            Assert.Equal("interrupted", old.Error);
            Assert.Equal(T0.AddMinutes(10), old.CompletedAtUtc);
            Assert.Equal(QaRerunStatus.Running, (await read.QaRerunRequests.SingleAsync(r => r.IsoCode == "fresh")).Status);
            Assert.Equal(QaRerunStatus.Running, (await read.QaRerunRequests.SingleAsync(r => r.IsoCode == "queued")).Status);
        }
    }

    [Fact]
    public async Task Complete_SetsTerminalFields()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = NewDb(conn);
        long id;
        await using (var ctx = new WeatherDataContext(db))
        {
            var row = Running("de", T0, startedAt: T0);
            ctx.QaRerunRequests.Add(row);
            await ctx.SaveChangesAsync();
            id = row.Id;
        }

        await using (var ctx = new WeatherDataContext(db))
            await QaRerunStore.CompleteAsync(ctx, id, claimedAtUtc: T0, QaRerunStatus.Succeeded, "20260630-120000", error: null, nowUtc: T0.AddMinutes(1), ct: default);

        await using (var read = new WeatherDataContext(db))
        {
            var row = await read.QaRerunRequests.SingleAsync(r => r.Id == id);
            Assert.Equal(QaRerunStatus.Succeeded, row.Status);
            Assert.Equal("20260630-120000", row.ResultStamp);
            Assert.Null(row.Error);
            Assert.Equal(T0.AddMinutes(1), row.CompletedAtUtc);
        }
    }

    [Fact]
    public async Task Complete_StaleClaim_DoesNotClobberRequeuedRow()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = NewDb(conn);
        long id;
        await using (var ctx = new WeatherDataContext(db))
        {
            var row = Running("de", T0, startedAt: T0); // claimed at T0
            ctx.QaRerunRequests.Add(row);
            await ctx.SaveChangesAsync();
            id = row.Id;
        }

        // The operator re-queued the same language while the old run was still in flight: the unique-IsoCode
        // row is reset to a fresh unclaimed Running (StartedAtUtc back to null, new RequestedAtUtc).
        await using (var ctx = new WeatherDataContext(db))
        {
            var row = await ctx.QaRerunRequests.SingleAsync(r => r.Id == id);
            row.StartedAtUtc = null;
            row.RequestedAtUtc = T0.AddMinutes(3);
            await ctx.SaveChangesAsync();
        }

        // The OLD run finishes and tries to complete with its now-stale claim time.
        await using (var ctx = new WeatherDataContext(db))
            await QaRerunStore.CompleteAsync(ctx, id, claimedAtUtc: T0, QaRerunStatus.Succeeded, "oldstamp", error: null, nowUtc: T0.AddMinutes(5), ct: default);

        // The re-queued row must remain an unclaimed Running — not clobbered to Succeeded by the stale run.
        await using (var read = new WeatherDataContext(db))
        {
            var row = await read.QaRerunRequests.SingleAsync(r => r.Id == id);
            Assert.Equal(QaRerunStatus.Running, row.Status);
            Assert.Null(row.StartedAtUtc);
            Assert.Null(row.ResultStamp);
            Assert.Null(row.CompletedAtUtc);
        }
    }

    [Fact]
    public async Task ReleaseClaim_ReturnsRowToUnclaimedRunning_SoItReruns()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = NewDb(conn);
        long id;
        await using (var ctx = new WeatherDataContext(db))
        {
            var row = Running("de", T0, startedAt: T0); // claimed at T0
            ctx.QaRerunRequests.Add(row);
            await ctx.SaveChangesAsync();
            id = row.Id;
        }

        await using (var ctx = new WeatherDataContext(db))
            await QaRerunStore.ReleaseClaimAsync(ctx, id, claimedAtUtc: T0, ct: default);

        // Back to an unclaimed Running row — the next poll will re-claim and re-run it.
        await using (var read = new WeatherDataContext(db))
        {
            var row = await read.QaRerunRequests.SingleAsync(r => r.Id == id);
            Assert.Equal(QaRerunStatus.Running, row.Status);
            Assert.Null(row.StartedAtUtc);
            Assert.Null(row.CompletedAtUtc);
        }
    }
}