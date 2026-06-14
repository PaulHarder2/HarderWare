using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

/// <summary>
/// WX-157 day-banded pre-scheduled quiet window (<see cref="ReportWorker.ShouldQuietBeforeSlot"/>):
/// a significant unscheduled change is suppressed when the next upcoming scheduled slot
/// falls within the change's day-band quiet window — with a not-severe→severe punch-through
/// and "send" escape hatches. Schedule "1:90,3:180": days 1–2 → 90 min, day 3+ → 180 min.
/// </summary>
public class QuietWindowDecisionTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    // America/Chicago: CST (UTC-6) → CDT (UTC-5); used for the tz/DST case.
    private static readonly TimeZoneInfo Central = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Central Standard Time" : "America/Chicago");

    private static readonly IReadOnlyList<DayBandedSchedule.Step> Schedule =
        DayBandedSchedule.Parse("1:90,3:180");

    private static IReadOnlyList<int> Hours(params int[] h) => h;

    private static SignificanceResult Gate(DateOnly? changedDay, bool severe = false) =>
        new(Significant: true, FiredCriteria: [], EarliestChangedDayLocal: changedDay, SevereEntered: severe);

    private static DateOnly LocalDay(DateTime nowUtc, TimeZoneInfo tz, int dayOffset) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz)).AddDays(dayOffset);

    private static bool Decide(SignificanceResult gate, IReadOnlyList<int> hours, DateTime nowUtc, TimeZoneInfo tz) =>
        ReportWorker.ShouldQuietBeforeSlot(gate, hours, Schedule, nowUtc, tz, out _);

    [Fact]
    public void NearTermChange_WithinQuietWindow_Suppresses()
    {
        // 07:00 slot, now 06:00 → 60 min out <= 90 min (day-1 band) → suppress.
        var now = new DateTime(2026, 6, 14, 6, 0, 0, DateTimeKind.Utc);
        Assert.True(Decide(Gate(LocalDay(now, Utc, 0)), Hours(7), now, Utc));
    }

    [Fact]
    public void NearTermChange_OutsideQuietWindow_Sends()
    {
        // 07:00 slot, now 05:00 → 120 min out > 90 min → send.
        var now = new DateTime(2026, 6, 14, 5, 0, 0, DateTimeKind.Utc);
        Assert.False(Decide(Gate(LocalDay(now, Utc, 0)), Hours(7), now, Utc));
    }

    [Fact]
    public void FarOutChange_HasWiderQuietWindow_KeysOnChangeDay()
    {
        // 07:00 slot, now 04:30 → 150 min out. A day-1 change (90 min band) SENDS, but a
        // day-3 change (180 min band) is suppressed — the band keys on the change's day.
        var now = new DateTime(2026, 6, 14, 4, 30, 0, DateTimeKind.Utc);
        Assert.False(Decide(Gate(LocalDay(now, Utc, 0)), Hours(7), now, Utc));   // near-term → send
        Assert.True(Decide(Gate(LocalDay(now, Utc, 2)), Hours(7), now, Utc));    // day-3 → suppress
    }

    [Fact]
    public void SevereOnset_PunchesThrough_EvenWithinWindow()
    {
        var now = new DateTime(2026, 6, 14, 6, 30, 0, DateTimeKind.Utc); // 30 min before slot
        Assert.False(Decide(Gate(LocalDay(now, Utc, 0), severe: true), Hours(7), now, Utc));
    }

    [Fact]
    public void Uncharacterized_NoChangedDay_Sends()
    {
        var now = new DateTime(2026, 6, 14, 6, 30, 0, DateTimeKind.Utc);
        Assert.False(Decide(Gate(changedDay: null), Hours(7), now, Utc));
    }

    [Fact]
    public void NoScheduledHours_Sends()
    {
        var now = new DateTime(2026, 6, 14, 6, 30, 0, DateTimeKind.Utc);
        Assert.False(Decide(Gate(LocalDay(now, Utc, 0)), Hours(), now, Utc));
    }

    [Fact]
    public void MultipleHours_PicksNearestUpcomingSlot()
    {
        // Hours 7 and 19; now 18:00 → nearest upcoming slot is 19:00 (60 min), not 07:00.
        var now = new DateTime(2026, 6, 14, 18, 0, 0, DateTimeKind.Utc);
        Assert.True(Decide(Gate(LocalDay(now, Utc, 0)), Hours(7, 19), now, Utc));
    }

    [Fact]
    public void AllSlotsPassedToday_RollsToTomorrow_Sends()
    {
        // 07:00 slot, now 23:00 → next slot is tomorrow 07:00 (8 h out) → send.
        var now = new DateTime(2026, 6, 14, 23, 0, 0, DateTimeKind.Utc);
        Assert.False(Decide(Gate(LocalDay(now, Utc, 0)), Hours(7), now, Utc));
    }

    [Fact]
    public void LateSlot_NearMidnight_SuppressesWithinWindow()
    {
        // 23:00 slot, now 22:00 → 60 min out <= 90 → suppress.
        var now = new DateTime(2026, 6, 14, 22, 0, 0, DateTimeKind.Utc);
        Assert.True(Decide(Gate(LocalDay(now, Utc, 0)), Hours(23), now, Utc));
    }

    [Fact]
    public void DstSpringForwardDay_SlotComputedInLocalTime_Suppresses()
    {
        // America/Chicago spring-forward 2026-03-08 (02:00 CST → 03:00 CDT). 07:00 CDT slot.
        // now = 11:00 UTC = 06:00 CDT → 60 min to the 12:00 UTC (07:00 CDT) slot → suppress.
        var now = new DateTime(2026, 3, 8, 11, 0, 0, DateTimeKind.Utc);
        Assert.True(Decide(Gate(LocalDay(now, Central, 0)), Hours(7), now, Central));
    }
}