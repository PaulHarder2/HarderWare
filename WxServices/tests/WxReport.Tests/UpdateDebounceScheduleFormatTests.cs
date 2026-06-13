using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

/// <summary>
/// WX-181 day-banded debounce schedule: parse (strict / fail-closed) + the
/// step-function day → min-hours lookup.
/// </summary>
public class UpdateDebounceScheduleFormatTests
{
    [Fact]
    public void Parse_DefaultSchedule_TwoOrderedSteps()
    {
        var steps = UpdateDebounceScheduleFormat.Parse("1:6,3:12");
        Assert.Equal(2, steps.Count);
        Assert.Equal(new UpdateDebounceScheduleFormat.Step(1, 6), steps[0]);
        Assert.Equal(new UpdateDebounceScheduleFormat.Step(3, 12), steps[1]);
    }

    [Fact]
    public void Parse_ToleratesWhitespace()
    {
        var steps = UpdateDebounceScheduleFormat.Parse("  1 : 6 ,  3 : 12  ");
        Assert.Equal(new UpdateDebounceScheduleFormat.Step(1, 6), steps[0]);
        Assert.Equal(new UpdateDebounceScheduleFormat.Step(3, 12), steps[1]);
    }

    [Theory]
    [InlineData(1, 6)]   // today
    [InlineData(2, 6)]   // tomorrow — still the first band
    [InlineData(3, 12)]  // band changes at day 3
    [InlineData(4, 12)]
    [InlineData(10, 12)] // far out — latest step holds
    public void MinHoursForDay_StepFunction(int day, int expected)
    {
        var steps = UpdateDebounceScheduleFormat.Parse("1:6,3:12");
        Assert.Equal(expected, UpdateDebounceScheduleFormat.MinHoursForDay(steps, day));
    }

    [Fact]
    public void MinHoursForDay_SingleStep_AppliesToAllDays()
    {
        var steps = UpdateDebounceScheduleFormat.Parse("1:6");
        Assert.Equal(6, UpdateDebounceScheduleFormat.MinHoursForDay(steps, 1));
        Assert.Equal(6, UpdateDebounceScheduleFormat.MinHoursForDay(steps, 9));
    }

    [Theory]
    [InlineData("")]              // empty
    [InlineData("   ")]           // whitespace only
    [InlineData("2:6")]           // first day not 1
    [InlineData("1:6,3:12,2:5")]  // not ascending
    [InlineData("1:6,1:7")]       // duplicate day (not strictly ascending)
    [InlineData("1:6,3:12,3:5")]  // duplicate later day
    [InlineData("0:6")]           // day < 1
    [InlineData("1:-6")]          // negative hours
    [InlineData(",")]             // bare comma -> empty tokens (was IndexOutOfRange)
    [InlineData("1:6,,3:12")]     // double comma -> empty middle token
    [InlineData("1:6,")]          // trailing comma -> empty token
    [InlineData("1")]             // missing ':hours'
    [InlineData("1:6:9")]         // too many parts
    [InlineData("1:x")]           // non-integer hours
    [InlineData("x:6")]           // non-integer day
    public void Parse_Invalid_Throws(string raw)
    {
        Assert.Throws<FormatException>(() => UpdateDebounceScheduleFormat.Parse(raw));
    }
}