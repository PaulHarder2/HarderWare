using WxInterp;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

// WX-527: when a report Claude generated could not be emailed, it is re-sent rather than
// regenerated, unless one of Paul's rules (2026-10-01) makes it stale. Grounded in the
// 2026-09-06 outage: SMTP unreachable for ~20 hours, 265 scheduled reconciles in one day.
public class PendingDeliveryTests
{
    private static readonly TimeZoneInfo Central = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Central Standard Time" : "America/Chicago");

    // Generated 11:00 CDT, inside the 06-12 local day-part (11:00Z-17:00Z); the 17:30Z checks
    // below are 12:30 CDT, in the next day-part and well inside the 3-hour age limit.
    private static readonly DateTime Generated = new(2026, 9, 6, 16, 0, 0, DateTimeKind.Utc);

    private static readonly InputIdentity Evidence = new(
        Metar: "KAUS;W1;V1;S2;T16;P",
        Taf: "2026-09-06T11:34:00.0000000",
        Gfs: "2026-09-06T06:00:00.0000000Z");

    private static readonly (DateTime, DateTime)[] NoChanges = [];

    private static PendingStale Check(
        DateTime now, ReportKind kind = ReportKind.Scheduled, InputIdentity? current = null,
        (DateTime, DateTime)[]? windows = null, bool severeOnset = false, string? recorded = null) =>
        PendingDelivery.Check(kind, Generated, recorded ?? Evidence.Serialize(), current ?? Evidence,
            windows ?? NoChanges, severeOnset, now, Central);

    [Fact]
    public void SameEvidence_WithinTheDayPart_IsResent() =>
        Assert.Equal(PendingStale.None, Check(Generated.AddMinutes(50)));

    [Fact]
    public void OnlySkyAndTemperatureMoved_IsResent() =>
        Assert.Equal(PendingStale.None, Check(Generated.AddMinutes(50), current: Evidence with { Metar = "KAUS;W1;V1;S3;T17;P" }));

    [Fact]
    public void AScheduledReport_AcrossADayPart_IsStale() =>
        // 12:30 CDT: the 06-12 block it opened with has ended.
        Assert.Equal(PendingStale.DayPartCrossed, Check(new DateTime(2026, 9, 6, 17, 30, 0, DateTimeKind.Utc)));

    [Fact]
    public void AnUpdate_AcrossADayPart_WithAChangeInTheEndedPart_IsStale() =>
        Assert.Equal(PendingStale.DayPartCrossed, Check(
            new DateTime(2026, 9, 6, 17, 30, 0, DateTimeKind.Utc), ReportKind.Unscheduled,
            windows: [(new DateTime(2026, 9, 6, 15, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 6, 16, 0, 0, DateTimeKind.Utc))]));

    [Fact]
    public void AnUpdate_AcrossADayPart_WithChangesOnlyLater_IsResent() =>
        Assert.Equal(PendingStale.None, Check(
            new DateTime(2026, 9, 6, 17, 30, 0, DateTimeKind.Utc), ReportKind.Unscheduled,
            windows: [(new DateTime(2026, 9, 6, 19, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 6, 22, 0, 0, DateTimeKind.Utc))]));

    [Fact]
    public void AnUpdate_AChangeSpanningTheBoundary_CountsAsInTheEndedPart() =>
        Assert.Equal(PendingStale.DayPartCrossed, Check(
            new DateTime(2026, 9, 6, 17, 30, 0, DateTimeKind.Utc), ReportKind.Unscheduled,
            windows: [(new DateTime(2026, 9, 6, 16, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 6, 19, 0, 0, DateTimeKind.Utc))]));

    [Fact]
    public void ThreeHoursOld_IsStale_EvenInTheSameDayPart()
    {
        // Generated 11:05Z (06:05 CDT); at 14:05Z it is 3 hours old and still in the 06-12 part.
        var early = new DateTime(2026, 9, 6, 11, 5, 0, DateTimeKind.Utc);
        Assert.Equal(PendingStale.TooOld, PendingDelivery.Check(
            ReportKind.Scheduled, early, Evidence.Serialize(), Evidence, NoChanges, false, early.AddHours(3), Central));
        Assert.Equal(PendingStale.None, PendingDelivery.Check(
            ReportKind.Scheduled, early, Evidence.Serialize(), Evidence, NoChanges, false, early.AddHours(3).AddMinutes(-1), Central));
    }

    [Fact]
    public void TheClockBehindTheReport_IsStale() =>
        Assert.Equal(PendingStale.TooOld, Check(Generated.AddMinutes(-1)));

    [Fact]
    public void ANewGfsRun_IsStale() =>
        Assert.Equal(PendingStale.NewGfsRun, Check(Generated.AddMinutes(50), current: Evidence with { Gfs = "2026-09-06T12:00:00.0000000Z" }));

    [Fact]
    public void ObservedWeatherStarting_IsStale() =>
        Assert.Equal(PendingStale.NewWeather, Check(Generated.AddMinutes(50), current: Evidence with { Metar = "KAUS;W1;V1;S2;T16;Prain,thunderstorm" }));

    [Fact]
    public void AWindBandChange_IsStale() =>
        Assert.Equal(PendingStale.NewWeather, Check(Generated.AddMinutes(50), current: Evidence with { Metar = "KAUS;W3;V1;S2;T16;P" }));

