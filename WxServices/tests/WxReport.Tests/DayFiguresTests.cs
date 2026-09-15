using System;
using System.Collections.Generic;
using System.Linq;

using MetarParser.Data.Entities;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

// WX-506: the per-day temperature and wind rule shared by the significance gate and the change detector. Hours
// already past keep the published values, so once the afternoon has gone today's high can only rise, its low only
// fall and its peak wind only rise; while the afternoon is still ahead a lower afternoon can still lower the high.
// Every scenario runs through BOTH consumers, which must agree.
public class DayFiguresTests
{
    // Tuesday 2026-06-02 in UTC; blocks are 00, 06, 12 and 18 Z. Thresholds are the defaults: T1 temperature 5 °F,
    // T1 wind 12 kt, wind advisory 25 kt, heat advisory 100 °F (kept out of reach here).
    private static readonly DateTime Day = new(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly SignificanceGateConfig Cfg = new();

    private static readonly DateTime Morning = Day.AddHours(8);   // the 06Z block in progress, afternoon ahead
    private static readonly DateTime Evening = Day.AddHours(19);  // 00, 06 and 12Z elapsed, 18Z in progress

    public static TheoryData<string> Scenarios => new(All.Keys);

    private static readonly Dictionary<string, Scenario> All = new()
    {
        ["late day: a cooler evening does not lower the published high"] = new(
            Body(Blk(12, loF: 70, hiF: 90), Blk(18, loF: 70, hiF: 80)),
            Body(Blk(18, loF: 70, hiF: 70)),
            Evening, null, null),
        ["late day: an evening above the published high raises it"] = new(
            Body(Blk(12, loF: 70, hiF: 90), Blk(18, loF: 70, hiF: 80)),
            Body(Blk(18, loF: 70, hiF: 96)),
            Evening, ChangePhenomenon.Temperature, ChangeDirection.Strengthening),
        ["late day: a warmer evening low does not raise the published low"] = new(
            Body(Blk(6, loF: 50, hiF: 80), Blk(18, loF: 60, hiF: 80)),
            Body(Blk(18, loF: 70, hiF: 80)),
            Evening, null, null),
        ["late day: an evening below the published low lowers it"] = new(
            Body(Blk(6, loF: 50, hiF: 80), Blk(18, loF: 60, hiF: 80)),
            Body(Blk(18, loF: 44, hiF: 80)),
            Evening, ChangePhenomenon.Temperature, ChangeDirection.Weakening),
        ["morning: a cooler afternoon still ahead lowers the high"] = new(
            Body(Blk(6, loF: 60, hiF: 75), Blk(12, loF: 70, hiF: 90)),
            Body(Blk(6, loF: 60, hiF: 75), Blk(12, loF: 70, hiF: 82)),
            Morning, ChangePhenomenon.Temperature, ChangeDirection.Weakening),
        ["late day: a calmer evening does not lower the published peak wind"] = new(
            Body(Blk(12, wind: 30), Blk(18, wind: 10)),
            Body(Blk(18, wind: 5)),
            Evening, null, null),
        ["late day: a windier evening below the published peak is not news"] = new(
            Body(Blk(12, wind: 30), Blk(18, wind: 8)),
            Body(Blk(18, wind: 22)),
            Evening, null, null),
        ["late day: an evening above the published peak raises it"] = new(
            Body(Blk(12, wind: 20), Blk(18, wind: 8)),
            Body(Blk(18, wind: 34)),
            Evening, ChangePhenomenon.Wind, ChangeDirection.Strengthening),
        ["a later day's peak wind falling is a weakening"] = new(
            Body(Blk(30, wind: 30), Blk(36, wind: 10)),
            Body(Blk(30, wind: 15), Blk(36, wind: 10)),
            Morning, ChangePhenomenon.Wind, ChangeDirection.Weakening),
        ["a block rolling in at the horizon's edge is not news"] = new(
            Body(Blk(96, hiF: 72, wind: 12), Blk(102, hiF: 72, wind: 12)),
            Body(Blk(96, hiF: 72, wind: 12), Blk(102, hiF: 72, wind: 12), Blk(108, hiF: 88, wind: 28)),
            Morning, null, null),
        ["a block the new forecast no longer carries is not a change"] = new(
            Body(Blk(30, hiF: 70, wind: 10), Blk(36, hiF: 90, wind: 30)),
            Body(Blk(30, hiF: 70, wind: 10)),
            Morning, null, null),
        ["a new block filling a gap inside the sent forecast is news"] = new(
            Body(Blk(30, hiF: 80), Blk(42, hiF: 80)),
            Body(Blk(30, hiF: 80), Blk(36, hiF: 101), Blk(42, hiF: 80)),
            Morning, ChangePhenomenon.Temperature, ChangeDirection.Appearing),
        ["a trailing day with no afternoon block has no high to change"] = new(
            Body(Blk(96, hiF: 62), Blk(102, hiF: 62)),
            Body(Blk(96, hiF: 90), Blk(102, hiF: 90)),
            Morning, null, null),
        ["a later day's block below its peak moving is not news"] = new(
            Body(Blk(30, wind: 30), Blk(36, wind: 5)),
            Body(Blk(30, wind: 30), Blk(36, wind: 20)),
            Morning, null, null),
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Detector_FollowsTheRule(string name)
    {
        var s = All[name];
        var changes = DeterministicChangeDetector.Detect(s.Prior, s.Current, Cfg, s.NowUtc, Utc)
            .Where(c => c.Phenomenon is ChangePhenomenon.Temperature or ChangePhenomenon.Wind)
            .ToList();

        if (s.Phenomenon is null)
            Assert.Empty(changes);
        else
        {
            var c = Assert.Single(changes);
            Assert.Equal(s.Phenomenon, c.Phenomenon);
            Assert.Equal(s.Direction, c.Direction);
        }
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Gate_AgreesWithTheDetector(string name)
    {
        var s = All[name];
        Assert.Equal(s.Phenomenon is not null, SignificanceGate.Evaluate(s.Prior, s.Current, Cfg, s.NowUtc, Utc).Significant);
    }

    [Fact]
    public void WindWindow_IsTheBlockHoldingTheNewPeak_OrThePublishedPeakWhenItFell()
    {
        var up = DeterministicChangeDetector.Detect(
            Body(Blk(30, wind: 10), Blk(36, wind: 10)), Body(Blk(30, wind: 10), Blk(36, wind: 30)), Cfg, Morning, Utc);
        Assert.Equal(Day.AddHours(36), Assert.Single(up).Window.StartUtc);

        var down = DeterministicChangeDetector.Detect(
            Body(Blk(30, wind: 30), Blk(36, wind: 10)), Body(Blk(30, wind: 15), Blk(36, wind: 10)), Cfg, Morning, Utc);
        Assert.Equal(Day.AddHours(30), Assert.Single(down).Window.StartUtc);

        // Two blocks reach the new peak: the window names the first, when the peak arrives.
        var tied = DeterministicChangeDetector.Detect(
            Body(Blk(30, wind: 10), Blk(36, wind: 10)), Body(Blk(30, wind: 30), Blk(36, wind: 30)), Cfg, Morning, Utc);
        Assert.Equal(Day.AddHours(30), Assert.Single(tied).Window.StartUtc);
    }

    [Fact]
    public void LocalDay_SpansUtcMidnight_ElapsedAfternoonAndEveningAreOneDay()
    {
        // UTC-6: 18Z is the local afternoon (elapsed at 01Z, 19:00 local) and 00Z the same local day's evening. A hot
        // evening above the afternoon's published high raises that local day's high; grouped by UTC day it would not.
        var minus6 = TimeZoneInfo.CreateCustomTimeZone("utc-minus-6", TimeSpan.FromHours(-6), "UTC-6", "UTC-6");
        var now = Day.AddHours(25);
        var prior = Body(Blk(18, hiF: 90), Blk(24, hiF: 80));
        var current = Body(Blk(24, hiF: 96));

        var c = Assert.Single(DeterministicChangeDetector.Detect(prior, current, Cfg, now, minus6));
        Assert.Equal(ChangeDirection.Strengthening, c.Direction);
        Assert.True(SignificanceGate.Evaluate(prior, current, Cfg, now, minus6).Significant);
    }

    [Fact]
    public void Compare_KeepsPublishedValuesForElapsedHours_EvenWhenTheNewForecastStillCarriesThem()
    {
        // The new forecast restates the elapsed afternoon at 60 °F; the past is the published 90.
        var day = Assert.Single(DayFigures.Compare(
            Body(Blk(12, hiF: 90), Blk(18, hiF: 80)), Body(Blk(12, hiF: 60), Blk(18, hiF: 70)),
            Evening, Evening.AddHours(120), Utc));
        Assert.Equal(FtoC(90), day.Now.HiC!.Value, 6);
        Assert.Equal(FtoC(90), day.Published.HiC!.Value, 6);
    }

    // Precipitation and severe: a change is news only where one of the gate's criteria fires, in both — endings near-term
    // only, onsets at any range, and a wording step such as possible -> expected never. Block hours are from Day; at Morning (08Z) a block
    // at 12 is T1, 36 is T2 (28 h), 60 is T3 (52 h) and 84 is T4 (76 h).
    public static TheoryData<string> Endings => new(AllEndings.Keys);

    private static readonly Dictionary<string, (ForecastSnapshotBody Prior, ForecastSnapshotBody Current, bool News)> AllEndings = new()
    {
        ["rain ending in the first 24 h is news"] = (Body(Wet(12, PrecipPhenomenon.Rain)), Body(Blk(12)), true),
        ["rain ending beyond 24 h is not"] = (Body(Wet(36, PrecipPhenomenon.Rain)), Body(Blk(36)), false),
        ["snow giving way to rain within 48 h is news"] = (Body(Wet(36, PrecipPhenomenon.Snow)), Body(Wet(36, PrecipPhenomenon.Rain)), true),
        ["severe clearing within 72 h is news"] = (Body(Wet(60, PrecipPhenomenon.Thunderstorm, severe: true)), Body(Wet(60, PrecipPhenomenon.Thunderstorm)), true),
        ["severe clearing beyond 72 h is not"] = (Body(Wet(84, PrecipPhenomenon.Thunderstorm, severe: true)), Body(Wet(84, PrecipPhenomenon.Thunderstorm)), false),
        ["a severe storm clearing to dry within 72 h is news"] = (Body(Wet(60, PrecipPhenomenon.Thunderstorm, severe: true)), Body(Blk(60)), true),
        ["wind falling below advisory within 48 h is news"] = (Body(Blk(36, wind: 30)), Body(Blk(36, wind: 20)), true),
        ["wind falling below advisory beyond 48 h is not"] = (Body(Blk(60, wind: 30)), Body(Blk(60, wind: 20)), false),
        ["rain firming from possible to expected is not"] = (Body(Wet(12, PrecipPhenomenon.Rain, PrecipExpectation.Possible)), Body(Wet(12, PrecipPhenomenon.Rain, PrecipExpectation.Certain)), false),
        ["snow giving way to rain beyond 48 h is not"] = (Body(Wet(60, PrecipPhenomenon.Snow)), Body(Wet(60, PrecipPhenomenon.Rain)), false),
        ["rain turning to snow at any range is news"] = (Body(Wet(84, PrecipPhenomenon.Rain)), Body(Wet(84, PrecipPhenomenon.Snow)), true),
    };

    [Theory]
    [MemberData(nameof(Endings))]
    public void HazardEndings_DetectorAndGateAgree(string name)
    {
        var (prior, current, news) = AllEndings[name];
        Assert.Equal(news, DeterministicChangeDetector.Detect(prior, current, Cfg, Morning, Utc).Count > 0);
        Assert.Equal(news, SignificanceGate.Evaluate(prior, current, Cfg, Morning, Utc).Significant);
    }

    private static ForecastSnapshotBlock Wet(
        double hoursFromDay, PrecipPhenomenon phenomenon, PrecipExpectation expectation = PrecipExpectation.Likely, bool severe = false) =>
        Blk(hoursFromDay) with { PrecipExpectation = expectation, PrecipPhenomenon = phenomenon, SevereFlag = severe };

    private sealed record Scenario(
        ForecastSnapshotBody Prior, ForecastSnapshotBody Current, DateTime NowUtc,
        ChangePhenomenon? Phenomenon, ChangeDirection? Direction);

    private static ForecastSnapshotBlock Blk(double hoursFromDay, double loF = 60, double hiF = 80, int wind = 10) => new()
    {
        StartUtc = Day.AddHours(hoursFromDay),
        SkyState = SkyState.Clear,
        Obscuration = Obscuration.None,
        TemperatureCelsius = new(FtoC(loF), FtoC(hiF)),
        WindKt = new(0, wind),
        PrecipExpectation = PrecipExpectation.None,
        SevereFlag = false,
    };

    private static ForecastSnapshotBody Body(params ForecastSnapshotBlock[] blocks) => new() { Blocks = blocks };

    private static double FtoC(double f) => (f - 32.0) * 5.0 / 9.0;
}