using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using MetarParser.Data;
using MetarParser.Data.Configuration;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Xunit;

namespace MetarParser.Tests;

/// <summary>
/// The shared write path into the <c>Config</c> table (WX-315). These live here, with the code,
/// rather than in a consumer's test project: <see cref="ConfigStore"/> now has two callers (the
/// setup console and WxManager's Configure tab), so its coverage must not hang off either one.
/// Runs against in-memory SQLite — the real EF mechanics, no SQL Server.
/// </summary>
public class ConfigStoreTests
{
    private static readonly DateTime Seeded = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);

    private static readonly KeyValuePair<string, string?>[] Rows =
    {
        new("Smtp:Host", "smtp.gmail.com"),
        new("Monitor:AlertEmail", "ops@example.com"),
    };

    private static DbContextOptions<WeatherDataContext> NewDb(SqliteConnection conn)
    {
        conn.Open();
        var options = new DbContextOptionsBuilder<WeatherDataContext>().UseSqlite(conn).Options;
        using var ctx = new WeatherDataContext(options);
        ctx.Database.ExecuteSqlRaw(ctx.Database.GenerateCreateScript().Replace("nvarchar(max)", "TEXT"));
        return options;
    }

    [Fact]
    public async Task Upsert_InsertsEveryRowIntoAnEmptyTable()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = NewDb(conn);

        using (var ctx = new WeatherDataContext(options))
        {
            var outcome = await ConfigStore.UpsertAsync(ctx, Rows, Seeded);
            Assert.Equal(2, outcome.Inserted);
        }

        using (var ctx = new WeatherDataContext(options))
        {
            var stored = await ctx.Config.OrderBy(c => c.Key).ToListAsync();
            Assert.Equal(new[] { "Monitor:AlertEmail", "Smtp:Host" }, stored.Select(c => c.Key));
            Assert.Equal(Seeded, stored[0].UpdatedUtc);
        }
    }

    /// <summary>An unchanged value must not bump UpdatedUtc — it records the last real write.</summary>
    [Fact]
    public async Task Upsert_UnchangedValue_LeavesRowAndTimestampAlone()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = NewDb(conn);

        using (var ctx = new WeatherDataContext(options))
            await ConfigStore.UpsertAsync(ctx, Rows, Seeded);

        using (var ctx = new WeatherDataContext(options))
        {
            var outcome = await ConfigStore.UpsertAsync(ctx, Rows, Later);
            Assert.Equal(0, outcome.Inserted);
            Assert.Equal(0, outcome.Updated);
            Assert.Equal(2, outcome.Unchanged);
        }

        using (var ctx = new WeatherDataContext(options))
            Assert.Equal(Seeded, (await ctx.Config.SingleAsync(c => c.Key == "Smtp:Host")).UpdatedUtc);
    }

    [Fact]
    public async Task Upsert_ChangedValue_UpdatesValueAndTimestamp()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = NewDb(conn);

        using (var ctx = new WeatherDataContext(options))
            await ConfigStore.UpsertAsync(ctx, Rows, Seeded);

        using (var ctx = new WeatherDataContext(options))
        {
            var outcome = await ConfigStore.UpsertAsync(
                ctx, new KeyValuePair<string, string?>[] { new("Smtp:Host", "smtp.example.com") }, Later);
            Assert.Equal(1, outcome.Updated);
        }

        using (var ctx = new WeatherDataContext(options))
        {
            var row = await ctx.Config.SingleAsync(c => c.Key == "Smtp:Host");
            Assert.Equal("smtp.example.com", row.Value);
            Assert.Equal(Later, row.UpdatedUtc);
        }
    }

    /// <summary>
    /// The provider ignores bootstrap-critical keys on read, so writing one would produce a row
    /// that looks configured and has no effect.
    /// </summary>
    [Theory]
    [InlineData("ConnectionStrings:WeatherData")]
    [InlineData("Database:StartupRetry:MaxAttempts")]
    [InlineData("Telemetry:OtlpEndpoint")]
    [InlineData("Claude:TimeoutSeconds")]
    public async Task Upsert_RefusesBootstrapCriticalKeys(string key)
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = NewDb(conn);
        using var ctx = new WeatherDataContext(options);

        var ex = await Assert.ThrowsAsync<ConfigWriteException>(() => ConfigStore.UpsertAsync(
            ctx, new KeyValuePair<string, string?>[] { new(key, "x") }, Seeded));

        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
        Assert.Empty(await ctx.Config.ToListAsync());
    }

    /// <summary>A sibling of the exact-matched Claude timeout key stays writable (WX-313 precedent).</summary>
    [Fact]
    public async Task Upsert_AllowsClaudeSiblingsOfTheExactMatchedTimeoutKey()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = NewDb(conn);
        using var ctx = new WeatherDataContext(options);

        var outcome = await ConfigStore.UpsertAsync(
            ctx, new KeyValuePair<string, string?>[] { new("Claude:Model", "claude-sonnet-4-6") }, Seeded);

        Assert.Equal(1, outcome.Inserted);
    }

    /// <summary>
    /// A key repeated in one batch would take the insert path twice and violate the primary key at
    /// SaveChanges — after other work in the same transaction had already been staged.
    /// </summary>
    [Fact]
    public async Task Upsert_RefusesDuplicateKeysWithinOneBatch()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = NewDb(conn);
        using var ctx = new WeatherDataContext(options);

        var ex = await Assert.ThrowsAsync<ConfigWriteException>(() => ConfigStore.UpsertAsync(
            ctx,
            new KeyValuePair<string, string?>[]
            {
                new("Smtp:Host", "a"),
                new("smtp:host", "b"),   // same key, different case
            },
            Seeded));

        Assert.Contains("Duplicate", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await ctx.Config.ToListAsync());
    }
}