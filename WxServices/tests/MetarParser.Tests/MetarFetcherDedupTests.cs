using System;
using System.Collections.Generic;
using System.Linq;

using MetarParser.Data;
using MetarParser.Data.Entities;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Xunit;

namespace MetarParser.Tests;

/// <summary>
/// WX-210: the METAR insert path must (a) collapse same-key duplicates within a
/// single fetch response — AWC re-serves byte-identical lines for some stations
/// (KJXI ~3x/cycle), which previously violated UX_Metars_Station_Time_Type and
/// rolled back the whole batch — and (b) let a later-arriving correction (COR)
/// overwrite a stored observation in place, while never letting an uncorrected
/// observation clobber a stored correction.
///
/// The decision rules (<see cref="MetarFetcher.CollapseByKey"/>,
/// <see cref="MetarFetcher.Reconcile"/>) are pure and tested directly; the EF
/// write (<see cref="MetarFetcher.ApplyPlan"/>, <see cref="MetarFetcher.InsertPerRow"/>)
/// is tested against a real SQLite in-memory database so the in-place overwrite,
/// the PK/child-row handling, the safety guard, and the per-row recovery are all
/// exercised — production almost never issues a COR, so only a test can prove them.
/// </summary>
public class MetarFetcherDedupTests
{
    private static readonly DateTime T = new(2026, 6, 24, 13, 35, 0, DateTimeKind.Utc);

    private static MetarRecord Obs(
        string station, DateTime t, bool cor = false, string? raw = null, string type = "METAR")
        => new()
        {
            StationIcao = station,
            ObservationUtc = t,
            ReportType = type,
            IsCorrection = cor,
            RawReport = raw ?? $"{type} {station} {t:ddHHmm}Z{(cor ? " COR" : "")}",
        };

    // ── CollapseByKey (pure, within-response) ────────────────────────────────

    [Fact]
    public void CollapseByKey_IdenticalCopies_CollapseToOne()
    {
        // The KJXI case: AWC returns the same line three times.
        var raw = "METAR KJXI 241335Z 18005KT";
        var survivors = MetarFetcher.CollapseByKey(
            [Obs("KJXI", T, raw: raw), Obs("KJXI", T, raw: raw), Obs("KJXI", T, raw: raw)]);

        Assert.Single(survivors);
    }

    [Fact]
    public void CollapseByKey_CorBeatsNonCor_RegardlessOfFeedOrder()
    {
        // COR after the original …
        var a = MetarFetcher.CollapseByKey([Obs("KJXI", T, cor: false), Obs("KJXI", T, cor: true)]);
        Assert.Single(a);
        Assert.True(a[0].IsCorrection);

        // … and COR before the original.
        var b = MetarFetcher.CollapseByKey([Obs("KJXI", T, cor: true), Obs("KJXI", T, cor: false)]);
        Assert.Single(b);
        Assert.True(b[0].IsCorrection);
    }

    [Fact]
    public void CollapseByKey_DistinctKeys_AllKept()
    {
        var survivors = MetarFetcher.CollapseByKey(
            [Obs("KJXI", T), Obs("KIAH", T), Obs("KJXI", T.AddMinutes(20))]);

        Assert.Equal(3, survivors.Count);
    }

    [Fact]
    public void CollapseByKey_DuplicateForOneStation_DoesNotDropCoBatchedStation()
    {
        // One station triplicated alongside a normal station: both survive.
        var survivors = MetarFetcher.CollapseByKey(
            [Obs("KJXI", T), Obs("KJXI", T), Obs("KJXI", T), Obs("KIAH", T)]);

        Assert.Equal(2, survivors.Count);
        Assert.Contains(survivors, s => s.StationIcao == "KIAH");
    }

    // ── Reconcile (pure, vs the DB snapshot) ─────────────────────────────────

    private static Dictionary<(string, DateTime, string), MetarFetcher.PriorMetar> Stored(
        int id, bool cor, string raw)
        => new() { [("KJXI", T, "METAR")] = new MetarFetcher.PriorMetar(id, cor, raw) };

