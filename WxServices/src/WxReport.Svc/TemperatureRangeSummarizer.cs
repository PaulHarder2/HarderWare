using System.Globalization;
using System.Text;

using MetarParser.Data.Entities;

namespace WxReport.Svc;

/// <summary>
/// WX-228: turns a locality's per-day forecast temperatures into the one or two
/// whole-degree-Celsius ranges the report's "In summary" prose speaks in, so the
/// reconciler model never has to phrase a temperature band itself — the defect that
/// produced "the upper 97°F range", a fuzzy qualifier the model wrapped around a
/// single rendered point value because the token vocabulary offered no way to say a
/// span.
///
/// <para>
/// The series of daily highs (and, separately, lows) is characterized as either:
/// <list type="bullet">
/// <item>a single range — the exact °C min–max across the period; or</item>
/// <item>two contiguous sub-periods (e.g. an early/later warming), split at most
///   once, when some prefix|suffix partition separates the two halves' midpoints by
///   at least <see cref="SplitThresholdC"/>.</item>
/// </list>
/// </para>
///
/// <para>
/// Each range is emitted as a <c>{q:temp_range:lo:hi}</c> token (canonical °C); the
/// renderer converts each endpoint to the recipient's unit, so the prose stays
/// unit-neutral. The per-day high/low is the max/min °C across the day's blocks —
/// the same aggregation <see cref="StructuredReportRenderer"/> uses for the Extended
/// Forecast grid — so a day's high/low figure matches the grid's (WX-153 spine). The day
/// <em>set</em> can differ: WX-230 omits a partial day's non-extreme (a high with no
/// afternoon, a low with no morning) from the summary, while the grid still shows that cell.
/// </para>
/// </summary>
public static class TemperatureRangeSummarizer
{
    /// <summary>
    /// Minimum °C between the two halves' midpoints for a split to be worth
    /// describing as two periods. 3 °C ≈ 5 °F — about the smallest change in a daily
    /// high a reader registers, and the separation at which the two ranges stop
    /// overlapping (a single period spans ~2 °C), so below it the distinction is one
    /// the reader could not feel. Calibrated with Paul (WX-228).
    /// </summary>
    public const double SplitThresholdC = 3.0;

    private const double Epsilon = 1e-9;

    /// <summary>
    /// One characterized sub-period: an inclusive canonical-°C span
    /// [<see cref="LowC"/>, <see cref="HighC"/>] over the contiguous forecast days
    /// [<see cref="StartDay"/>, <see cref="EndDay"/>] (0-based indices into the input
    /// series).  Endpoints are the RAW °C extremes (not pre-rounded), so the renderer's
    /// single conversion matches the Extended Forecast grid in BOTH units — whole-°C and
    /// whole-°F alike (rounding raw °C first would diverge from the grid's raw→°F by up to
    /// 1 °F).  The lone exception is a dead-flat run, widened to whole-degree endpoints.
    /// </summary>
    public readonly record struct TempPeriod(double LowC, double HighC, int StartDay, int EndDay)
    {
        /// <summary>The unit-neutral renderer token for this range, e.g. <c>{q:temp_range:24:26}</c> (period decimal separator, like every quantity token).</summary>
        public string Token =>
            $"{{q:temp_range:{LowC.ToString(CultureInfo.InvariantCulture)}:{HighC.ToString(CultureInfo.InvariantCulture)}}}";
    }

