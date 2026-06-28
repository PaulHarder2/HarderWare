using MetarParser.Data.Entities;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

// WX-228: the deterministic temperature-range characterization that feeds the
// reconciler ready {q:temp_range:...} tokens — one or two contiguous °C ranges per
// series (highs, lows), split only when the two halves' midpoints separate by at least
// 3 °C; each range the exact RAW °C min–max (rounded only at render, to match the grid),
// a display-degenerate run widened to a whole-degree ±1 °C band.
public class TemperatureRangeSummarizerTests
{
    // Whole-degree period endpoints, for the assertions on integer-valued inputs.
    private static (int Lo, int Hi, int Start, int End) Whole(TemperatureRangeSummarizer.TempPeriod p) =>
        ((int)p.LowC, (int)p.HighC, p.StartDay, p.EndDay);

    // ── single range ──────────────────────────────────────────────────────────

    [Fact]
    public void Characterize_SmallSpread_OneRangeExactMinMax()
    {
        var one = Assert.Single(TemperatureRangeSummarizer.Characterize([36, 37, 36]));
        Assert.Equal((36, 37, 0, 2), Whole(one));
    }

    [Fact]
    public void Characterize_DeadFlat_WidensByOneDegree()
    {
        // Every day the same whole degree: present a small band, not a robotic point.
        var one = Assert.Single(TemperatureRangeSummarizer.Characterize([36, 36, 36]));
        Assert.Equal((35, 37, 0, 2), Whole(one));
    }

    [Fact]
    public void Characterize_FractionalDaysRoundingToSameWhole_WidensByOneDegree()
    {
        // Raw values differ but both round to 36 °C — display-degenerate, so widen to 35–37.
        var one = Assert.Single(TemperatureRangeSummarizer.Characterize([36.1, 36.4, 35.9]));
        Assert.Equal((35.0, 37.0), (one.LowC, one.HighC));
    }

    [Fact]
    public void Characterize_FractionalEndpoints_KeptRawForGridMatch()
    {
        // Non-degenerate: endpoints stay RAW (34.0, 36.4) so the renderer's per-unit rounding
        // matches the grid (rounding to whole °C first would diverge from the grid's raw→°F).
        var one = Assert.Single(TemperatureRangeSummarizer.Characterize([34.0, 36.4]));
        Assert.Equal((34.0, 36.4), (one.LowC, one.HighC));
    }

    [Fact]
    public void Characterize_BelowThreshold_StaysOneRange()
    {
        // The widest midpoint separation across all splits is < 3 °C — one range.
        var one = Assert.Single(TemperatureRangeSummarizer.Characterize([27, 28, 28, 29]));
        Assert.Equal((27, 29, 0, 3), Whole(one));
    }

    // ── two ranges ────────────────────────────────────────────────────────────

    [Fact]
    public void Characterize_Warming_SplitsIntoEarlyLater()
    {
        var p = TemperatureRangeSummarizer.Characterize([24, 25, 24, 28, 29, 28]);
        Assert.Equal(2, p.Count);
        Assert.Equal((24, 25, 0, 2), Whole(p[0]));
        Assert.Equal((28, 29, 3, 5), Whole(p[1]));
    }

    [Fact]
    public void Characterize_Cooling_SplitsIntoEarlyLater()
    {
        var p = TemperatureRangeSummarizer.Characterize([30, 31, 30, 25, 24, 25]);
        Assert.Equal(2, p.Count);
        Assert.Equal((30, 31, 0, 2), Whole(p[0]));
        Assert.Equal((24, 25, 3, 5), Whole(p[1]));
    }

    [Fact]
    public void Characterize_ExactlyThreeDegreeSeparation_Splits()
    {
        // Best split has midpoints 24 and 27 — exactly 3 °C apart — and splits (the boundary
        // is inclusive). The shape is non-monotonic so no end-day split separates them further.
        var p = TemperatureRangeSummarizer.Characterize([25, 23, 28, 26]);
        Assert.Equal(2, p.Count);
        Assert.Equal((23, 25, 0, 1), Whole(p[0]));
        Assert.Equal((26, 28, 2, 3), Whole(p[1]));
    }

    [Fact]
    public void Characterize_LinearRamp_TieBreaksToBalancedSplit()
    {
        // Every split separates the midpoints equally (a clean ramp); the tie-break picks
        // the most balanced cut (smallest larger-half span) — here the middle.
        var p = TemperatureRangeSummarizer.Characterize([20, 22, 24, 26, 28, 30]);
        Assert.Equal(2, p.Count);
        Assert.Equal((20, 24, 0, 2), Whole(p[0]));
        Assert.Equal((26, 30, 3, 5), Whole(p[1]));
    }