    [Fact]
    public void ASevereOnset_IsStale_BeforeAnythingElse() =>
        Assert.Equal(PendingStale.SevereOnset, Check(Generated.AddMinutes(50), severeOnset: true));

    [Fact]
    public void AnUnreadableRecord_IsStale() =>
        Assert.Equal(PendingStale.UnreadableRecord, Check(Generated.AddMinutes(50), recorded: "garbage"));

    // CheckAsync: the TAF comparison runs only when it decides, and against the recorded TAF.
    private static async Task<(PendingStale Stale, List<string> Asked)> DecideAsync(InputIdentity current, bool same, DateTime? now = null)
    {
        var asked = new List<string>();
        var stale = await PendingDelivery.CheckAsync(
            ReportKind.Scheduled, Generated, Evidence.Serialize(), current, NoChanges, false, now ?? Generated.AddMinutes(50), Central,
            recorded => { asked.Add(recorded); return Task.FromResult(same); });
        return (stale, asked);
    }

    private static readonly InputIdentity Amended = Evidence with { Taf = "2026-09-06T13:57:00.0000000" };

    [Fact]
    public async Task AnAmendedTaf_SameInSubstance_IsResent()
    {
        var (stale, asked) = await DecideAsync(Amended, same: true);
        Assert.Equal(PendingStale.None, stale);
        Assert.Equal([Evidence.Taf], asked);
    }

    [Fact]
    public async Task AnAmendedTaf_DifferentInSubstance_IsStale() =>
        Assert.Equal(PendingStale.NewTaf, (await DecideAsync(Amended, same: false)).Stale);

    [Fact]
    public async Task TheSameTaf_IsNotCompared()
    {
        var (stale, asked) = await DecideAsync(Evidence, same: false);
        Assert.Equal(PendingStale.None, stale);
        Assert.Empty(asked);
    }

    [Fact]
    public async Task AnotherReasonToRegenerate_SkipsTheComparison()
    {
        var (stale, asked) = await DecideAsync(Amended with { Gfs = "2026-09-06T12:00:00.0000000Z" }, same: true);
        Assert.Equal(PendingStale.NewGfsRun, stale);
        Assert.Empty(asked);
    }

    // ApplyResentState: a re-send moves the locality's state only on the report's first delivery, and
    // never the last-Claude-call identity or the WX-182 degrade breaker.
    private static MetarParser.Data.Entities.LocalityState Before() => new()
    {
        LastScheduledSentUtc = Generated.AddHours(-6),
        LastUnscheduledSentUtc = Generated.AddHours(-5),
        LastClaudeInputHash = "newer-claude-identity",
        LastSentInputHash = "older-sent-identity",
        LastDegradedInputHash = "stuck",
        LastMetarIcao = "KEDC",
        LastRejectedGateCriteria = "precip-remove@T1(09-06 18Z)",
    };

    [Fact]
    public void AFirstDelivery_MovesTheCadenceAndTheSentIdentityOnly()
    {
        var state = Before();
        var now = Generated.AddMinutes(40);
        PendingDelivery.ApplyResentState(state, ReportKind.Scheduled, firstDelivery: true, now, "report-identity", "KAUS");
        Assert.Equal(now, state.LastScheduledSentUtc);
        Assert.Equal(Generated.AddHours(-5), state.LastUnscheduledSentUtc);
        Assert.Equal("report-identity", state.LastSentInputHash);
        Assert.Equal("newer-claude-identity", state.LastClaudeInputHash);
        Assert.Equal("stuck", state.LastDegradedInputHash);
        Assert.Equal("KAUS", state.LastMetarIcao);
        Assert.Null(state.LastRejectedGateCriteria);
    }

    [Fact]
    public void AnUpdatesFirstDelivery_MovesTheUnscheduledStamp()
    {
        var state = Before();
        var now = Generated.AddMinutes(40);
        PendingDelivery.ApplyResentState(state, ReportKind.Unscheduled, firstDelivery: true, now, "report-identity", "KAUS");
        Assert.Equal(now, state.LastUnscheduledSentUtc);
        Assert.Equal(Generated.AddHours(-6), state.LastScheduledSentUtc);
    }

    [Fact]
    public void AReSendAfterAPartialDelivery_ChangesNothing()
    {
        var state = Before();
        PendingDelivery.ApplyResentState(state, ReportKind.Unscheduled, firstDelivery: false, Generated.AddMinutes(40), "report-identity", "KAUS");
        Assert.Equivalent(Before(), state);
    }

    [Fact]
    public void TheDayPartEdgeOnASpringForwardMidnight_DoesNotThrow()
    {
        // Havana changes clocks at midnight (Cuba: 00:00 -> 01:00 on the second Sunday of March).
        var havana = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Cuba Standard Time" : "America/Havana");
        var generated = new DateTime(2026, 3, 8, 5, 30, 0, DateTimeKind.Utc);   // 01:30 local (UTC-4), just after the midnight jump: the 00-06 part, whose 00:00 does not exist
        var ex = Record.Exception(() => PendingDelivery.DayPartCrossed(
            ReportKind.Unscheduled, generated, [(generated, generated.AddHours(1))], generated.AddHours(6), havana));
        Assert.Null(ex);
    }
}