using System;

using MetarParser.Data.Entities;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

// WX-114: the deterministic significance gate (cost pre-filter). Verifies each
// criterion row, the onset-eager / cessation-lazy tier asymmetry, the safety-floor
// always-pass rows, the directional freeze/thaw 32 °F dead band, and that a block or
// day that merely rolled into the horizon is not news by itself.
public class SignificanceGateTests
{
    private static readonly DateTime Now = new(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly SignificanceGateConfig Cfg = new();

    // Block at a given horizon offset, temperatures expressed in °F for readability.
    private static ForecastSnapshotBlock Blk(
        double hoursFromNow = 0,
        double loF = 50, double hiF = 60,
        int windMax = 10,
        PrecipExpectation precip = PrecipExpectation.None,
        PrecipPhenomenon? phenom = null,
        bool severe = false) => new()
        {
            StartUtc = Now.AddHours(hoursFromNow),
            SkyState = SkyState.Clear,
            Obscuration = Obscuration.None,
            TemperatureCelsius = new(FtoC(loF), FtoC(hiF)),
            WindKt = new(0, windMax),
            PrecipExpectation = precip,
            PrecipPhenomenon = phenom,
            SevereFlag = severe,
        };

    private static ForecastSnapshotBody Body(params ForecastSnapshotBlock[] blocks) => new() { Blocks = blocks };

    private static bool Sig(ForecastSnapshotBody prior, ForecastSnapshotBody current) =>
        SignificanceGate.Evaluate(prior, current, Cfg, Now, Utc, freshTafSinceLastSend: false).Significant;

    private static double FtoC(double f) => (f - 32.0) * 5.0 / 9.0;

    [Fact]
    public void Identical_NotSignificant()
    {
        Assert.False(Sig(Body(Blk()), Body(Blk())));
    }

    [Fact]
    public void TempDelta_BelowThreshold_NotSignificant()
    {
        // T1 threshold is 5 °F; a 4 °F high move stays below it.
        Assert.False(Sig(Body(Blk(hiF: 60)), Body(Blk(hiF: 64))));
    }

    [Fact]
    public void TempDelta_AtThreshold_Significant()
    {
        Assert.True(Sig(Body(Blk(hiF: 60)), Body(Blk(hiF: 65))));
    }

    [Fact]
    public void TempDelta_LoosensWithHorizon()
    {
        // A 6 °F move at T4 (threshold 12) is not significant...
        Assert.False(Sig(Body(Blk(hoursFromNow: 90, hiF: 60)), Body(Blk(hoursFromNow: 90, hiF: 66))));
        // ...but the same move at T1 (threshold 5) is.
        Assert.True(Sig(Body(Blk(hoursFromNow: 0, hiF: 60)), Body(Blk(hoursFromNow: 0, hiF: 66))));
    }

    [Fact]
    public void Freeze_Add_FallingBelow32_Significant()
    {
        // Low 33 → 31 °F: a freeze appears (and only a 2 °F move, below the delta threshold).
        Assert.True(Sig(Body(Blk(loF: 33, hiF: 50)), Body(Blk(loF: 31, hiF: 50))));
    }

    [Fact]
    public void Freeze_FallingToExactly32_NotYetFrozen()
    {
        // 34 → 32 °F falling: 32 is still the dead band, no freeze; 2 °F move is sub-threshold.
        Assert.False(Sig(Body(Blk(loF: 34, hiF: 50)), Body(Blk(loF: 32, hiF: 50))));
    }

    [Fact]
    public void Thaw_RisingAbove32_Significant()
    {
        // Low 30 → 33 °F: a thaw (3 °F move, sub-threshold, so it must be the thaw firing).
        Assert.True(Sig(Body(Blk(loF: 30, hiF: 50)), Body(Blk(loF: 33, hiF: 50))));
    }

    [Fact]
    public void Thaw_RisingToExactly32_StillFrozen()
    {
        // 30 → 32 °F rising: 32 is still frozen, no thaw; 2 °F move is sub-threshold.
        Assert.False(Sig(Body(Blk(loF: 30, hiF: 50)), Body(Blk(loF: 32, hiF: 50))));
    }

    [Fact]
    public void Heat_CrossingAdvisoryLine_Significant()
    {
        // High 98 → 101 °F crosses the 100 °F line (3 °F move, sub-threshold).
        Assert.True(Sig(Body(Blk(hiF: 98, loF: 80)), Body(Blk(hiF: 101, loF: 80))));
    }

    [Fact]
    public void PrecipOccurrence_Add_Significant_AtAllTiers()
    {
        // Dry → wet at T4: ADD precip fires at every tier.
        var prior = Body(Blk(hoursFromNow: 90));
        var current = Body(Blk(hoursFromNow: 90, precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Rain));
        Assert.True(Sig(prior, current));
    }

    [Fact]
    public void PrecipOccurrence_Remove_NearTermOnly()
    {
        var wet = PrecipExpectation.Likely;
        // T1 (now): wet → dry is significant.
        Assert.True(Sig(
            Body(Blk(hoursFromNow: 0, precip: wet, phenom: PrecipPhenomenon.Rain)),
            Body(Blk(hoursFromNow: 0))));
        // T3 (+54h): wet → dry is not (REMOVE is near-term only).
        Assert.False(Sig(
            Body(Blk(hoursFromNow: 54, precip: wet, phenom: PrecipPhenomenon.Rain)),
            Body(Blk(hoursFromNow: 54))));
    }

    [Fact]
    public void FrozenPrecip_Add_Significant_AtFarHorizon()
    {
        // Rain → Snow at T4: frozen ADD is a safety floor, fires at every tier.
        var prior = Body(Blk(hoursFromNow: 90, precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Rain));
        var current = Body(Blk(hoursFromNow: 90, precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Snow));
        Assert.True(Sig(prior, current));
    }

    [Fact]
    public void Mixed_CountsAsFrozen()
    {
        // Rain → Mixed (rain/snow) trips the safety-floor frozen onset.
        Assert.True(Sig(
            Body(Blk(precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Rain)),
            Body(Blk(precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Mixed))));
        // Snow → Mixed is neither onset nor downgrade — both are frozen.
        Assert.False(Sig(
            Body(Blk(precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Snow)),
            Body(Blk(precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Mixed))));
    }

    [Fact]
    public void Severe_Add_Significant_AtFarHorizon()
    {
        // Severe onset is a safety floor at every tier (T4 here).
        Assert.True(Sig(
            Body(Blk(hoursFromNow: 90)),
            Body(Blk(hoursFromNow: 90, severe: true))));
    }

    [Fact]
    public void Severe_Remove_NearTerm_Significant_FarTerm_InfoOnly()
    {
        // T1: severe clearing is significant.
        Assert.True(Sig(
            Body(Blk(hoursFromNow: 0, severe: true)),
            Body(Blk(hoursFromNow: 0, severe: false))));
        // T4: severe clearing is info-only and does not gate.
        Assert.False(Sig(
            Body(Blk(hoursFromNow: 90, severe: true)),
            Body(Blk(hoursFromNow: 90, severe: false))));
    }

    [Fact]
    public void Wind_ReachesAdvisory_Significant()
    {
        // Max 20 → 26 kt crosses the 25 kt advisory line (6 kt move, below the 12 kt T1 delta).
        Assert.True(Sig(Body(Blk(windMax: 20)), Body(Blk(windMax: 26))));
    }

    [Fact]
    public void WindDelta_BelowThreshold_NotSignificant()
    {
        // 11 kt move, both below advisory: under the 12 kt T1 threshold.
        Assert.False(Sig(Body(Blk(windMax: 10)), Body(Blk(windMax: 21))));
    }

    [Fact]
    public void WindDelta_AtThreshold_Significant()
    {
        // 12 kt move at T1, both below advisory.
        Assert.True(Sig(Body(Blk(windMax: 8)), Body(Blk(windMax: 20))));
    }

    [Fact]
    public void RolledInBlock_NotNewsByItself()
    {
        // Prior and current share an identical near block; current has an extra far
        // block the prior never carried (it rolled into the horizon). That alone is not news.
        var near = Blk(hoursFromNow: 0);
        var prior = Body(near);
        var current = Body(near, Blk(hoursFromNow: 90, severe: true));
        Assert.False(Sig(prior, current));
    }

    [Fact]
    public void BeyondHorizon_Ignored()
    {
        // A change beyond 120h (day 6, narrative-only) does not gate.
        var prior = Body(Blk(hoursFromNow: 130, hiF: 60));
        var current = Body(Blk(hoursFromNow: 130, hiF: 90));
        Assert.False(Sig(prior, current));
    }

    [Fact]
    public void DisjointHorizon_EmptyPrior_Significant()
    {
        // Nothing to compare against (empty prior): must call Claude, never suppress.
        Assert.True(Sig(Body(), Body(Blk())));
    }

    [Fact]
    public void TierBoundary_24h_FallsInLooserTier()
    {
        // A block at exactly 24h is T2 (threshold 7 °F), so a 6 °F move does not fire...
        Assert.False(Sig(Body(Blk(hoursFromNow: 24, hiF: 60)), Body(Blk(hoursFromNow: 24, hiF: 66))));
        // ...while at 23h it is still T1 (threshold 5 °F) and does.
        Assert.True(Sig(Body(Blk(hoursFromNow: 23, hiF: 60)), Body(Blk(hoursFromNow: 23, hiF: 66))));
    }

    [Fact]
    public void MultiBlockDay_BlockMoveNotChangingDailyHiLo_NotSignificant()
    {
        // The day's high is set by the warmer second block; nudging the cooler block's
        // high by 5 °F leaves the daily high (and low) unchanged — so it is not news.
        // This proves temperature is judged on the daily aggregate, not per block.
        var prior = Body(Blk(hoursFromNow: 0, loF: 40, hiF: 50), Blk(hoursFromNow: 6, loF: 40, hiF: 60));
        var current = Body(Blk(hoursFromNow: 0, loF: 40, hiF: 55), Blk(hoursFromNow: 6, loF: 40, hiF: 60));
        Assert.False(Sig(prior, current));
    }

    [Fact]
    public void FrozenDowngrade_NearTermOnly()
    {
        // T1: snow → rain is a significant downgrade.
        Assert.True(Sig(
            Body(Blk(hoursFromNow: 0, precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Snow)),
            Body(Blk(hoursFromNow: 0, precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Rain))));
        // T3: the same downgrade is not (cessations are near-term only).
        Assert.False(Sig(
            Body(Blk(hoursFromNow: 54, precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Snow)),
            Body(Blk(hoursFromNow: 54, precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Rain))));
    }

    [Fact]
    public void WindAdvisory_Remove_NearTermOnly()
    {
        // T1: dropping below the 25 kt advisory is significant.
        Assert.True(Sig(Body(Blk(hoursFromNow: 0, windMax: 30)), Body(Blk(hoursFromNow: 0, windMax: 20))));
        // T3: the same drop is not (cessation near-term only; the 10 kt delta is below the 18 kt T3 threshold).
        Assert.False(Sig(Body(Blk(hoursFromNow: 54, windMax: 30)), Body(Blk(hoursFromNow: 54, windMax: 20))));
    }

    [Fact]
    public void NonUtcTimezone_GroupsByLocalDay_FreezeStillDetected()
    {
        // Exercises the local-day grouping with a non-UTC (fixed -6h) offset.
        var minus6 = TimeZoneInfo.CreateCustomTimeZone("utc-minus-6", TimeSpan.FromHours(-6), "UTC-6", "UTC-6");
        var prior = Body(Blk(loF: 35, hiF: 45));
        var current = Body(Blk(loF: 30, hiF: 45));
        Assert.True(SignificanceGate.Evaluate(prior, current, Cfg, Now, minus6, freshTafSinceLastSend: false).Significant);
    }

    // WX-114: the provisional body is GFS-only, so a fresh/amended TAF is a change the
    // gate cannot see in the bodies. A fresh TAF must therefore force significance
    // (route to Claude) even when the GFS-derived bodies are byte-identical, so a
    // real short-term update is never suppressed.
    [Fact]
    public void FreshTaf_ForcesSignificant_EvenWhenBodiesIdentical()
    {
        var result = SignificanceGate.Evaluate(Body(Blk()), Body(Blk()), Cfg, Now, Utc, freshTafSinceLastSend: true);
        Assert.True(result.Significant);
        Assert.Contains("taf-fresh", result.FiredCriteria);
    }

    [Fact]
    public void NoFreshTaf_IdenticalBodies_RemainsNotSignificant()
    {
        // The exemption is scoped to fresh-TAF cycles only: without one, identical
        // bodies still suppress (the 94-case GFS/METAR win is preserved).
        var result = SignificanceGate.Evaluate(Body(Blk()), Body(Blk()), Cfg, Now, Utc, freshTafSinceLastSend: false);
        Assert.False(result.Significant);
    }
}