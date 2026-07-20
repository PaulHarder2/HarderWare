using System.Linq;
using System.Threading.Tasks;

using MetarParser.Data;
using MetarParser.Data.Entities;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Xunit;

namespace MetarParser.Tests;

/// <summary>
/// WX-322: the secret columns on the <c>GlobalSettings</c> row — round-trip, null handling, and
/// schema stability.
/// <para>
/// These cover the <em>entity</em> only. They do NOT detect a secret introduced in a config file:
/// nothing here reads JSON, so a new file-based credential would pass every assertion. The rule
/// that secrets belong on this row is stated in <c>DESIGN.md</c> §4.7 and enforced by review, not
/// by this suite.
/// </para>
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
    /// Schema stability for the secret columns: each one exists on the entity, so removing or
    /// renaming one is a deliberate, test-breaking act rather than a silent drop.
    ///
    /// <para>Deliberately NOT claimed: this cannot detect a <em>new</em> secret introduced in a
    /// config file. It knows nothing about JSON, and asserting the presence of six names can only
    /// fail when one is removed. An earlier version of this comment said it would catch that case,
    /// which was untrue — the sort of over-claim that makes a green suite misleading. Enforcing the
    /// rule against config files would mean scanning the JSON for secret-shaped key names, which is
    /// a different test and is not written yet.</para>
    /// </summary>
    [Fact]
    public void SecretColumnsExistOnTheRow()
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