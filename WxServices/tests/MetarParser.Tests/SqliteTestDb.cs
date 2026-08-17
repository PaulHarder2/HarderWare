using MetarParser.Data;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MetarParser.Tests;

/// <summary>
/// Builds a real SQLite in-memory <see cref="WeatherDataContext"/> for tests that need genuine
/// EF mechanics — unique indexes, FK cascades, in-place updates — rather than a mocked store.
/// </summary>
/// <remarks>
/// Extracted from <c>MetarFetcherDedupTests.NewDb</c> (WX-210) when WX-451 became its second
/// consumer, so the "(max)" workaround below has one home rather than two.
/// </remarks>
internal static class SqliteTestDb
{
    /// <summary>
    /// Opens <paramref name="conn"/> and creates the production schema on it.
    /// </summary>
    /// <remarks>
    /// The production model pins some columns to <c>nvarchar(max)</c> (SQL Server); SQLite's DDL
    /// parser rejects the "(max)" length, so <c>EnsureCreated()</c> throws. The schema is built
    /// from the generated create script with those columns remapped to SQLite's TEXT affinity —
    /// unique indexes and FK cascades are preserved.
    /// </remarks>
    /// <param name="conn">A SQLite connection; opened by this method and owned by the caller.</param>
    /// <returns>Options bound to <paramref name="conn"/>, with the schema already created.</returns>
    internal static DbContextOptions<WeatherDataContext> New(SqliteConnection conn)
    {
        conn.Open();
        var options = new DbContextOptionsBuilder<WeatherDataContext>().UseSqlite(conn).Options;
        using var ctx = new WeatherDataContext(options);

        var script = ctx.Database.GenerateCreateScript().Replace("nvarchar(max)", "TEXT");
        ctx.Database.ExecuteSqlRaw(script);
        return options;
    }
}