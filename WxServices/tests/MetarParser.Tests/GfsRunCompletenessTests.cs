using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using MetarParser.Data;
using MetarParser.Data.Entities;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Xunit;

namespace MetarParser.Tests;

/// <summary>
/// WX-451: a GFS model run must be marked <see cref="GfsModelRun.IsComplete"/> only when every
/// forecast hour in <c>0..maxForecastHours</c> is stored — decided by <b>set membership</b>, never
/// by a count of distinct hours.
///
/// The previous test was <c>storedHourCount &gt;= expectedHours</c>, under which any hour stored
/// <em>outside</em> the expected range substituted one-for-one for a missing hour inside it. That
/// is unreachable while <c>MaxForecastHours</c> stays at 120 (the fetch loop cannot insert out of
/// range), and becomes reachable the moment the value is reduced or the horizon is extended —
/// which is WX-452.
///
/// The decision rules (<see cref="GfsFetcher.ComputeMissingHours"/>,
/// <see cref="GfsFetcher.DescribeMissingHours"/>) are pure and tested directly; the marking path
/// (<see cref="GfsFetcher.EvaluateRunCompletenessAsync"/>) is tested against a real SQLite
/// in-memory database, because a pure test of the arithmetic stays green against a wiring bug in
/// which nothing consults it. Production is almost never short an hour, so only a test can prove
/// any of this.
/// </summary>
public class GfsRunCompletenessTests
{
    private static readonly DateTime Run = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);

    private static GfsGridPoint Point(int forecastHour, float lat = 30.0f, float lon = -95.0f)
        => new() { ModelRunUtc = Run, ForecastHour = forecastHour, Lat = lat, Lon = lon };

    // ── ComputeMissingHours (pure) ───────────────────────────────────────────

    [Fact]
    public void ComputeMissingHours_AllPresent_ReturnsEmpty()
    {
        var stored = Enumerable.Range(0, 121).ToHashSet();
        Assert.Empty(GfsFetcher.ComputeMissingHours(stored, 120));
    }

    [Fact]
    public void ComputeMissingHours_OneAbsent_ReturnsThatHour()
    {
        var stored = Enumerable.Range(0, 121).Where(h => h != 57).ToHashSet();
        Assert.Equal([57], GfsFetcher.ComputeMissingHours(stored, 120));
    }

    /// <summary>
    /// THE DEFECT. The stored set has exactly the expected cardinality (121) — so the old
    /// <c>count &gt;= expected</c> test passed — while f113 is absent and f200 is present in its
    /// place. Set membership must still report f113 missing.
    /// </summary>
    [Fact]
    public void ComputeMissingHours_OutOfRangeHourDoesNotSubstituteForAMissingOne()
    {
        var stored = Enumerable.Range(0, 121).Where(h => h != 113).Append(200).ToHashSet();

        Assert.Equal(121, stored.Count);                                   // the count is satisfied
        Assert.Equal([113], GfsFetcher.ComputeMissingHours(stored, 120));  // the set is not
    }

    [Fact]
    public void ComputeMissingHours_NothingStored_ReturnsEveryExpectedHour()
    {
        var missing = GfsFetcher.ComputeMissingHours(new HashSet<int>(), 120);
        Assert.Equal(121, missing.Count);
        Assert.Equal(0, missing[0]);
        Assert.Equal(120, missing[^1]);
    }

    [Fact]
    public void ComputeMissingHours_IgnoresHoursAboveTheBound()
    {
        // A horizon reduction leaves hours above the new bound in place; they are neither
        // missing nor a substitute — simply out of scope.
        var stored = Enumerable.Range(0, 121).Concat(Enumerable.Range(121, 48)).ToHashSet();
        Assert.Empty(GfsFetcher.ComputeMissingHours(stored, 120));
    }

    // ── DescribeMissingHours (pure) ──────────────────────────────────────────

    [Fact]
    public void DescribeMissingHours_CollapsesConsecutiveHoursIntoRanges()
        => Assert.Equal("f113-f114", GfsFetcher.DescribeMissingHours([113, 114]));

    [Fact]
    public void DescribeMissingHours_RendersSinglesAndRangesTogether_Sorted()
        => Assert.Equal("f005, f113-f115", GfsFetcher.DescribeMissingHours([114, 5, 113, 115]));

    [Fact]
    public void DescribeMissingHours_Empty_ReturnsNone()
        => Assert.Equal("none", GfsFetcher.DescribeMissingHours([]));

    [Fact]
    public void DescribeMissingHours_CapsOutput_SoAnUnfetchedRunCannotFloodTheLog()
    {
        // Ten isolated hours => ten ranges; the cap is 8.
        var missing = Enumerable.Range(0, 10).Select(i => i * 2).ToList();
        var text = GfsFetcher.DescribeMissingHours(missing);

        Assert.EndsWith("+2 more range(s)", text);
        Assert.DoesNotContain("f016", text);
    }

    // ── EvaluateRunCompletenessAsync (SQLite-backed — the WIRING) ────────────

    private static async Task SeedAsync(DbContextOptions<WeatherDataContext> options, IEnumerable<int> hours)
    {
        using var ctx = new WeatherDataContext(options);
        ctx.GfsModelRuns.Add(new GfsModelRun { ModelRunUtc = Run, IsComplete = false });
        ctx.GfsGrid.AddRange(hours.Select(h => Point(h)));
        await ctx.SaveChangesAsync();
    }

    private static async Task<bool> IsCompleteAsync(DbContextOptions<WeatherDataContext> options)
    {
        using var ctx = new WeatherDataContext(options);
        var run = await ctx.GfsModelRuns.SingleAsync(r => r.ModelRunUtc == Run);
        return run.IsComplete;
    }

    [Fact]
    public async Task Evaluate_EveryHourStored_MarksRunComplete()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = SqliteTestDb.New(conn);
        await SeedAsync(options, Enumerable.Range(0, 121));

        var missing = await GfsFetcher.EvaluateRunCompletenessAsync(options, Run, 120, CancellationToken.None);

        Assert.NotNull(missing);
        Assert.Empty(missing);
        Assert.True(await IsCompleteAsync(options));
    }

    /// <summary>
    /// THE REGRESSION GUARD. 121 distinct hours are stored — satisfying the old count test — but
    /// f113 is absent and f200 stands in its place. The run must NOT be marked complete.
    /// Reverting <see cref="GfsFetcher.EvaluateRunCompletenessAsync"/> to the count comparison
    /// turns this red.
    /// </summary>
    [Fact]
    public async Task Evaluate_CountSatisfiedByAnOutOfRangeHour_DoesNotMarkComplete()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = SqliteTestDb.New(conn);
        await SeedAsync(options, Enumerable.Range(0, 121).Where(h => h != 113).Append(200));

        var missing = await GfsFetcher.EvaluateRunCompletenessAsync(options, Run, 120, CancellationToken.None);

        Assert.Equal([113], missing);
        Assert.False(await IsCompleteAsync(options));
    }

    [Fact]
    public async Task Evaluate_HourMissing_LeavesRunIncomplete()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = SqliteTestDb.New(conn);
        await SeedAsync(options, Enumerable.Range(0, 121).Where(h => h != 120));

        var missing = await GfsFetcher.EvaluateRunCompletenessAsync(options, Run, 120, CancellationToken.None);

        Assert.Equal([120], missing);
        Assert.False(await IsCompleteAsync(options));
    }

    /// <summary>
    /// The misconfiguration guard: a negative bound means completeness can never be evaluated, so
    /// it must not silently mark the run complete (which <c>0 &gt;= 0</c> would have done). It
    /// returns null and logs at ERROR, which WxMonitor's log scanner escalates to an email.
    /// </summary>
    [Fact]
    public async Task Evaluate_NegativeMaxForecastHours_EvaluatesNothingAndLeavesRunAlone()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = SqliteTestDb.New(conn);
        await SeedAsync(options, []);

        var missing = await GfsFetcher.EvaluateRunCompletenessAsync(options, Run, -1, CancellationToken.None);

        Assert.Null(missing);
        Assert.False(await IsCompleteAsync(options));
    }

    [Fact]
    public async Task Evaluate_AlreadyComplete_IsNotRewritten()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = SqliteTestDb.New(conn);
        await SeedAsync(options, Enumerable.Range(0, 121));

        using (var ctx = new WeatherDataContext(options))
        {
            (await ctx.GfsModelRuns.SingleAsync(r => r.ModelRunUtc == Run)).IsComplete = true;
            await ctx.SaveChangesAsync();
        }

        var missing = await GfsFetcher.EvaluateRunCompletenessAsync(options, Run, 120, CancellationToken.None);

        Assert.Empty(missing!);
        Assert.True(await IsCompleteAsync(options));
    }
}