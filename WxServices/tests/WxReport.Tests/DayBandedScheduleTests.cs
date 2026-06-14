using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

/// <summary>
/// Day-banded schedule (<see cref="DayBandedSchedule"/>): strict / fail-closed parse +
/// the step-function day → min-minutes lookup. Shared by the WX-181 debounce
/// (<c>"1:360,3:720"</c>) and the WX-157 quiet window (<c>"1:90,3:180"</c>).
/// </summary>
public class DayBandedScheduleTests
{
    [Fact]
    public void Parse_DefaultDebounceSchedule_TwoOrderedSteps()
    {
        var steps = DayBandedSchedule.Parse("1:360,3:720");
        Assert.Equal(2, steps.Count);
        Assert.Equal(new DayBandedSchedule.Step(1, 360), steps[0]);
        Assert.Equal(new DayBandedSchedule.Step(3, 720), steps[1]);
    }

    [Fact]
    public void Parse_ToleratesWhitespace()
    {
        var steps = DayBandedSchedule.Parse("  1 : 90 ,  3 : 180  ");
        Assert.Equal(new DayBandedSchedule.Step(1, 90), steps[0]);
        Assert.Equal(new DayBandedSchedule.Step(3, 180), steps[1]);
    }

    [Theory]
    [InlineData(1, 90)]   // today
    [InlineData(2, 90)]   // tomorrow — still the first band
    [InlineData(3, 180)]  // band changes at day 3
    [InlineData(4, 180)]
    [InlineData(10, 180)] // far out — latest step holds
    public void MinMinutesForDay_StepFunction(int day, int expected)
    {
        var steps = DayBandedSchedule.Parse("1:90,3:180");
        Assert.Equal(expected, DayBandedSchedule.MinMinutesForDay(steps, day));
    }

    [Fact]
    public void MinMinutesForDay_SingleStep_AppliesToAllDays()
    {
        var steps = DayBandedSchedule.Parse("1:90");
        Assert.Equal(90, DayBandedSchedule.MinMinutesForDay(steps, 1));
        Assert.Equal(90, DayBandedSchedule.MinMinutesForDay(steps, 9));
    }

    [Theory]
    [InlineData("")]                // empty
    [InlineData("   ")]             // whitespace only
    [InlineData("2:90")]            // first day not 1
    [InlineData("1:90,3:180,2:5")]  // not ascending
    [InlineData("1:90,1:120")]      // duplicate day (not strictly ascending)
    [InlineData("1:90,3:180,3:5")]  // duplicate later day
    [InlineData("0:90")]            // day < 1
    [InlineData("1:-90")]           // negative minutes
    [InlineData(",")]               // bare comma -> empty tokens (was IndexOutOfRange)
    [InlineData("1:90,,3:180")]     // double comma -> empty middle token
    [InlineData("1:90,")]           // trailing comma -> empty token
    [InlineData("1")]               // missing ':minutes'
    [InlineData("1:90:9")]          // too many parts
    [InlineData("1:x")]             // non-integer minutes
    [InlineData("x:90")]            // non-integer day
    public void Parse_Invalid_Throws(string raw)
    {
        Assert.Throws<FormatException>(() => DayBandedSchedule.Parse(raw));
    }
}