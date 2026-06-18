using System;
using System.Text.Json;

using MetarParser.Data.Entities;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

// WX-189: direct unit tests of ForecastReconciler.ValidateChangeSnapshotConsistency,
// the reject-path defense-in-depth net. The change set is now computed
// deterministically (DeterministicChangeDetector) and injected, so the validator no
// longer fires on a Claude-authored phantom in the normal flow — but it still runs on
// the injected set as a tautological backstop, and a regression in the detector would
// surface here. These build the report/snapshots directly (no HTTP round-trip) so each
// reject reason is asserted in isolation. The keystone "every detector change passes
// the validator" invariant lives in DeterministicChangeDetectorTests; this file pins
// the inverse — that genuine phantoms are caught.
public class ChangeConsistencyValidatorTests
{
    private static readonly DateTime Base = new(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    // Block at a 6-hour offset from Base; precip/severe configurable.
    private static ForecastSnapshotBlock Blk(
        double hoursFromBase = 0,
        PrecipExpectation precip = PrecipExpectation.None,
        PrecipPhenomenon? phenom = null,
        bool severe = false) => new()
        {
            StartUtc = Base.AddHours(hoursFromBase),
            SkyState = SkyState.Clear,
            Obscuration = Obscuration.None,
            TemperatureCelsius = new(10, 20),
            WindKt = new(0, 10),
            PrecipExpectation = precip,
            PrecipPhenomenon = phenom,
            SevereFlag = severe,
        };

    private static ForecastSnapshotBody Body(params ForecastSnapshotBlock[] blocks) => new() { Blocks = blocks };

    // A single-change report built directly (no Deserialize, so no narrative/anchor
    // validation runs — this exercises the consistency validator alone).
    private static StructuredReportBody Report(
        ChangeTier tier, ChangePhenomenon phenom, ChangeDirection dir,
        double startHours, double endHours) => new()
        {
            Changes =
            [
                new ReportChange
                {
                    Tier = tier,
                    Phenomenon = phenom,
                    Direction = dir,
                    Window = new ChangeWindow(Base.AddHours(startHours), Base.AddHours(endHours)),
                    Quantities = [],
                    SummaryToken = "ch1",
                },
            ],
        };

    [Fact]
    public void PhantomAppearing_PriorEqualsNew_Rejected()
    {
        // Prior and new both carry the SAME rain in-window, so "rain appearing" is a
        // change that did not occur (the send-1977 shape).
        var report = Report(ChangeTier.Plans, ChangePhenomenon.Rain, ChangeDirection.Appearing, 0, 6);
        var rain = Body(Blk(0, PrecipExpectation.Likely, PrecipPhenomenon.Rain));
        Assert.ThrowsAny<JsonException>(() =>
            ForecastReconciler.ValidateChangeSnapshotConsistency(report, rain, rain, Utc));
    }

    [Fact]
    public void GenuineAppearing_DryToRain_Accepted()
    {
        // Prior dry in-window, new carries rain — a real onset, no throw.
        var report = Report(ChangeTier.Plans, ChangePhenomenon.Rain, ChangeDirection.Appearing, 0, 6);
        var dry = Body(Blk(0));
        var rain = Body(Blk(0, PrecipExpectation.Likely, PrecipPhenomenon.Rain));
        ForecastReconciler.ValidateChangeSnapshotConsistency(report, rain, dry, Utc);
    }

    [Fact]
    public void PhantomWeakening_PriorCoversAndUnchanged_Rejected()
    {
        // Prior fully covers the window and carries the SAME rain as new, so a
        // "rain weakening" is a change that did not occur.
        var report = Report(ChangeTier.Plans, ChangePhenomenon.Rain, ChangeDirection.Weakening, 0, 6);
        var rain = Body(Blk(0, PrecipExpectation.Likely, PrecipPhenomenon.Rain));
        Assert.ThrowsAny<JsonException>(() =>
            ForecastReconciler.ValidateChangeSnapshotConsistency(report, rain, rain, Utc));
    }

    [Fact]
    public void OffGridWindow_NotABlockBoundary_Rejected()
    {
        // A window starting at +2h is off the local 00/06/12/18 day-part grid, so the
        // narrative would claim timing finer than the blocks support.
        var report = Report(ChangeTier.Plans, ChangePhenomenon.Rain, ChangeDirection.Appearing, 2, 8);
        var dry = Body(Blk(0));
        var rain = Body(Blk(0, PrecipExpectation.Likely, PrecipPhenomenon.Rain));
        Assert.ThrowsAny<JsonException>(() =>
            ForecastReconciler.ValidateChangeSnapshotConsistency(report, rain, dry, Utc));
    }

    [Fact]
    public void SafetyTier_NoBackingSignal_Rejected()
    {
        // "rain appearing" is a real onset (prior dry → new rain), but it is emitted at
        // the safety tier with no block carrying a safety-grade signal (no severeFlag,
        // freezing/snow precip, or sustained wind >= 34 kt) — the tier is over-escalated.
        var report = Report(ChangeTier.Safety, ChangePhenomenon.Rain, ChangeDirection.Appearing, 0, 6);
        var dry = Body(Blk(0));
        var rain = Body(Blk(0, PrecipExpectation.Possible, PrecipPhenomenon.Rain));
        Assert.ThrowsAny<JsonException>(() =>
            ForecastReconciler.ValidateChangeSnapshotConsistency(report, rain, dry, Utc));
    }
}