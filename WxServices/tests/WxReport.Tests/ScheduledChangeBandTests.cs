using MetarParser.Data.Entities;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

// WX-178: a SCHEDULED report carries the "What's changed" band ONLY for a newly-appearing
// near-term (local Day 1-3) severe hazard; everything else is suppressed so the band can't
// make a scheduled report read as an unscheduled update. These cover the deterministic
// decision (ReportWorker.SuppressesScheduledChangeBand) + the near-term cutoff overload on
// ForecastSnapshotBody.HasSevereEscalationOver. Locality timezone = UTC, so local day = UTC day.
public class ScheduledChangeBandTests
{
    private static readonly DateTime Now = new(2026, 6, 9, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    // Noon on the local day Now + offset (Day 1 = offset 0 = today).
    private static DateTime Day(int offset) => Now.Date.AddDays(offset).AddHours(12);

    // Start of local Day 4 — the near-term cutoff a block must start before to count.
    private static readonly DateTime Day4Start = new(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc);

    private static ForecastSnapshotBlock Blk(DateTime start, bool severe) => new()
    {
        StartUtc = start,
        SkyState = SkyState.Clear,
        Obscuration = Obscuration.None,
        TemperatureCelsius = new(20, 28),
        WindKt = new(3, 8),
        PrecipExpectation = severe ? PrecipExpectation.Likely : PrecipExpectation.None,
        PrecipPhenomenon = severe ? PrecipPhenomenon.Thunderstorm : (PrecipPhenomenon?)null,
        SevereFlag = severe,
    };

    private static ForecastSnapshotBody Snap(params ForecastSnapshotBlock[] blocks) => new() { Blocks = blocks };

    // Four calm days; the "prior" baseline.
    private static ForecastSnapshotBody Calm() =>
        Snap(Blk(Day(0), false), Blk(Day(1), false), Blk(Day(2), false), Blk(Day(3), false));

    // As Calm() but the block at local Day `severeDay` (0-based) is severe.
    private static ForecastSnapshotBody SevereOn(int severeDay) =>
        Snap(Blk(Day(0), severeDay == 0), Blk(Day(1), severeDay == 1), Blk(Day(2), severeDay == 2), Blk(Day(3), severeDay == 3));

    private static StructuredReportBody Report(bool withBand) => new()
    {
        Changes = withBand
            ? [new ReportChange { Tier = ChangeTier.Plans, Phenomenon = ChangePhenomenon.Thunderstorm, Direction = ChangeDirection.Appearing, Window = new(Now, Now.AddHours(6)), Quantities = [], SummaryToken = "ch1" }]
            : [],
        Narrative = new Dictionary<string, NarrativeSections>(StringComparer.Ordinal)
        {
            ["en"] = new() { ChangeSummary = withBand ? "{ch1}Storms appearing this afternoon." : null, Closing = "Unsettled." },
        },
    };

    // ── the deterministic decision ────────────────────────────────────────────

    [Fact]
    public void Scheduled_NonSevereChange_SuppressesBand()
    {
        // A real but non-severe change on a scheduled report → strip (the WX-178 / 2285 case).
        Assert.True(ReportWorker.SuppressesScheduledChangeBand(
            ReportKind.Scheduled, Report(withBand: true), Calm(), Calm(), Now, Utc));
    }

    [Fact]
    public void Scheduled_NearTermSevereOnset_KeepsBand()
    {
        // A not-severe→severe onset on Day 2 (within Day 1-3) → keep the band.
        Assert.False(ReportWorker.SuppressesScheduledChangeBand(
            ReportKind.Scheduled, Report(withBand: true), SevereOn(1), Calm(), Now, Utc));
    }

    [Fact]
    public void Scheduled_SevereOnsetOnDay4_SuppressesBand()
    {
        // A severe onset that only appears on Day 4 is beyond the near-term window → strip.
        Assert.True(ReportWorker.SuppressesScheduledChangeBand(
            ReportKind.Scheduled, Report(withBand: true), SevereOn(3), Calm(), Now, Utc));
    }

    [Fact]
    public void Scheduled_SevereAlreadyPresentInPrior_NotAnOnset_SuppressesBand()
    {
        // Day 2 is severe in BOTH prior and new — no escalation, so not a near-term onset → strip.
        Assert.True(ReportWorker.SuppressesScheduledChangeBand(
            ReportKind.Scheduled, Report(withBand: true), SevereOn(1), SevereOn(1), Now, Utc));
    }

    [Fact]
    public void Scheduled_NoPriorSnapshot_NeverSuppresses()
    {
        // First send: nothing to compare against (and no real band anyway) → never suppress.
        Assert.False(ReportWorker.SuppressesScheduledChangeBand(
            ReportKind.Scheduled, Report(withBand: true), Calm(), priorBody: null, Now, Utc));
    }

    [Fact]
    public void Scheduled_ReportHasNoBand_NothingToSuppress()
    {
        Assert.False(ReportWorker.SuppressesScheduledChangeBand(
            ReportKind.Scheduled, Report(withBand: false), Calm(), Calm(), Now, Utc));
    }

    [Fact]
    public void Unscheduled_NeverSuppresses_EvenWithoutSevere()
    {
        // The gate is scheduled-only; unscheduled updates keep narrating non-severe change.
        Assert.False(ReportWorker.SuppressesScheduledChangeBand(
            ReportKind.Unscheduled, Report(withBand: true), Calm(), Calm(), Now, Utc));
    }

    // ── the near-term cutoff overload ──────────────────────────────────────────

    [Fact]
    public void HasSevereEscalationOver_Cutoff_TrueWithinWindow_FalseBeyond()
    {
        Assert.True(SevereOn(1).HasSevereEscalationOver(Calm(), Day4Start));   // Day 2 severe < cutoff
        Assert.False(SevereOn(3).HasSevereEscalationOver(Calm(), Day4Start));  // Day 4 severe not < cutoff
    }

    [Fact]
    public void HasSevereEscalationOver_BlockExactlyAtCutoff_IsExcluded()
    {
        // A severe block starting exactly at the cutoff is NOT before it (strict <) → excluded.
        var atCutoff = Snap(Blk(Day4Start, true));
        Assert.False(atCutoff.HasSevereEscalationOver(Calm(), Day4Start));
    }

    [Fact]
    public void HasSevereEscalationOver_NoArg_ConsidersAllBlocks()
    {
        // The cutoff-less overload (WX-108 hysteresis) still sees a Day-4 escalation.
        Assert.True(SevereOn(3).HasSevereEscalationOver(Calm()));
    }
}