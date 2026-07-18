using System;
using System.Collections.Generic;

using MetarParser.Data;
using MetarParser.Data.Configuration;
using MetarParser.Data.Entities;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

using Xunit;

namespace MetarParser.Tests;

/// <summary>
/// WX-313: the DB-backed configuration provider overlays the <c>Config</c> table
/// onto <see cref="IConfiguration"/>.  These exercise the pieces that can only be
/// proven against a real database: rows land at their <c>Section:SubKey</c> slot
/// (case-insensitively), the provider is resilient when the table is missing or no
/// connection is available (the fresh-DB bootstrap case), a reload picks up a table
/// that appeared after the first load (what the host does after
/// <c>EnsureSchemaAsync</c>), and — added last — the database wins over the file
/// layers.  Backed by SQLite in-memory so it runs on CI without SQL Server; the
/// production model pins <c>Value</c> to <c>nvarchar(max)</c>, which SQLite's DDL
/// parser rejects, so the schema is built from the generated script with that type
/// remapped to <c>TEXT</c> (same approach as the METAR write tests).
/// </summary>
public class DbConfigurationProviderTests
{
    /// <summary>Opens <paramref name="conn"/> and creates the full schema (Value remapped to TEXT for SQLite).</summary>
    private static DbContextOptions<WeatherDataContext> NewDbWithSchema(SqliteConnection conn)
    {
        conn.Open();
        var options = new DbContextOptionsBuilder<WeatherDataContext>().UseSqlite(conn).Options;
        using var ctx = new WeatherDataContext(options);
        var script = ctx.Database.GenerateCreateScript().Replace("nvarchar(max)", "TEXT");
        ctx.Database.ExecuteSqlRaw(script);
        return options;
    }

    private static void Seed(DbContextOptions<WeatherDataContext> options, params (string Key, string? Value)[] rows)
    {
        using var ctx = new WeatherDataContext(options);
        foreach (var (key, value) in rows)
            ctx.Config.Add(new Config { Key = key, Value = value, UpdatedUtc = DateTime.UtcNow });
        ctx.SaveChanges();
    }

    [Fact]
    public void Load_PopulatesConfiguration_FromConfigRows_CaseInsensitively()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = NewDbWithSchema(conn);
        Seed(options, ("Fetch:HomeIcao", "KAUS"), ("Smtp:Host", "smtp.example.com"), ("Present:ButNull", null));

        var provider = new DbConfigurationProvider(() => new WeatherDataContext(options));
        provider.Load();

        Assert.True(provider.TryGet("Fetch:HomeIcao", out var icao));
        Assert.Equal("KAUS", icao);

        // Configuration keys are case-insensitive; the provider must match that.
        Assert.True(provider.TryGet("fetch:homeicao", out var icaoLower));
        Assert.Equal("KAUS", icaoLower);

        // A present row with a null value is kept as a present-but-null entry.
        Assert.True(provider.TryGet("Present:ButNull", out var nullVal));
        Assert.Null(nullVal);
    }

    [Fact]
    public void Load_MissingConfigTable_ContributesNothing_AndDoesNotThrow()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        // Deliberately do NOT create the schema — the Config table does not exist,
        // as on a fresh DB before EnsureSchemaAsync has run.
        var options = new DbContextOptionsBuilder<WeatherDataContext>().UseSqlite(conn).Options;

        var provider = new DbConfigurationProvider(() => new WeatherDataContext(options));

        var ex = Record.Exception(() => provider.Load());
        Assert.Null(ex);
        Assert.False(provider.TryGet("Fetch:HomeIcao", out _));
    }

    [Fact]
    public void Load_NullContextFactory_ContributesNothing()
    {
        // A null/blank connection string produces a null factory (see AddDatabaseConfig).
        var provider = new DbConfigurationProvider(null);

        provider.Load();

        Assert.False(provider.TryGet("anything", out _));
    }

    [Fact]
    public void Reload_AfterTableAppears_OverlaysValues()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<WeatherDataContext>().UseSqlite(conn).Options;

        var provider = new DbConfigurationProvider(() => new WeatherDataContext(options));

        // First load models a fresh DB: the Config table does not exist yet, so the
        // provider overlays nothing and does not throw.
        provider.Load();
        Assert.False(provider.TryGet("Fetch:HomeIcao", out _));

        // The schema appears (as EnsureSchemaAsync would create it) and a value is seeded.
        using (var ctx = new WeatherDataContext(options))
        {
            var script = ctx.Database.GenerateCreateScript().Replace("nvarchar(max)", "TEXT");
            ctx.Database.ExecuteSqlRaw(script);
        }
        Seed(options, ("Fetch:HomeIcao", "KAUS"));

        // Reload — what the host does immediately after EnsureSchemaAsync — now populates.
        provider.Load();
        Assert.True(provider.TryGet("Fetch:HomeIcao", out var icao));
        Assert.Equal("KAUS", icao);
    }

    [Fact]
    public void Load_IgnoresConnectionStringKeys_BootstrapGuard()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = NewDbWithSchema(conn);
        // A stray ConnectionStrings row must never be overlaid — the provider needs
        // the connection string to reach the very DB it would read the override from.
        Seed(options, ("ConnectionStrings:WeatherData", "Server=stray;"), ("Smtp:Host", "db-host"));

        var provider = new DbConfigurationProvider(() => new WeatherDataContext(options));
        provider.Load();

        Assert.False(provider.TryGet("ConnectionStrings:WeatherData", out _));   // guarded out
        Assert.True(provider.TryGet("Smtp:Host", out var host));                 // other keys still load
        Assert.Equal("db-host", host);
    }

    [Fact]
    public void DatabaseSource_AddedLast_WinsOverEarlierProviders()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = NewDbWithSchema(conn);
        Seed(options, ("Smtp:Host", "db-host"));

        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Smtp:Host"] = "file-host" })
            .Add(new DbConfigurationSource(() => new WeatherDataContext(options)))   // added LAST → wins
            .Build();

        Assert.Equal("db-host", config["Smtp:Host"]);
    }
}