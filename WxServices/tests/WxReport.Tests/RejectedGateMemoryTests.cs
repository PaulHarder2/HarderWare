using MetarParser.Data.Entities;

using WxInterp;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

// WX-506 rework: a gate result Claude has already rejected is not paid for again on every
// arrival. Grounded in Austin, TX on 2026-09-29/30: the gate fired exactly
// `precip-remove@T1(09-30 11Z)` on 18 consecutive arrivals, Claude kept the rain each time,
// and each reconciled update was withheld as carrying no change.
public class RejectedGateMemoryTests
{
    private static readonly DateTime Now = new(2026, 9, 29, 15, 0, 0, DateTimeKind.Utc);
    private const double Window = 6;
    private const string Austin = "precip-remove@T1(09-30 11Z)";

    // The evidence at the rejection: a METAR (station; wind, visibility, sky, temperature bands;
    // present weather), a TAF issuance and a GFS run.
    private static readonly InputIdentity Evidence = new(
        Metar: "KAUS;W1;V1;S2;T16;P",
        Taf: "2026-09-29T12:00:00.0000000Z",
        Gfs: "2026-09-29T06:00:00.0000000Z");

    private const int Prior = 23600;   // the prior ForecastSnapshot the gate measured against

    private static SignificanceResult Passed(params string[] fired) =>
        new(true, fired, new DateOnly(2026, 9, 30), SevereEntered: false);

    private static LocalityState Rejected(params string[] fired)
    {
        var state = new LocalityState();
        RejectedGateMemory.Record(state, Passed(fired), Evidence, Prior, Now);
        return state;
    }

    private static RejectedGateCheck Check(SignificanceResult gate, LocalityState state, InputIdentity? input = null, double hoursLater = 1, double window = Window, int prior = Prior) =>
        RejectedGateMemory.Check(gate, state, input ?? Evidence, prior, Now.AddHours(hoursLater), window);

    [Fact]
    public void SameCriteriaAndEvidence_WithinWindow_IsRepeat() =>
        Assert.Equal(RejectedGateCheck.Repeat, Check(Passed(Austin), Rejected(Austin)));

    [Fact]
    public void SubsetOfRecordedCriteria_IsRepeat() =>
        Assert.Equal(RejectedGateCheck.Repeat, Check(Passed(Austin), Rejected(Austin, "temp-delta@T2(2026-10-01)")));

    [Fact]
    public void OnlySkyAndTemperatureBandsMoved_IsRepeat()
    {
        // The hourly churn: cloud cover and the ~5 F temperature band. Not treated as reason to
        // reconsider a rejected forecast change.
        var laterMetar = Evidence with { Metar = "KAUS;W1;V1;S3;T17;P" };
        Assert.Equal(RejectedGateCheck.Repeat, Check(Passed(Austin), Rejected(Austin), laterMetar));
    }

    [Fact]
    public void AWindBandChange_AsksClaude() =>
        Assert.Equal(RejectedGateCheck.NewWeather,
            Check(Passed(Austin), Rejected(Austin), Evidence with { Metar = "KAUS;W3;V1;S2;T16;P" }));

    [Fact]
    public void AVisibilityBandChange_AsksClaude() =>
        Assert.Equal(RejectedGateCheck.NewWeather,
            Check(Passed(Austin), Rejected(Austin), Evidence with { Metar = "KAUS;W1;V0;S2;T16;P" }));

    [Fact]
    public void AnotherPrior_AsksClaude() =>
        Assert.Equal(RejectedGateCheck.NewBaseline, Check(Passed(Austin), Rejected(Austin), prior: Prior + 1));

    [Fact]
    public void AStationBeginningWithP_StillComparesItsWeather()
    {
        // PHNL: the station code must not be read as the present-weather segment.
        var state = new LocalityState();
        var honolulu = new InputIdentity("PHNL;W1;V1;S2;T16;P", Evidence.Taf, Evidence.Gfs);
        RejectedGateMemory.Record(state, Passed(Austin), honolulu, Prior, Now);
        Assert.Equal(RejectedGateCheck.NewWeather,
            Check(Passed(Austin), state, honolulu with { Metar = "PHNL;W1;V1;S3;T16;Pheavy,rain,thunderstorm" }));
        Assert.Equal(RejectedGateCheck.Repeat, Check(Passed(Austin), state, honolulu));
    }

    [Fact]
    public void AtOrAfterTheWindow_AsksClaudeAgain()
    {
        var state = Rejected(Austin);
        Assert.Equal(RejectedGateCheck.WindowExpired, Check(Passed(Austin), state, hoursLater: Window));
        Assert.Equal(RejectedGateCheck.WindowExpired, Check(Passed(Austin), state, hoursLater: Window + 3));
        Assert.Equal(RejectedGateCheck.Repeat, Check(Passed(Austin), state, hoursLater: Window - 1.0 / 60));
    }

    [Fact]
    public void NewCriterion_AsksClaude() =>
        Assert.Equal(RejectedGateCheck.NewCriterion, Check(Passed(Austin, "precip-add@T1(10-01 05Z)"), Rejected(Austin)));

    [Fact]
    public void NewTaf_AsksClaude() =>
        Assert.Equal(RejectedGateCheck.NewGuidance,
            Check(Passed(Austin), Rejected(Austin), Evidence with { Taf = "2026-09-29T15:00:00.0000000Z" }));