    [Fact]
    public void Reconcile_KeyNotInDb_Inserts()
    {
        var plan = MetarFetcher.Reconcile(
            [Obs("KJXI", T)],
            new Dictionary<(string, DateTime, string), MetarFetcher.PriorMetar>());

        Assert.Single(plan.Inserts);
        Assert.Empty(plan.Overwrites);
        Assert.Equal(0, plan.Skipped);
    }

    [Fact]
    public void Reconcile_IncomingCor_OverwritesStoredNonCor()
    {
        var incoming = Obs("KJXI", T, cor: true, raw: "METAR KJXI 241335Z 18012KT COR");
        var plan = MetarFetcher.Reconcile([incoming], Stored(5, cor: false, raw: "METAR KJXI 241335Z 18005KT"));

        Assert.Empty(plan.Inserts);
        var (id, corrected) = Assert.Single(plan.Overwrites);
        Assert.Equal(5, id);
        Assert.Same(incoming, corrected);
        Assert.Equal(0, plan.Skipped);
    }

    [Fact]
    public void Reconcile_StoredCor_NotOverwrittenByLateNonCor()
    {
        // The case Paul flagged: a plain observation must never displace a stored COR.
        var plan = MetarFetcher.Reconcile(
            [Obs("KJXI", T, cor: false)],
            Stored(7, cor: true, raw: "METAR KJXI 241335Z 18012KT COR"));

        Assert.Empty(plan.Inserts);
        Assert.Empty(plan.Overwrites);
        Assert.Equal(1, plan.Skipped);
    }

    [Fact]
    public void Reconcile_IdenticalNonCorReArrival_Skipped()
    {
        // Truly identical (same raw): a benign re-send -> skip.
        var raw = "METAR KJXI 241335Z 18005KT";
        var plan = MetarFetcher.Reconcile(
            [Obs("KJXI", T, cor: false, raw: raw)],
            Stored(9, cor: false, raw: raw));

        Assert.Empty(plan.Inserts);
        Assert.Empty(plan.Overwrites);
        Assert.Equal(1, plan.Skipped);
    }

    [Fact]
    public void Reconcile_DifferingNonCor_KeepsStoredAndSkips()
    {
        // Same key, neither a COR, different content: a conflict — keep the stored
        // row (no correction authorizes a replacement), never overwrite.
        var plan = MetarFetcher.Reconcile(
            [Obs("KJXI", T, cor: false, raw: "METAR KJXI 241335Z 18012KT")],
            Stored(9, cor: false, raw: "METAR KJXI 241335Z 18005KT"));

        Assert.Empty(plan.Inserts);
        Assert.Empty(plan.Overwrites);
        Assert.Equal(1, plan.Skipped);
    }

    [Fact]
    public void Reconcile_TwoDifferingCors_LaterArrivalOverwrites()
    {
        var incoming = Obs("KJXI", T, cor: true, raw: "METAR KJXI 241335Z 18012G25KT COR");
        var plan = MetarFetcher.Reconcile([incoming], Stored(11, cor: true, raw: "METAR KJXI 241335Z 18012KT COR"));

        var (id, _) = Assert.Single(plan.Overwrites);
        Assert.Equal(11, id);
        Assert.Equal(0, plan.Skipped);
    }

    [Fact]
    public void Reconcile_SameCorContentReArrival_Skipped()
    {
        var raw = "METAR KJXI 241335Z 18012KT COR";
        var plan = MetarFetcher.Reconcile([Obs("KJXI", T, cor: true, raw: raw)], Stored(13, cor: true, raw: raw));

        Assert.Empty(plan.Overwrites);
        Assert.Equal(1, plan.Skipped);
    }

    // ── ApplyPlan / InsertPerRow (SQLite-backed, EF mechanics) ───────────────

    private static DbContextOptions<WeatherDataContext> NewDb(SqliteConnection conn)
    {
        conn.Open();
        var options = new DbContextOptionsBuilder<WeatherDataContext>().UseSqlite(conn).Options;
        using var ctx = new WeatherDataContext(options);

        // The production model pins some columns to nvarchar(max) (SQL Server);
        // SQLite's DDL parser rejects the "(max)" length, so EnsureCreated() throws.
        // Build the schema from the generated script with those columns remapped to
        // SQLite's TEXT affinity — the unique index and FK cascades (what these
        // tests exercise) are preserved.
        var script = ctx.Database.GenerateCreateScript().Replace("nvarchar(max)", "TEXT");
        ctx.Database.ExecuteSqlRaw(script);
        return options;
    }

