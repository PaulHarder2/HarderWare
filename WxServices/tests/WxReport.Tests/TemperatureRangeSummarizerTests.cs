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
        // kept RAW (no rounding here — the renderer rounds once per unit, matching the grid). Each
        // day carries an afternoon (local-12:00) block so its high is a genuine daytime high.
        var body = new ForecastSnapshotBody
        {
            Blocks =
            [
                Blk(6, 20, 30), Blk(12, 22, 34.4),   // day 1: morning + afternoon → hi 34.4, lo 20
                Blk(30, 18, 26), Blk(36, 19, 28),    // day 2: morning + afternoon → hi 28, lo 18
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
                Blk(6, 20, 30), Blk(12, 22, 33),      // today — morning + afternoon → kept, hi 33
            ],
        };
        var (highs, _) = TemperatureRangeSummarizer.DailyHighsLows(body, Now, TimeZoneInfo.Utc);
        Assert.Equal(new[] { 33.0 }, highs);
    }

    [Fact]
    public void DailyHighsLows_BucketsByLocalDay_NotUtcDay()
    {
        // A fixed +6h zone (no DST, ICU-independent): two blocks on the SAME UTC day land on two
        // different LOCAL days — and both at a low-capable band (morning 06:00 / pre-dawn 00:00),
        // so each yields a low. Under UTC they'd collapse to one day (min 10).
        var tz = TimeZoneInfo.CreateCustomTimeZone("t+6", TimeSpan.FromHours(6), "t+6", "t+6");
        var body = new ForecastSnapshotBody
        {
            Blocks =
            [
                Blk(0, 10, 20),    // UTC 00:00 → local 06:00 (morning, local day D)
                Blk(18, 15, 30),   // UTC 18:00 → local 00:00 (pre-dawn, local day D+1) — same UTC day
            ],
        };
        var (_, lows) = TemperatureRangeSummarizer.DailyHighsLows(body, Now, tz);
        Assert.Equal(new[] { 10.0, 15.0 }, lows);
    }

    [Fact]
    public void DailyHighsLows_DayWithNoOvernightBlock_ExcludedFromLows_KeptForHighs()
    {
        // The mirror of the partial-trailing-day case: a leading day (today, sent past the morning)
        // whose earliest block is the afternoon has only daytime minima — not an overnight low — so
        // it must not feed the lows series (WX-230). It still has an afternoon, so its high is real.
        var body = new ForecastSnapshotBody
        {
            Blocks =
            [
                Blk(12, 25, 37),  // 06-02 12:00 local — afternoon (peak) → real high 37, no overnight
                Blk(18, 24, 33),  // 06-02 18:00 local — evening
                Blk(24, 20, 28),  // 06-03 00:00 local — pre-dawn
                Blk(30, 19, 26),  // 06-03 06:00 local — morning
                Blk(36, 22, 35),  // 06-03 12:00 local — afternoon
            ],
        };
        var (highs, lows) = TemperatureRangeSummarizer.DailyHighsLows(body, Now, TimeZoneInfo.Utc);
        Assert.Equal(new[] { 37.0, 35.0 }, highs);   // both days have an afternoon
        Assert.Equal(new[] { 19.0 }, lows);          // day 1's daytime min (24) excluded — no overnight block
    }

    // ── WX-230: a partial trailing day (no afternoon block) must not feed the highs ──

    [Fact]
    public void DailyHighsLows_PartialTrailingDay_ExcludedFromHighs_KeptForLows()
    {
        // The last local day has only a pre-dawn block (the GFS horizon ends before its
        // afternoon). Its max is a pre-dawn temperature, NOT a daytime high — so it must not
        // feed the highs series (WX-230); its min is still a valid low.
        var body = new ForecastSnapshotBody
        {
            Blocks =
            [
                Blk(6, 24, 30),   // 06-02 06:00 local — morning
                Blk(12, 28, 37),  // 06-02 12:00 local — afternoon (peak) → real high 37
                Blk(18, 26, 33),  // 06-02 18:00 local — evening
                Blk(24, 26, 28),  // 06-03 00:00 local — pre-dawn ONLY (no afternoon)
            ],
        };
        var (highs, lows) = TemperatureRangeSummarizer.DailyHighsLows(body, Now, TimeZoneInfo.Utc);
        Assert.Equal(new[] { 37.0 }, highs);        // only the day with an afternoon block
        Assert.Equal(new[] { 24.0, 26.0 }, lows);   // both days contribute a low
    }

    [Fact]
    public void Characterize_SteadyHeatWithPartialPreDawnLastDay_NoPhantomCooldown()
    {
        // The WX-230 case: five full ~37 °C days, then a pre-dawn-only trailing day whose 28 °C
        // max is not a daytime high. Pre-fix the highs were [37,37,37,37,37,28] → a phantom
        // two-period "cooldown"; now the partial day is excluded and the highs stay one band.
        var blocks = new List<ForecastSnapshotBlock>();
        for (int d = 0; d < 5; d++)
        {
            blocks.Add(Blk(d * 24 + 6, 26, 32));    // morning
            blocks.Add(Blk(d * 24 + 12, 28, 37));   // afternoon (peak) → high 37
            blocks.Add(Blk(d * 24 + 18, 27, 34));   // evening
        }
        blocks.Add(Blk(5 * 24, 26, 28));            // day 6: pre-dawn block only

        var (highs, _) = TemperatureRangeSummarizer.DailyHighsLows(
            new ForecastSnapshotBody { Blocks = blocks }, Now, TimeZoneInfo.Utc);
        var periods = TemperatureRangeSummarizer.Characterize(highs);

        Assert.Equal(5, highs.Count);               // the partial day dropped out of the highs
        var one = Assert.Single(periods);           // one band — no phantom cooldown
        Assert.Equal((36, 38), ((int)one.LowC, (int)one.HighC));
    }
}