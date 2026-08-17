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

    /// <summary>
    /// A second, earlier run. <c>Gfs:RetainModelRuns = 1</c> plus <c>PurgeOldRunsAsync</c>'s
    /// "never delete a run newer than the latest complete run" filter means GfsGrid routinely
    /// holds two runs at once, so this is the production shape rather than a contrivance.
    /// </summary>
    private static readonly DateTime OtherRun = new(2026, 8, 17, 6, 0, 0, DateTimeKind.Utc);

    private static GfsGridPoint Point(int forecastHour, DateTime? run = null, float lat = 30.0f, float lon = -95.0f)
        => new() { ModelRunUtc = run ?? Run, ForecastHour = forecastHour, Lat = lat, Lon = lon };

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

    // ── CountOutOfRangeHours (pure) ──────────────────────────────────────────

    [Fact]
    public void CountOutOfRangeHours_NormalOperation_IsZero()
        => Assert.Equal(0, GfsFetcher.CountOutOfRangeHours(Enumerable.Range(0, 121).ToHashSet(), 120));

    /// <summary>
    /// The horizon-REDUCTION case, and the reason this helper exists. A run fetched at 120 and
    /// then judged against a bound of 60 holds 60 surplus hours; without this count the log
    /// reports a tidy "61/61 hours stored" and the orphaned data is invisible — in exactly the
    /// scenario the ticket names as making the defect reachable.
    /// </summary>
    [Fact]
    public void CountOutOfRangeHours_AfterAHorizonReduction_CountsTheSurplus()
        => Assert.Equal(60, GfsFetcher.CountOutOfRangeHours(Enumerable.Range(0, 121).ToHashSet(), 60));

    [Fact]
    public void CountOutOfRangeHours_IgnoresMissingHours_CountsOnlySurplus()
    {
        // Short of f005 AND holding f200: missing and surplus are different questions.
        var stored = Enumerable.Range(0, 121).Where(h => h != 5).Append(200).ToHashSet();
        Assert.Equal(1, GfsFetcher.CountOutOfRangeHours(stored, 120));
        Assert.Equal([5], GfsFetcher.ComputeMissingHours(stored, 120));
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

    /// <summary>
    /// The docstring promises the input "need not be sorted or distinct". Sorted was already
    /// tested; distinct was not — the contract claimed something nothing checked. ClaudePx
    /// predicted duplicates would render degenerately as "f113, f113-f114"; that is refuted
    /// (the method calls <c>Distinct()</c>), but the untested-clause half of her point stands,
    /// and this closes it.
    /// </summary>
    [Fact]
    public void DescribeMissingHours_DuplicateInput_CollapsesRatherThanRepeating()
        => Assert.Equal("f113-f114", GfsFetcher.DescribeMissingHours([113, 113, 114, 114, 113]));

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

    private static async Task SeedRunAsync(
        DbContextOptions<WeatherDataContext> options, DateTime run, IEnumerable<int> hours, bool complete = false)
    {
        using var ctx = new WeatherDataContext(options);
        ctx.GfsModelRuns.Add(new GfsModelRun { ModelRunUtc = run, IsComplete = complete });
        ctx.GfsGrid.AddRange(hours.Select(h => Point(h, run)));
        await ctx.SaveChangesAsync();
    }

    private static Task SeedAsync(DbContextOptions<WeatherDataContext> options, IEnumerable<int> hours)
        => SeedRunAsync(options, Run, hours);

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

    /// <summary>
    /// THE RUN DIMENSION. Every other SQLite test seeds exactly one run, so the
    /// <c>ModelRunUtc</c> filter in the completeness read is a no-op in all of them and
    /// deleting it survives the whole suite — found by ClaudePx in sibling review and
    /// confirmed by mutation.
    /// <para>
    /// Here the run under test has stored <em>nothing</em>, while a different, complete run
    /// holds all 121 hours. Without the filter those hours are counted as this run's and a
    /// freshly registered run is marked complete with zero data — WX-451's own defect one
    /// level up, and worse than the bug being fixed. Not contrived: <c>RetainModelRuns = 1</c>
    /// plus the purge's latest-complete guard means two runs' grid rows routinely coexist.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Evaluate_HoursBelongToADifferentRun_DoesNotMarkComplete()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = SqliteTestDb.New(conn);
        await SeedRunAsync(options, OtherRun, Enumerable.Range(0, 121), complete: true);
        await SeedRunAsync(options, Run, []);

        var missing = await GfsFetcher.EvaluateRunCompletenessAsync(options, Run, 120, CancellationToken.None);

        Assert.Equal(121, missing!.Count);
        Assert.False(await IsCompleteAsync(options));
    }

    /// <summary>
    /// The same lens as the f200 test, applied to the run axis: this run is short exactly
    /// f113, and the neighbouring run supplies one. Set membership must be scoped to the run.
    /// </summary>
    [Fact]
    public async Task Evaluate_MissingHourSuppliedByANeighbouringRun_DoesNotMarkComplete()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = SqliteTestDb.New(conn);
        await SeedRunAsync(options, OtherRun, [113], complete: true);
        await SeedRunAsync(options, Run, Enumerable.Range(0, 121).Where(h => h != 113));

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

    /// <summary>
    /// The negative-bound ERROR is logged once per process, because a log line repeating every
    /// fetch cycle holds WxMonitor's per-service cooldown open and its suppressed findings are
    /// discarded rather than deferred (WX-453) — so a permanently-lit ERROR masks every other
    /// error from that service.
    /// <para>
    /// This pins that only the LOGGING was rate-limited and not the GUARD: the condition must be
    /// re-checked, and still refused, on every call. That is the part with consequences — a guard
    /// that quietly stopped refusing after the first call would mark runs complete with no data.
    /// </para>
    /// <para>
    /// ⚠️ It deliberately does NOT assert that the log was written exactly once. `Logger` is
    /// static with no capture seam, so no test can observe that; the once-per-process behaviour
    /// is a source literal documented at the call site. See the disposition recorded on WX-451.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Evaluate_NegativeMaxForecastHours_RefusesOnEveryCall_NotOnlyTheFirst()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = SqliteTestDb.New(conn);
        await SeedAsync(options, []);

        var first = await GfsFetcher.EvaluateRunCompletenessAsync(options, Run, -1, CancellationToken.None);
        var second = await GfsFetcher.EvaluateRunCompletenessAsync(options, Run, -1, CancellationToken.None);
        var third = await GfsFetcher.EvaluateRunCompletenessAsync(options, Run, -5, CancellationToken.None);

        Assert.Null(first);
        Assert.Null(second);
        Assert.Null(third);
        Assert.False(await IsCompleteAsync(options));
    }

    /// <summary>
    /// The out-of-range note exists so a horizon REDUCTION is visible in the log: both branches
    /// report in-range coverage, which can never exceed the expected total, so without it the
    /// presence of orphaned hours could not appear at all. Here the run is complete under a
    /// reduced bound while holding hours above it — and must still be marked complete, since
    /// out-of-range hours are surplus, not missing.
    /// </summary>
    [Fact]
    public async Task Evaluate_CompleteUnderAReducedBound_StillMarksComplete_WithSurplusHoursPresent()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        var options = SqliteTestDb.New(conn);
        await SeedAsync(options, Enumerable.Range(0, 121));

        // Bound reduced to 60; hours 61..120 are now surplus rather than expected.
        var missing = await GfsFetcher.EvaluateRunCompletenessAsync(options, Run, 60, CancellationToken.None);

        Assert.Empty(missing!);
        Assert.True(await IsCompleteAsync(options));
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