    [Fact]
    public void ApplyPlan_IncomingCor_OverwritesInPlace_PreservesPk_ReplacesChildren()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = NewDb(conn);

        int storedId;
        using (var ctx = new WeatherDataContext(options))
        {
            var seed = Obs("KJXI", T, cor: false, raw: "METAR KJXI 241335Z 18005KT");
            seed.WindSpeed = 5;
            seed.SkyConditions.Add(new MetarSkyCondition { Cover = "BKN", HeightFeet = 1800, SortOrder = 0 });
            seed.SkyConditions.Add(new MetarSkyCondition { Cover = "OVC", HeightFeet = 2500, SortOrder = 1 });
            seed.WeatherPhenomena.Add(new MetarWeatherPhenomenon { Intensity = "-", Precipitation = "RA", SortOrder = 0 });
            seed.WeatherPhenomena.Add(new MetarWeatherPhenomenon { Intensity = "", Obscuration = "BR", SortOrder = 1 });
            seed.RunwayVisualRanges.Add(new MetarRunwayVisualRange { Runway = "18", MeanMeters = 800 });
            ctx.Metars.Add(seed);
            ctx.SaveChanges();
            storedId = seed.Id;
        }

        var cor = Obs("KJXI", T, cor: true, raw: "METAR KJXI 241335Z 18012KT COR");
        cor.WindSpeed = 12;
        cor.SkyConditions.Add(new MetarSkyCondition { Cover = "SCT", HeightFeet = 3000, SortOrder = 0 });
        cor.WeatherPhenomena.Add(new MetarWeatherPhenomenon { Intensity = "+", Precipitation = "TSRA", SortOrder = 0 });
        cor.RunwayVisualRanges.Add(new MetarRunwayVisualRange { Runway = "18", MeanMeters = 1500 });
        cor.RunwayVisualRanges.Add(new MetarRunwayVisualRange { Runway = "36", MeanMeters = 1200 });

        var plan = new MetarFetcher.MetarReconcilePlan([], [(storedId, cor)], 0);
        using (var ctx = new WeatherDataContext(options))
        {
            MetarFetcher.ApplyPlan(ctx, plan);
            ctx.SaveChanges();
        }

