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

    private static SignificanceResult Passed(params string[] fired) =>
        new(true, fired, new DateOnly(2026, 9, 30), SevereEntered: false);

    private static LocalityState Rejected(DateTime at, params string[] fired)
    {
        var state = new LocalityState();
        RejectedGateMemory.Record(state, Passed(fired), at);
        return state;
    }

    [Fact]
    public void SameCriteria_WithinWindow_IsRepeat()
    {
        var state = Rejected(Now, Austin);
        Assert.True(RejectedGateMemory.IsRepeat(Passed(Austin), state, Now.AddHours(1), Window));
    }

    [Fact]
    public void SubsetOfRecordedCriteria_IsRepeat()
    {
        var state = Rejected(Now, Austin, "temp-delta@T2(2026-10-01)");
        Assert.True(RejectedGateMemory.IsRepeat(Passed(Austin), state, Now.AddHours(1), Window));
    }

    [Fact]
    public void SameCriteria_AtOrAfterWindow_AsksClaudeAgain()
    {
        var state = Rejected(Now, Austin);
        Assert.False(RejectedGateMemory.IsRepeat(Passed(Austin), state, Now.AddHours(Window), Window));
        Assert.False(RejectedGateMemory.IsRepeat(Passed(Austin), state, Now.AddHours(Window + 3), Window));
        Assert.True(RejectedGateMemory.IsRepeat(Passed(Austin), state, Now.AddHours(Window).AddMinutes(-1), Window));
    }

    [Fact]
    public void NewCriterion_AsksClaude()
    {
        var state = Rejected(Now, Austin);
        Assert.False(RejectedGateMemory.IsRepeat(
            Passed(Austin, "precip-add@T1(10-01 05Z)"), state, Now.AddHours(1), Window));
    }

    [Fact]
    public void SevereOnset_AsksClaude_EvenWhenEveryCriterionIsOnRecord()
    {
        var state = Rejected(Now, "severe-add@T1(09-30 17Z)");
        var severe = new SignificanceResult(true, ["severe-add@T1(09-30 17Z)"], new DateOnly(2026, 9, 30), SevereEntered: true);
        Assert.False(RejectedGateMemory.IsRepeat(severe, state, Now.AddHours(1), Window));
    }

    [Fact]
    public void DisjointHorizon_IsNeverARepeat()
    {
        var state = Rejected(Now, RejectedGateMemory.DisjointHorizon);
        Assert.False(RejectedGateMemory.IsRepeat(
            Passed(RejectedGateMemory.DisjointHorizon), state, Now.AddHours(1), Window));
    }

    [Fact]
    public void NoRecord_AsksClaude()
    {
        Assert.False(RejectedGateMemory.IsRepeat(Passed(Austin), new LocalityState(), Now, Window));
    }

    [Fact]
    public void WindowOfZero_TurnsTheSkipOff()
    {
        var state = Rejected(Now, Austin);
        Assert.False(RejectedGateMemory.IsRepeat(Passed(Austin), state, Now.AddMinutes(1), 0));
    }

    [Fact]
    public void ClockBehindTheRecord_AsksClaude()
    {
        var state = Rejected(Now, Austin);
        Assert.False(RejectedGateMemory.IsRepeat(Passed(Austin), state, Now.AddMinutes(-5), Window));
    }

    [Fact]
    public void ASend_ClearsTheRecord()
    {
        var state = Rejected(Now, Austin);
        Assert.True(RejectedGateMemory.IsRepeat(Passed(Austin), state, Now.AddHours(1), Window));

        ReportWorker.ApplySentState(
            state, "change", Now.AddHours(1), "input", new WeatherSnapshot { StationIcao = "KAUS" }, clearDegraded: true);

        Assert.Null(state.LastRejectedGateCriteria);
        Assert.Null(state.LastRejectedGateUtc);
        Assert.False(RejectedGateMemory.IsRepeat(Passed(Austin), state, Now.AddHours(2), Window));
    }

    [Fact]
    public void ACachedScheduledResend_AlsoClearsTheRecord()
    {
        var state = Rejected(Now, Austin);
        ReportWorker.ApplySentState(
            state, "scheduled", Now.AddHours(1), "input", new WeatherSnapshot { StationIcao = "KAUS" }, clearDegraded: false);
        Assert.Null(state.LastRejectedGateCriteria);
    }

    [Fact]
    public void Serialize_IsSortedDistinct_AndRefusesAnOverlongSet()
    {
        Assert.Equal("a\nb", RejectedGateMemory.Serialize(["b", "a", "b"]));
        Assert.Null(RejectedGateMemory.Serialize([]));
        var tooLong = Enumerable.Range(0, 200).Select(i => $"precip-remove@T1(09-30 {i:D4}Z)").ToList();
        Assert.Null(RejectedGateMemory.Serialize(tooLong));

        var state = new LocalityState();
        RejectedGateMemory.Record(state, Passed([.. tooLong]), Now);
        Assert.Null(state.LastRejectedGateCriteria);
        Assert.Null(state.LastRejectedGateUtc);
    }
}