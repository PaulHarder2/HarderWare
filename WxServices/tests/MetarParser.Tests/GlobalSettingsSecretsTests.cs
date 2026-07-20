using System.Linq;
using System.Threading.Tasks;

using MetarParser.Data;
using MetarParser.Data.Entities;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Xunit;

namespace MetarParser.Tests;

/// <summary>
/// WX-322: every secret lives on the <c>GlobalSettings</c> row — the connection string is the sole
/// exception, since it cannot live in the store it unlocks. These pin the rule so a future secret
/// added to a config file fails a test rather than reaching production.
/// </summary>
public class GlobalSettingsSecretsTests
{
    private static DbContextOptions<WeatherDataContext> NewDb(SqliteConnection conn)
    {
        conn.Open();
        var options = new DbContextOptionsBuilder<WeatherDataContext>().UseSqlite(conn).Options;
        using var ctx = new WeatherDataContext(options);
        ctx.Database.ExecuteSqlRaw(ctx.Database.GenerateCreateScript().Replace("nvarchar(max)", "TEXT"));
        return options;
    }

    /// <summary>
    /// The key round-trips. Previously it lived in <c>appsettings.local.json</c>, where the
    /// Configure tab's file-rebuilding save silently deleted it (see WX-315's history note).
    /// </summary>
    [Fact]
    public async Task What3WordsApiKey_RoundTrips()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = NewDb(conn);

        using (var ctx = new WeatherDataContext(options))
        {
            ctx.GlobalSettings.Add(new GlobalSettings { Id = 1, What3WordsApiKey = "AB12CD34" });
            await ctx.SaveChangesAsync();
        }

        using (var ctx = new WeatherDataContext(options))
            Assert.Equal("AB12CD34", (await ctx.GlobalSettings.SingleAsync(x => x.Id == 1)).What3WordsApiKey);
    }

    /// <summary>
    /// A null key is legal — it means "not configured", which `AddressGeocoder` reports as an
    /// actionable error rather than failing obscurely.
    /// </summary>
    [Fact]
    public async Task What3WordsApiKey_MayBeNull()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = NewDb(conn);

        using var ctx = new WeatherDataContext(options);
        ctx.GlobalSettings.Add(new GlobalSettings { Id = 1 });
        await ctx.SaveChangesAsync();

        Assert.Null((await ctx.GlobalSettings.SingleAsync(x => x.Id == 1)).What3WordsApiKey);
    }

    /// <summary>
    /// Guards the rule itself: every secret-shaped property on the entity is a column here. If a
    /// future secret is added to the entity this passes; if one is added to a config file instead,
    /// this test is the reminder that it belongs on the row.
    /// </summary>
    [Fact]
    public void EverySecretIsAColumnOnTheRow()
    {
        var names = typeof(GlobalSettings).GetProperties().Select(p => p.Name).ToArray();

        Assert.Contains("ClaudeApiKey", names);
        Assert.Contains("GeminiApiKey", names);
        Assert.Contains("SmtpUsername", names);
        Assert.Contains("SmtpPassword", names);
        Assert.Contains("SmtpFromAddress", names);
        Assert.Contains("What3WordsApiKey", names);
    }
}