        using (var ctx = new WeatherDataContext(options))
        {
            var row = Assert.Single(ctx.Metars
                .Include(m => m.SkyConditions)
                .Include(m => m.WeatherPhenomena)
                .Include(m => m.RunwayVisualRanges)
                .Where(m => m.StationIcao == "KJXI"));
            Assert.Equal(storedId, row.Id);          // PK + unique key preserved
            Assert.True(row.IsCorrection);           // promoted to a correction
            Assert.Equal(12, row.WindSpeed);         // scalar columns replaced
            // all three cascade child collections replaced (sky 2->1, weather 2->1, rvr 1->2)
            Assert.Single(row.SkyConditions);
            Assert.Equal("SCT", row.SkyConditions[0].Cover);
            Assert.Single(row.WeatherPhenomena);
            Assert.Equal("TSRA", row.WeatherPhenomena[0].Precipitation);
            Assert.Equal(2, row.RunwayVisualRanges.Count);
            // old child rows cascade-removed, none orphaned in any of the three tables
            Assert.Equal(1, ctx.SkyConditions.Count());
            Assert.Equal(1, ctx.WeatherPhenomena.Count());
            Assert.Equal(2, ctx.RunwayVisualRanges.Count());
        }
    }

    [Fact]
    public void ApplyPlan_RefusesToOverwriteStoredCorWithNonCor()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = NewDb(conn);

        int storedId;
        using (var ctx = new WeatherDataContext(options))
        {
            var seed = Obs("KJXI", T, cor: true, raw: "METAR KJXI 241335Z 18012KT COR");
            seed.WindSpeed = 12;
            seed.SkyConditions.Add(new MetarSkyCondition { Cover = "FEW", HeightFeet = 4000, SortOrder = 0 });
            ctx.Metars.Add(seed);
            ctx.SaveChanges();
            storedId = seed.Id;
        }

        // A deliberately bad plan (Reconcile would never build this) must be refused
        // at the point of mutation, leaving the stored correction AND its child rows intact —
        // the guard returns before the RemoveRange calls, so moving it would fail here.
        var nonCor = Obs("KJXI", T, cor: false);
        nonCor.WindSpeed = 99;
        nonCor.SkyConditions.Add(new MetarSkyCondition { Cover = "OVC", HeightFeet = 500, SortOrder = 0 });
        var badPlan = new MetarFetcher.MetarReconcilePlan([], [(storedId, nonCor)], 0);

        using (var ctx = new WeatherDataContext(options))
        {
            MetarFetcher.ApplyPlan(ctx, badPlan);
            ctx.SaveChanges();
        }

        using (var ctx = new WeatherDataContext(options))
        {
            var row = Assert.Single(ctx.Metars.Include(m => m.SkyConditions).Where(m => m.StationIcao == "KJXI"));
            Assert.True(row.IsCorrection);   // correction intact
            Assert.Equal(12, row.WindSpeed); // not clobbered by the non-correction's 99
            var sky = Assert.Single(row.SkyConditions);  // the correction's child rows survive the refusal
            Assert.Equal("FEW", sky.Cover);
            Assert.Equal(1, ctx.SkyConditions.Count());  // the bad plan's child was not inserted
        }
    }

    [Fact]
    public void InsertPerRow_DuplicateAmongBatch_GoodRowsStillLand()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = NewDb(conn);

        using (var ctx = new WeatherDataContext(options))
        {
            ctx.Metars.Add(Obs("KJXI", T));   // already stored -> a re-insert collides
            ctx.SaveChanges();
        }

        // A duplicate (KJXI@T) next to a genuinely new row (KMMM@T): the duplicate
        // fails on the unique index, the new row must still be inserted.
        var landed = MetarFetcher.InsertPerRow([Obs("KJXI", T), Obs("KMMM", T)], options);

        Assert.Equal(1, landed);
        using (var ctx = new WeatherDataContext(options))
        {
            Assert.Equal(1, ctx.Metars.Count(m => m.StationIcao == "KMMM"));
            Assert.Equal(1, ctx.Metars.Count(m => m.StationIcao == "KJXI")); // still just the original
        }
    }

    [Fact]
    public void ApplyOverwritesPerRow_AppliesCorrectionInIsolation()
    {
        // The mixed-batch fallback: corrections are re-applied one-per-context (not
        // discarded with the failed insert batch). One overwrite must land in place.
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = NewDb(conn);

        int storedId;
        using (var ctx = new WeatherDataContext(options))
        {
            var seed = Obs("KJXI", T, cor: false, raw: "METAR KJXI 241335Z 18005KT");
            seed.WindSpeed = 5;
            seed.SkyConditions.Add(new MetarSkyCondition { Cover = "BKN", HeightFeet = 1800, SortOrder = 0 });
            ctx.Metars.Add(seed);
            ctx.SaveChanges();
            storedId = seed.Id;
        }

        var cor = Obs("KJXI", T, cor: true, raw: "METAR KJXI 241335Z 18012KT COR");
        cor.WindSpeed = 12;
        cor.SkyConditions.Add(new MetarSkyCondition { Cover = "SCT", HeightFeet = 3000, SortOrder = 0 });

        var applied = MetarFetcher.ApplyOverwritesPerRow([(storedId, cor)], options);

        Assert.Equal(1, applied);
        using (var ctx = new WeatherDataContext(options))
        {
            var row = Assert.Single(ctx.Metars.Include(m => m.SkyConditions).Where(m => m.StationIcao == "KJXI"));
            Assert.Equal(storedId, row.Id);          // overwrite in place, PK preserved
            Assert.True(row.IsCorrection);
            Assert.Equal(12, row.WindSpeed);
            Assert.Single(row.SkyConditions);
            Assert.Equal("SCT", row.SkyConditions[0].Cover);
            Assert.Equal(1, ctx.SkyConditions.Count()); // no orphaned child rows
        }
    }
}