    /// <summary>
    /// Characterizes a series of whole-°C daily values (highs OR lows, in
    /// chronological order) as one or two contiguous ranges per the rules above.
    /// Returns an empty list for empty input; never more than two periods.
    /// </summary>
    public static IReadOnlyList<TempPeriod> Characterize(IReadOnlyList<double> dailyC)
    {
        int n = dailyC.Count;
        if (n == 0)
            return [];
        if (n == 1)
            return [MakePeriod(dailyC, 0, 0)];

        // Scan every contiguous prefix|suffix split (n-1 of them). A split qualifies
        // when the two halves' midpoints are >= SplitThresholdC apart. Among
        // qualifiers prefer the widest separation (the most distinct two periods),
        // breaking ties toward the tightest description (the smaller larger-half
        // span) — which also disambiguates a linear ramp, where every split separates
        // the midpoints equally and only the tie-break distinguishes the balanced cut.
        int bestK = -1;
        double bestSeparation = double.NegativeInfinity;
        double bestWiderSpan = double.PositiveInfinity;
        for (int k = 1; k < n; k++)
        {
            var (aLo, aHi) = MinMax(dailyC, 0, k - 1);
            var (bLo, bHi) = MinMax(dailyC, k, n - 1);
            double separation = Math.Abs((aLo + aHi) / 2.0 - (bLo + bHi) / 2.0);
            if (separation < SplitThresholdC)
                continue;
            double widerSpan = Math.Max(aHi - aLo, bHi - bLo);
            if (separation > bestSeparation + Epsilon
                || (Math.Abs(separation - bestSeparation) <= Epsilon && widerSpan < bestWiderSpan))
            {
                bestK = k;
                bestSeparation = separation;
                bestWiderSpan = widerSpan;
            }
        }

        return bestK < 0
            ? [MakePeriod(dailyC, 0, n - 1)]
            : [MakePeriod(dailyC, 0, bestK - 1), MakePeriod(dailyC, bestK, n - 1)];
    }