    // ── degenerate inputs ─────────────────────────────────────────────────────

    [Fact]
    public void Characterize_SingleDay_WidensToBand()
    {
        var one = Assert.Single(TemperatureRangeSummarizer.Characterize([30]));
        Assert.Equal((29, 31, 0, 0), Whole(one));
    }

    [Fact]
    public void Characterize_Empty_ReturnsEmpty() =>
        Assert.Empty(TemperatureRangeSummarizer.Characterize([]));

    [Fact]
    public void TempPeriod_Token_IsWellFormed() =>
        Assert.Equal("{q:temp_range:24:26}", new TemperatureRangeSummarizer.TempPeriod(24, 26, 0, 2).Token);

    [Fact]
    public void TempPeriod_Token_UsesInvariantDecimalSeparator() =>
        // A raw fractional endpoint serializes with a period, regardless of machine locale.
        Assert.Equal("{q:temp_range:34:36.4}", new TemperatureRangeSummarizer.TempPeriod(34.0, 36.4, 0, 1).Token);

    // ── prompt guidance ───────────────────────────────────────────────────────

    [Fact]
    public void BuildPromptGuidance_SinglePeriod_NamesTheRangeTokens()
    {
        var g = TemperatureRangeSummarizer.BuildPromptGuidance([36, 37, 36], [27, 28, 27]);
        Assert.Contains("daytime highs: {q:temp_range:36:37}", g);
        Assert.Contains("overnight lows: {q:temp_range:27:28}", g);
    }

    [Fact]
    public void BuildPromptGuidance_TwoPeriods_NamesBothTokensAndTrend()
    {
        var g = TemperatureRangeSummarizer.BuildPromptGuidance([24, 25, 24, 28, 29, 28], []);
        Assert.Contains("{q:temp_range:24:25}", g);
        Assert.Contains("{q:temp_range:28:29}", g);
        Assert.Contains("rising", g);
    }

    [Fact]
    public void BuildPromptGuidance_NoData_IsEmpty() =>
        Assert.Equal(string.Empty, TemperatureRangeSummarizer.BuildPromptGuidance([], []));

    // ── DailyHighsLows: mirrors the Extended Forecast grid's day aggregation ───

    private static readonly DateTime Now = new(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc);

    private static ForecastSnapshotBlock Blk(double hoursFromNow, double loC, double hiC) => new()
    {
        StartUtc = Now.AddHours(hoursFromNow),
        SkyState = SkyState.Clear,
        Obscuration = Obscuration.None,
        TemperatureCelsius = new(loC, hiC),
        WindKt = new(0, 5),
        PrecipExpectation = PrecipExpectation.None,
        PrecipPhenomenon = null,
        SevereFlag = false,
    };

    [Fact]
    public void DailyHighsLows_AggregatesPerLocalDayMaxMin_Raw()
    {
        // tz = UTC, so a local day equals a UTC day; high = max across the day's blocks, low = min,
        // kept RAW (no rounding here — the renderer rounds once per unit, matching the grid).
        var body = new ForecastSnapshotBody
        {
            Blocks =
            [
                Blk(0, 20, 30), Blk(6, 22, 34.4),   // day 1: hi 34.4, lo 20
                Blk(24, 18, 26), Blk(30, 19, 28),   // day 2: hi 28, lo 18
            ],
        };
        var (highs, lows) = TemperatureRangeSummarizer.DailyHighsLows(body, Now, TimeZoneInfo.Utc);
        Assert.Equal(new[] { 34.4, 28.0 }, highs);
        Assert.Equal(new[] { 20.0, 18.0 }, lows);
    }

    [Fact]
    public void DailyHighsLows_DropsWhollyElapsedDay()
    {
        // A day whose every block ended before nowUtc is off the grid (WX-188), so it is
        // not characterized either.
        var body = new ForecastSnapshotBody
        {
            Blocks =
            [
                Blk(-48, 10, 15), Blk(-42, 11, 16),   // two days ago — fully elapsed, dropped
                Blk(0, 20, 30), Blk(6, 22, 33),       // today — kept
            ],
        };
        var (highs, _) = TemperatureRangeSummarizer.DailyHighsLows(body, Now, TimeZoneInfo.Utc);
        Assert.Equal(new[] { 33.0 }, highs);
    }
}