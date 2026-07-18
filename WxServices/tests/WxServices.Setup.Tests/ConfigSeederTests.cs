using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using MetarParser.Data;
using MetarParser.Data.Entities;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using WxServices.Setup;

using Xunit;

namespace WxServices.Setup.Tests;

/// <summary>
/// WX-314 AC-6, test-first: upserting the foundational rows into the <c>Config</c> table. Runs
/// against in-memory SQLite (the repo's existing EF harness) so the real EF mechanics are exercised
/// without a SQL Server.
/// </summary>
public class ConfigSeederTests
{
    private static readonly DateTime Seeded = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);

    private static readonly KeyValuePair<string, string?>[] Rows =
    {
        new("Fetch:HomeIcao", "KDFW"),
        new("WxVis:MapExtent", "conus"),
    };

    private static DbContextOptions<WeatherDataContext> NewDb(SqliteConnection conn)
    {
        conn.Open();
        var options = new DbContextOptionsBuilder<WeatherDataContext>().UseSqlite(conn).Options;
        using var ctx = new WeatherDataContext(options);
        // SQLite's DDL parser rejects nvarchar(max); remap to TEXT affinity (same as the
        // other EF tests in this repo).
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
            var outcome = await ConfigSeeder.UpsertAsync(ctx, Rows, Seeded);
            Assert.Equal(2, outcome.Inserted);
            Assert.Equal(0, outcome.Updated);
        }

        using (var ctx = new WeatherDataContext(options))
        {
            var stored = await ctx.Config.OrderBy(c => c.Key).ToListAsync();
            Assert.Equal(new[] { "Fetch:HomeIcao", "WxVis:MapExtent" }, stored.Select(c => c.Key));
            Assert.Equal("KDFW", stored[0].Value);
            Assert.Equal(Seeded, stored[0].UpdatedUtc);
        }
    }

    /// <summary>Re-running setup with the same answers must be a no-op (AC-4 idempotency, DB side).</summary>
    [Fact]
    public async Task Upsert_SecondRunWithSameValues_ChangesNothing()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = NewDb(conn);

        using (var ctx = new WeatherDataContext(options))
            await ConfigSeeder.UpsertAsync(ctx, Rows, Seeded);

        using (var ctx = new WeatherDataContext(options))
        {
            var outcome = await ConfigSeeder.UpsertAsync(ctx, Rows, Later);
            Assert.Equal(0, outcome.Inserted);
            Assert.Equal(0, outcome.Updated);
            Assert.Equal(2, outcome.Unchanged);
        }

        using (var ctx = new WeatherDataContext(options))
        {
            // An unchanged value must not bump UpdatedUtc — the timestamp records the last real
            // write, so a no-op re-run must leave the audit trail alone.
            var row = await ctx.Config.SingleAsync(c => c.Key == "Fetch:HomeIcao");
            Assert.Equal(Seeded, row.UpdatedUtc);
        }
    }

    [Fact]
    public async Task Upsert_ChangedValue_UpdatesValueAndTimestamp()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = NewDb(conn);

        using (var ctx = new WeatherDataContext(options))
            await ConfigSeeder.UpsertAsync(ctx, Rows, Seeded);

        using (var ctx = new WeatherDataContext(options))
        {
            var outcome = await ConfigSeeder.UpsertAsync(
                ctx, new KeyValuePair<string, string?>[] { new("Fetch:HomeIcao", "KOKC") }, Later);
            Assert.Equal(1, outcome.Updated);
        }

        using (var ctx = new WeatherDataContext(options))
        {
            var row = await ctx.Config.SingleAsync(c => c.Key == "Fetch:HomeIcao");
            Assert.Equal("KOKC", row.Value);
            Assert.Equal(Later, row.UpdatedUtc);
        }
    }

    /// <summary>
    /// Defence in depth: the seed rows are already free of bootstrap keys by construction, but the
    /// seeder refuses them outright so no future caller can write a key the provider would ignore
    /// — which would look configured while having no effect.
    /// </summary>
    [Theory]
    [InlineData("ConnectionStrings:WeatherData")]
    [InlineData("Telemetry:OtlpEndpoint")]
    [InlineData("Claude:TimeoutSeconds")]
    public async Task Upsert_RejectsBootstrapCriticalKeys(string key)
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = NewDb(conn);
        using var ctx = new WeatherDataContext(options);

        var ex = await Assert.ThrowsAsync<SetupException>(() => ConfigSeeder.UpsertAsync(
            ctx, new KeyValuePair<string, string?>[] { new(key, "x") }, Seeded));

        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
        Assert.Empty(await ctx.Config.ToListAsync());
    }

    /// <summary>A sibling of the exact-matched Claude timeout key stays seedable (WX-313 precedent).</summary>
    [Fact]
    public async Task Upsert_AllowsClaudeSiblingsOfTheExactMatchedTimeoutKey()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = NewDb(conn);
        using var ctx = new WeatherDataContext(options);

        var outcome = await ConfigSeeder.UpsertAsync(
            ctx, new KeyValuePair<string, string?>[] { new("Claude:Model", "claude-sonnet-4-6") }, Seeded);

        Assert.Equal(1, outcome.Inserted);
    }
}