    /// <summary>
    /// The per-local-day RAW °C highs and lows for the temperature summary, in chronological
    /// order. Each day's high/low is the max/min °C across all its blocks (mirroring
    /// <see cref="StructuredReportRenderer"/>'s grid aggregation), and a day whose every block
    /// has fully elapsed at <paramref name="nowUtc"/> is dropped (WX-188).
    ///
    /// <para>
    /// Highs and lows are extracted ASYMMETRICALLY (WX-230), each gated on the band that holds its
    /// extreme so a partial day can't contribute a bogus one:
    /// <list type="bullet">
    /// <item>a day contributes a HIGH only if it has its <b>afternoon</b> block (local 12:00–18:00).
    ///   A partial TRAILING day whose horizon ends before its afternoon has only a pre-dawn/morning
    ///   block, whose max is not a daytime high — counting it made the summary split off a phantom
    ///   "cooldown" (a steady 99 °F week ending in a pre-dawn 83 °F).</item>
    /// <item>a day contributes a LOW only if it has a <b>pre-dawn or morning</b> block (local 00:00
    ///   or 06:00 — the dawn minimum sits on that boundary). A leading day (today, sent past the
    ///   morning) whose earliest block is the afternoon has only daytime minima, not an overnight
    ///   low — the mirror of the same defect.</item>
    /// </list>
    /// A day can therefore contribute a high, a low, both, or neither, so the returned series can
    /// differ in length.
    /// </para>
    /// Values are RAW °C (no rounding here): the renderer converts and rounds once per recipient
    /// unit, exactly as the grid does, so the two never disagree by a rounding step.
    /// </summary>
    public static (IReadOnlyList<double> HighsC, IReadOnlyList<double> LowsC) DailyHighsLows(
        ForecastSnapshotBody body, DateTime nowUtc, TimeZoneInfo tz)
    {
        var order = new List<DateOnly>();
        var byDay = new Dictionary<DateOnly, (double Hi, double Lo, bool AnyLive, bool HasAfternoon, bool HasDawnWindow)>();
        // Sort by StartUtc rather than trust block order — final_snapshot is
        // Claude-emitted and the schema documents but does not enforce "earliest first".
        foreach (var b in body.Blocks.OrderBy(b => b.StartUtc))
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(b.StartUtc, DateTimeKind.Utc), tz);
            var day = DateOnly.FromDateTime(local);
            // Share the renderer's exact elapsed-block predicate so the summary's day-set
            // and the Extended Forecast grid's cannot drift (WX-188).
            bool live = SevereBlocks.NotFullyElapsed(b, nowUtc);
            bool afternoon = DayPartBands.HasAfternoon(local.Hour);     // the peak-heating band that holds the daily high (WX-230/234)
            bool dawnWindow = DayPartBands.HasDawnWindow(local.Hour);   // a morning-half block brackets the dawn minimum
            if (byDay.TryGetValue(day, out var cur))
                byDay[day] = (Math.Max(cur.Hi, b.TemperatureCelsius.Max), Math.Min(cur.Lo, b.TemperatureCelsius.Min), cur.AnyLive || live, cur.HasAfternoon || afternoon, cur.HasDawnWindow || dawnWindow);
            else
            {
                order.Add(day);
                byDay[day] = (b.TemperatureCelsius.Max, b.TemperatureCelsius.Min, live, afternoon, dawnWindow);
            }
        }

        var highs = new List<double>();
        var lows = new List<double>();
        foreach (var day in order)
        {
            var d = byDay[day];
            if (!d.AnyLive)
                continue;            // a wholly-past day is not on the grid, so not in the summary (WX-188)
            if (d.HasAfternoon)
                highs.Add(d.Hi);     // a real daytime high needs the afternoon (peak-heating) block
            if (d.HasDawnWindow)
                lows.Add(d.Lo);      // a real overnight low needs a pre-dawn/morning (morning-half) block
        }
        return (highs, lows);
    }

    /// <summary>
    /// The English prompt-guidance block naming the computed high/low ranges as ready
    /// <c>{q:temp_range:...}</c> tokens for the reconciler to weave into its native
    /// closing prose — so the figures are deterministic and the model supplies only
    /// the phrasing. Empty when no forecast days are available (the closing then omits
    /// a numeric temperature summary rather than inventing one).
    /// </summary>
    public static string BuildPromptGuidance(IReadOnlyList<double> highsC, IReadOnlyList<double> lowsC)
    {
        if (highsC.Count == 0 && lowsC.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("temperature_summary (WX-228): when you summarize daytime highs and overnight lows in the closing,");
        sb.AppendLine("state EXACTLY these ranges using the given {q:temp_range:lo:hi} tokens verbatim. Do NOT write your own");
        sb.AppendLine("temperature figures, and NEVER wrap a temperature token in a vague band word (no \"the upper {q:temp:36} range\",");
        sb.AppendLine("no \"in the low {q:temp:33}s\") — a {q:temp_range:...} token already renders a complete range in the recipient's units.");
        if (highsC.Count > 0)
            sb.Append("  daytime highs: ").AppendLine(DescribeSeries(Characterize(highsC)));
        if (lowsC.Count > 0)
            sb.Append("  overnight lows: ").AppendLine(DescribeSeries(Characterize(lowsC)));
        return sb.ToString();
    }

    private static string DescribeSeries(IReadOnlyList<TempPeriod> periods)
    {
        if (periods.Count == 1)
            return $"{periods[0].Token} across the whole period.";
        var (first, second) = (periods[0], periods[1]);
        string trend = second.LowC + second.HighC >= first.LowC + first.HighC ? "rising" : "falling";
        return $"{first.Token} for the earlier part of the period, then {second.Token} for the later part (a {trend} trend).";
    }

    // A period's range is the RAW °C min–max of its days (so the renderer's per-unit
    // rounding matches the grid). The one exception: a run that would DISPLAY as a single
    // whole degree — round(min) == round(max) — widens to a whole-degree ±1 °C band so the
    // prose reads as a small band ("35–37") rather than a robotic single point ("36").
    private static TempPeriod MakePeriod(IReadOnlyList<double> dailyC, int start, int end)
    {
        var (lo, hi) = MinMax(dailyC, start, end);
        double loWhole = Math.Round(lo), hiWhole = Math.Round(hi);
        return loWhole == hiWhole
            ? new TempPeriod(loWhole - 1, hiWhole + 1, start, end)
            : new TempPeriod(lo, hi, start, end);
    }

    private static (double Min, double Max) MinMax(IReadOnlyList<double> values, int start, int end)
    {
        double min = values[start], max = values[start];
        for (int i = start + 1; i <= end; i++)
        {
            if (values[i] < min) min = values[i];
            if (values[i] > max) max = values[i];
        }
        return (min, max);
    }
}