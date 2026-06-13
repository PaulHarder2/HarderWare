using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

/// <summary>
/// WX-181 day-banded debounce decision (<see cref="ReportWorker.ShouldDebounceUpdate"/>):
/// a significant unscheduled change is suppressed only when its day-band's minimum gap
/// has not elapsed since the last unscheduled send — with a not-severe→severe punch-through
/// and "send" escape hatches. Schedule "1:6,3:12": days 1–2 → 6 h, day 3+ → 12 h.
/// </summary>
public class UpdateDebounceDecisionTests
{
    private static readonly DateTime Now = new(2026, 6, 13, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly IReadOnlyList<UpdateDebounceScheduleFormat.Step> Schedule =
        UpdateDebounceScheduleFormat.Parse("1:6,3:12");

    private static DateOnly Day(int n) => DateOnly.FromDateTime(Now).AddDays(n - 1); // Day(1) = today (UTC tz)

    private static SignificanceResult Gate(DateOnly? day, bool severe = false) =>
        new(Significant: true, FiredCriteria: [], EarliestChangedDayLocal: day, SevereEntered: severe);

    private static bool Decide(SignificanceResult gate, DateTime? lastUnscheduled) =>
        ReportWorker.ShouldDebounceUpdate(gate, lastUnscheduled, Schedule, Now, Utc, out _);

    [Theory]
    [InlineData(1, 3.0, true)]    // today, 3 h ago < 6 h band → suppress
    [InlineData(1, 7.0, false)]   // today, 7 h ago >= 6 h → send
    [InlineData(2, 5.0, true)]    // tomorrow still in the 6 h band → suppress
    [InlineData(2, 6.5, false)]   // tomorrow, past 6 h → send
    [InlineData(3, 10.0, true)]   // day 3 → 12 h band; 10 h ago → suppress
    [InlineData(3, 13.0, false)]  // day 3, past 12 h → send
    [InlineData(5, 11.5, true)]   // far out → still the 12 h band → suppress
    public void DayBand_SuppressesWithinWindow(int changeDay, double hoursSinceLast, bool expectSuppress)
    {
        var suppressed = Decide(Gate(Day(changeDay)), Now.AddHours(-hoursSinceLast));
        Assert.Equal(expectSuppress, suppressed);
    }

    [Fact]
    public void SevereOnset_PunchesThrough_EvenWithinWindow()
    {
        // A not-severe→severe onset sends immediately regardless of the band.
        Assert.False(Decide(Gate(Day(1), severe: true), Now.AddHours(-0.5)));
    }

    [Fact]
    public void Uncharacterized_NoEarliestDay_Sends()
    {
        // e.g. disjoint-horizon: significant but no changed-day → not debounced.
        Assert.False(Decide(Gate(day: null), Now.AddHours(-0.5)));
    }

    [Fact]
    public void NeverSentUnscheduled_Sends()
    {
        Assert.False(Decide(Gate(Day(1)), lastUnscheduled: null));
    }

    [Fact]
    public void Reason_IsPopulated_WhenSuppressed()
    {
        var suppressed = ReportWorker.ShouldDebounceUpdate(
            Gate(Day(1)), Now.AddHours(-2), Schedule, Now, Utc, out var reason);
        Assert.True(suppressed);
        Assert.Contains("min gap 6h", reason);
    }
}