    [Fact]
    public void NewGfsRun_AsksClaude() =>
        Assert.Equal(RejectedGateCheck.NewGuidance,
            Check(Passed(Austin), Rejected(Austin), Evidence with { Gfs = "2026-09-29T12:00:00.0000000Z" }));

    [Fact]
    public void ObservedWeatherStarting_AsksClaude() =>
        Assert.Equal(RejectedGateCheck.NewWeather,
            Check(Passed(Austin), Rejected(Austin), Evidence with { Metar = "KAUS;W1;V1;S3;T16;Prain" }));

    [Fact]
    public void AnotherStation_AsksClaude() =>
        Assert.Equal(RejectedGateCheck.NewWeather,
            Check(Passed(Austin), Rejected(Austin), Evidence with { Metar = "KEDC;W1;V1;S2;T16;P" }));

    [Fact]
    public void AnUnreadableRecordedEvidence_AsksClaude()
    {
        var state = Rejected(Austin);
        state.LastRejectedInputHash = "not an identity";
        Assert.Equal(RejectedGateCheck.NewGuidance, Check(Passed(Austin), state));
    }

    [Fact]
    public void SevereOnset_AsksClaude_EvenWhenEveryCriterionIsOnRecord()
    {
        var severe = new SignificanceResult(true, ["severe-add@T1(09-30 17Z)"], new DateOnly(2026, 9, 30), SevereEntered: true);
        Assert.Equal(RejectedGateCheck.SevereOnset, Check(severe, Rejected("severe-add@T1(09-30 17Z)")));
    }

    [Fact]
    public void DisjointHorizon_IsNeverARepeat() =>
        Assert.Equal(RejectedGateCheck.DisjointHorizon,
            Check(Passed(RejectedGateMemory.DisjointHorizonCriterion), Rejected(RejectedGateMemory.DisjointHorizonCriterion)));

    [Fact]
    public void NoRecord_AndAWindowOfZero_AskClaude()
    {
        Assert.Equal(RejectedGateCheck.NoRecord, Check(Passed(Austin), new LocalityState()));
        Assert.Equal(RejectedGateCheck.NoRecord, Check(Passed(Austin), Rejected(Austin), window: 0));
    }

    [Fact]
    public void ClockBehindTheRecord_AsksClaude() =>
        Assert.Equal(RejectedGateCheck.ClockBehind, Check(Passed(Austin), Rejected(Austin), hoursLater: -0.1));

    [Fact]
    public void ASend_ClearsTheRecord()
    {
        var state = Rejected(Austin);
        ReportWorker.ApplySentState(
            state, "change", Now.AddHours(1), "input", new WeatherSnapshot { StationIcao = "KAUS" }, clearDegraded: true);

        Assert.Null(state.LastRejectedGateCriteria);
        Assert.Null(state.LastRejectedGateUtc);
        Assert.Null(state.LastRejectedInputHash);
        Assert.Null(state.LastRejectedPriorSnapshotId);
        Assert.Equal(RejectedGateCheck.NoRecord, Check(Passed(Austin), state));
    }

    [Fact]
    public void ACachedScheduledResend_AlsoClearsTheRecord()
    {
        var state = Rejected(Austin);
        ReportWorker.ApplySentState(
            state, "scheduled", Now.AddHours(1), "input", new WeatherSnapshot { StationIcao = "KAUS" }, clearDegraded: false);
        Assert.Null(state.LastRejectedGateCriteria);
    }

    [Fact]
    public void OnlyRedundantAndNoChangeSuppressionsAreRecorded()
    {
        Assert.True(RejectedGateMemory.Records(ReportWorker.UnscheduledSuppression.NoComputedChange));
        Assert.True(RejectedGateMemory.Records(ReportWorker.UnscheduledSuppression.Redundant));
        Assert.False(RejectedGateMemory.Records(ReportWorker.UnscheduledSuppression.SevereFlip));
        Assert.False(RejectedGateMemory.Records(ReportWorker.UnscheduledSuppression.None));
    }

    [Fact]
    public void Serialize_IsSortedDistinct_AndRefusesAnOverlongSet()
    {
        Assert.Equal("a\nb", RejectedGateMemory.Serialize(["b", "a", "b"]));
        Assert.Null(RejectedGateMemory.Serialize([]));
        var tooLong = Enumerable.Range(0, 200).Select(i => $"precip-remove@T1(09-30 {i:D4}Z)").ToList();
        Assert.Null(RejectedGateMemory.Serialize(tooLong));

        var state = new LocalityState();
        RejectedGateMemory.Record(state, Passed([.. tooLong]), Evidence, Prior, Now);
        Assert.Null(state.LastRejectedGateCriteria);
        Assert.Null(state.LastRejectedGateUtc);
        Assert.Null(state.LastRejectedInputHash);
    }

    [Fact]
    public void ObservedEvidence_KeepsStationWindVisibilityAndWeather_OrTheWholeSignature()
    {
        Assert.Equal("KAUS;W1;V1;Prain,ts", RejectedGateMemory.ObservedEvidence("KAUS;W1;V1;S2;T16;Prain,ts"));
        Assert.Equal("none", RejectedGateMemory.ObservedEvidence("none"));
        Assert.Equal("KAUS;W1", RejectedGateMemory.ObservedEvidence("KAUS;W1"));
    }
}