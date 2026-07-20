using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using MetarParser.Data;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using WxServices.Setup;

using Xunit;

namespace WxServices.Setup.Tests;

/// <summary>
/// WX-314 AC-6, narrowed in WX-315: the seeding path is now a thin adapter over the shared
/// <c>MetarParser.Data.Configuration.ConfigStore</c>, so the upsert semantics and the
/// bootstrap-key / duplicate-key refusals are covered where that code lives
/// (<c>MetarParser.Tests.ConfigStoreTests</c>). What remains this ticket's own is the contract the
/// setup console depends on: the foundational rows land, and a refusal arrives as a
/// <see cref="SetupException"/> so Program.cs prints a plain message instead of a stack trace.
/// </summary>
public class ConfigSeederTests
{
    private static readonly DateTime Seeded = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);

    private static DbContextOptions<WeatherDataContext> NewDb(SqliteConnection conn)
    {
        conn.Open();
        var options = new DbContextOptionsBuilder<WeatherDataContext>().UseSqlite(conn).Options;
        using var ctx = new WeatherDataContext(options);
        ctx.Database.ExecuteSqlRaw(ctx.Database.GenerateCreateScript().Replace("nvarchar(max)", "TEXT"));
        return options;
    }

    [Fact]
    public async Task UpsertAsync_SeedsTheFoundationalRows_AndReportsTheOutcome()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = NewDb(conn);
        using var ctx = new WeatherDataContext(options);

        var rows = ConfigSeed.BuildFoundationalSeedRows(new FoundationalInputs(
            "KDFW", 32.9, -97.0, 2.5, 25, 40, -105, -90, "conus"));

        var outcome = await ConfigSeeder.UpsertAsync(ctx, rows, Seeded);

        Assert.Equal(9, outcome.Inserted);
        Assert.Equal(0, outcome.Updated);
        Assert.Equal(0, outcome.Unchanged);
        Assert.Equal(9, await ctx.Config.CountAsync());
    }

    /// <summary>Re-running setup with the same answers must be a no-op (AC-4 idempotency, DB side).</summary>
    [Fact]
    public async Task UpsertAsync_SecondRunWithSameValues_ReportsAllUnchanged()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = NewDb(conn);
        var rows = ConfigSeed.BuildFoundationalSeedRows(new FoundationalInputs(
            "KDFW", 32.9, -97.0, 2.5, 25, 40, -105, -90, "conus"));

        using (var ctx = new WeatherDataContext(options))
            await ConfigSeeder.UpsertAsync(ctx, rows, Seeded);

        using (var ctx = new WeatherDataContext(options))
        {
            var outcome = await ConfigSeeder.UpsertAsync(ctx, rows, Seeded.AddDays(1));
            Assert.Equal(0, outcome.Inserted);
            Assert.Equal(0, outcome.Updated);
            Assert.Equal(9, outcome.Unchanged);
        }
    }

    /// <summary>
    /// The adapter's whole reason to exist: a ConfigWriteException from the shared store must reach
    /// the operator as a SetupException, which Program.cs prints as a plain actionable message.
    /// </summary>
    [Fact]
    public async Task UpsertAsync_TranslatesARefusalIntoSetupException()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = NewDb(conn);
        using var ctx = new WeatherDataContext(options);

        var ex = await Assert.ThrowsAsync<SetupException>(() => ConfigSeeder.UpsertAsync(
            ctx,
            new KeyValuePair<string, string?>[] { new("ConnectionStrings:WeatherData", "x") },
            Seeded));

        Assert.Contains("ConnectionStrings:WeatherData", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await ctx.Config.ToListAsync());
    }
}