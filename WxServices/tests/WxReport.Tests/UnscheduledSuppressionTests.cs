using MetarParser.Data.Entities;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

// WX-108: the deterministic post-reconciliation backstop decision.
// Grounded in the 2026-06-02 KDWH overnight incident: row 348 (severeFlag true)
// → row 363 (severeFlag false) shared the same GFS run AND the same TAF issuance —
// only the METAR advanced. That severe flip must be suppressed; a flip backed by
// fresh model/TAF guidance, and genuinely new weather, must still send.
public class UnscheduledSuppressionTests
{
    private static readonly System.DateTime Anchor = new(2026, 6, 2, 0, 0, 0, System.DateTimeKind.Utc);

    private static ForecastSnapshotBlock Block(
        PrecipExpectation precip = PrecipExpectation.Likely,
        PrecipPhenomenon? phenom = PrecipPhenomenon.Rain,
        bool severe = false,
        double tMax = 14.0,
        int wMax = 12,
        System.DateTime? startUtc = null) => new()
        {
            StartUtc = startUtc ?? Anchor,
            SkyState = SkyState.Overcast,
            Obscuration = Obscuration.None,
            TemperatureCelsius = new(8.0, tMax),
            WindKt = new(5, wMax),
            PrecipExpectation = precip,
            PrecipPhenomenon = phenom,
            SevereFlag = severe,
        };

    private static ForecastSnapshotBody Body(params ForecastSnapshotBlock[] blocks) => new() { Blocks = blocks };

    [Fact]
    public void Identical_IsRedundant()
    {
        var d = ReportWorker.EvaluateUnscheduledSuppression(
            Body(Block()), Body(Block()), freshGuidanceSinceLastSend: false);
        Assert.Equal(ReportWorker.UnscheduledSuppression.Redundant, d);
    }

    [Fact]
    public void Redundant_SuppressedEvenWithFreshGuidance()
    {
        // A fresh TAF arrived but reconciled to the same forecast — still redundant.
        var d = ReportWorker.EvaluateUnscheduledSuppression(
            Body(Block()), Body(Block()), freshGuidanceSinceLastSend: true);
        Assert.Equal(ReportWorker.UnscheduledSuppression.Redundant, d);
    }

    [Fact]
    public void SevereDeEscalation_ObservationOnly_IsSevereFlip()
    {
        // The 348→363 case: severe drops (true→false), nothing else changes, no
        // fresh guidance — the untrusted whipsaw. Suppress.
        var d = ReportWorker.EvaluateUnscheduledSuppression(
            Body(Block(severe: true)), Body(Block(severe: false)), freshGuidanceSinceLastSend: false);
        Assert.Equal(ReportWorker.UnscheduledSuppression.SevereFlip, d);
    }

    [Fact]
    public void SevereEscalation_ObservationOnly_Sends()
    {
        // Directional asymmetry: a severe hazard APPEARING (false→true) is news even
        // on a bare observation — never suppressed, regardless of fresh guidance.
        var d = ReportWorker.EvaluateUnscheduledSuppression(
            Body(Block(severe: false)), Body(Block(severe: true)), freshGuidanceSinceLastSend: false);
        Assert.Equal(ReportWorker.UnscheduledSuppression.None, d);
    }

    [Fact]
    public void SevereWindowMoves_ObservationOnly_Sends()
    {
        // Severe risk shifts from the first block to the second (a new severe block
        // appears at +6h). The escalation on the later block must still send.
        var later = Anchor.AddHours(6);
        var prior = Body(Block(severe: true), Block(severe: false, startUtc: later));
        var moved = Body(Block(severe: false), Block(severe: true, startUtc: later));
        var d = ReportWorker.EvaluateUnscheduledSuppression(prior, moved, freshGuidanceSinceLastSend: false);
        Assert.Equal(ReportWorker.UnscheduledSuppression.None, d);
    }

    [Fact]
    public void SevereDeEscalation_WithFreshGuidance_Sends()
    {
        // A de-escalation backed by a newer GFS run / TAF is trusted — send it.
        var d = ReportWorker.EvaluateUnscheduledSuppression(
            Body(Block(severe: true)), Body(Block(severe: false)), freshGuidanceSinceLastSend: true);
        Assert.Equal(ReportWorker.UnscheduledSuppression.None, d);
    }

    [Fact]
    public void RainArrivesWhereDryWasPromised_Sends()
    {
        // Positive control: precip tier changes (dry → likely rain) — genuine news,
        // even on an observation-only advance.
        var dry = Body(Block(precip: PrecipExpectation.None, phenom: null));
        var wet = Body(Block(precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Rain));
        var d = ReportWorker.EvaluateUnscheduledSuppression(dry, wet, freshGuidanceSinceLastSend: false);
        Assert.Equal(ReportWorker.UnscheduledSuppression.None, d);
    }
}