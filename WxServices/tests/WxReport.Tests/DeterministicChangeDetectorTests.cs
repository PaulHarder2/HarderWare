using System;
using System.Collections.Generic;
using System.Linq;

using MetarParser.Data.Entities;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

// WX-189: the deterministic change detector — the generative inverse of
// ForecastReconciler.ValidateChangeSnapshotConsistency. Verifies each phenomenon ×
// direction, the temperature/wind thresholds borrowed from the significance gate, the
// severe de-dup, windowing, salience ranking, and the keystone invariant: every change
// the detector emits passes the consistency validator (so the defense-in-depth net
// stays tautologically green).
public class DeterministicChangeDetectorTests
{
    private static readonly DateTime Now = new(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly SignificanceGateConfig Cfg = new();

    // Block at a given horizon offset; temperatures expressed in °F for readability.
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

    private static IReadOnlyList<ReportChange> Detect(ForecastSnapshotBody prior, ForecastSnapshotBody final) =>
        DeterministicChangeDetector.Detect(prior, final, Cfg, Now, Utc);

    private static double FtoC(double f) => (f - 32.0) * 5.0 / 9.0;

    // ── first send / no change ────────────────────────────────────────────────

    [Fact]
    public void FirstSend_NullPrior_NoChanges() =>
        Assert.Empty(DeterministicChangeDetector.Detect(null, Body(Blk(precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Rain)), Cfg, Now, Utc));

    [Fact]
    public void Identical_NoChanges() =>
        Assert.Empty(Detect(Body(Blk()), Body(Blk())));

    // ── precipitation (inverse of the oracle) ─────────────────────────────────

    [Fact]
    public void Rain_Appearing()
    {
        var c = Assert.Single(Detect(
            Body(Blk()),
            Body(Blk(precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Rain))));
        Assert.Equal(ChangePhenomenon.Rain, c.Phenomenon);
        Assert.Equal(ChangeDirection.Appearing, c.Direction);
        Assert.Equal("ch1", c.SummaryToken);
    }

    [Fact]
    public void Rain_PossibleToLikely_IsNotAChange()
    {
        // WX-284 step 2 (Niki): "possible" and "likely" read as the same tier to the recipient, so a
        // possible<->likely move is NOT news — it must not surface a Strengthening/Weakening that
        // would warrant an unscheduled update. Both directions fold flat.
        Assert.Empty(Detect(
            Body(Blk(precip: PrecipExpectation.Possible, phenom: PrecipPhenomenon.Rain)),
            Body(Blk(precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Rain))));
        Assert.Empty(Detect(
            Body(Blk(precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Rain)),
            Body(Blk(precip: PrecipExpectation.Possible, phenom: PrecipPhenomenon.Rain))));
    }

    [Fact]
    public void Rain_Strengthening_OnExpectationStep()
    {
        // A genuine step above the merged possible/likely tier — likely -> "expected" (Certain) —
        // is still a real strengthening (the "expected" register survives the WX-284 collapse).
        var c = Assert.Single(Detect(
            Body(Blk(precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Rain)),
            Body(Blk(precip: PrecipExpectation.Certain, phenom: PrecipPhenomenon.Rain))));
        Assert.Equal(ChangeDirection.Strengthening, c.Direction);
    }

    [Fact]
    public void Rain_Weakening_AndClearing()
    {
        var weaken = Assert.Single(Detect(
            Body(Blk(precip: PrecipExpectation.Certain, phenom: PrecipPhenomenon.Rain)),
            Body(Blk(precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Rain))));
        Assert.Equal(ChangeDirection.Weakening, weaken.Direction);

        var clear = Assert.Single(Detect(
            Body(Blk(precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Rain)),
            Body(Blk())));
        Assert.Equal(ChangeDirection.Clearing, clear.Direction);
    }

    [Fact]
    public void FlatExpectation_SevereRise_IsStrengthening()
    {
        // Likely snow both sides, but severeFlag false→true: strengthening. Snow does NOT fold to
        // rain (WX-284 collapses only the convective gradient), so it keeps its own axis — this
        // preserves the WX-148 worked example that the oracle's per-phenomenon severe-strength axis
        // relies on. (The thunderstorm form of this case now collapses — see
        // NonSevereThunderstorm_UpgradesToSevere_SevereStormsAppear_RainEnds.)
        var c = Assert.Single(Detect(
            Body(Blk(precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Snow)),
            Body(Blk(precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Snow, severe: true))));
        Assert.Equal(ChangePhenomenon.Snow, c.Phenomenon);
        Assert.Equal(ChangeDirection.Strengthening, c.Direction);
        Assert.Equal(ChangeTier.Safety, c.Tier);
    }

    [Fact]
    public void Severe_TierBump_WhileSevere_IsNotAChange()
    {
        // WX-284 step 2 (Paul): a severe hazard's recipient wording is the constant "severe storms
        // possible" — never "likely"/"expected" — so an internal expectation bump (likely -> certain) on
        // a block that STAYS severe is not a change the reader sees. RecipientPrecip.Expectation pins a
        // severe block to the top of the ladder, so only the SevereFlag flip (onset/clearing) drives a
        // severe change; the tier bump reads flat. (Contrast FlatExpectation_SevereRise: a severe ONSET
        // — false -> true — is still a strengthening.)
        Assert.Empty(Detect(
            Body(Blk(precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Thunderstorm, severe: true)),
            Body(Blk(precip: PrecipExpectation.Certain, phenom: PrecipPhenomenon.Thunderstorm, severe: true))));
    }

    [Fact]
    public void NonSevereThunderstorm_UpgradesToSevere_IsSevereStormsOnly_NoRainClearing()
    {
        // WX-284 (Paul): a non-severe thunderstorm reads as ordinary "rain"; upgrading it to SEVERE
        // is NOT "rain clearing" — the rain did not go away, it escalated to "severe storms". The
        // convective precip crossing the severe line is narrated ONCE, on the Thunderstorm(severe)
        // axis; the phantom Rain change is suppressed. (Contrast rain→snow, a genuine TYPE change,
        // which still splits into rain-clearing + snow-appearing.)
        var changes = Detect(
            Body(Blk(precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Thunderstorm)),
            Body(Blk(precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Thunderstorm, severe: true)));
        var storms = Assert.Single(changes);
        Assert.Equal(ChangePhenomenon.Thunderstorm, storms.Phenomenon);
        Assert.Equal(ChangeTier.Safety, storms.Tier);
        Assert.DoesNotContain(changes, c => c.Phenomenon == ChangePhenomenon.Rain);
    }

    [Fact]
    public void SevereStorms_DeEscalateToRain_IsSevereStormsClearing_NoRainAppearing()
    {
        // WX-284 symmetry: severe storms easing back to ordinary rain is "severe storms" clearing on
        // the Thunderstorm axis, NOT "rain appearing" — the recipient is not told rain arrived when
        // their severe storm merely de-escalated. The phantom Rain change is suppressed both ways.
        var changes = Detect(
            Body(Blk(precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Thunderstorm, severe: true)),
            Body(Blk(precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Thunderstorm)));
        var storms = Assert.Single(changes);
        Assert.Equal(ChangePhenomenon.Thunderstorm, storms.Phenomenon);
        Assert.DoesNotContain(changes, c => c.Phenomenon == ChangePhenomenon.Rain);
    }

    // ── severe de-dup ─────────────────────────────────────────────────────────

    [Fact]
    public void SevereThunderstorm_FoldsIntoThunderstorm_NoStandaloneSevere()
    {
        var changes = Detect(
            Body(Blk()),
            Body(Blk(precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Thunderstorm, severe: true)));
        Assert.Single(changes);
        Assert.Equal(ChangePhenomenon.Thunderstorm, changes[0].Phenomenon);
        Assert.DoesNotContain(changes, c => c.Phenomenon == ChangePhenomenon.Severe);
    }

    [Fact]
    public void NonConvectiveSevere_IsStandaloneSevere()
    {
        // A ≥50 kt wind event: severeFlag, no precip phenomenon.
        var c = Assert.Single(Detect(
            Body(Blk(windMax: 20)),
            Body(Blk(windMax: 55, severe: true))).Where(c => c.Phenomenon == ChangePhenomenon.Severe));
        Assert.Equal(ChangeDirection.Appearing, c.Direction);
        Assert.Equal(ChangeTier.Safety, c.Tier);
    }

    // ── temperature (gate thresholds) ─────────────────────────────────────────

    [Fact]
    public void Freeze_Add_IsTemperatureAppearingSafety()
    {
        var c = Assert.Single(Detect(
            Body(Blk(loF: 33, hiF: 45)),
            Body(Blk(loF: 31, hiF: 45))));
        Assert.Equal(ChangePhenomenon.Temperature, c.Phenomenon);
        Assert.Equal(ChangeDirection.Appearing, c.Direction);
        Assert.Equal(ChangeTier.Safety, c.Tier);
    }

    [Fact]
    public void Thaw_IsTemperatureClearingPlans()
    {
        var c = Assert.Single(Detect(
            Body(Blk(loF: 30, hiF: 45)),
            Body(Blk(loF: 34, hiF: 45))));
        Assert.Equal(ChangePhenomenon.Temperature, c.Phenomenon);
        Assert.Equal(ChangeDirection.Clearing, c.Direction);
        Assert.Equal(ChangeTier.Plans, c.Tier);
    }

    [Fact]
    public void TempMagnitude_Warming_Strengthening_Cooling_Weakening()
    {
        var warm = Assert.Single(Detect(Body(Blk(hiF: 60)), Body(Blk(hiF: 70))));
        Assert.Equal(ChangePhenomenon.Temperature, warm.Phenomenon);
        Assert.Equal(ChangeDirection.Strengthening, warm.Direction);

        var cool = Assert.Single(Detect(Body(Blk(hiF: 70)), Body(Blk(hiF: 60))));
        Assert.Equal(ChangeDirection.Weakening, cool.Direction);
    }

    [Fact]
    public void TempMagnitude_SubThreshold_NoChange() =>
        Assert.Empty(Detect(Body(Blk(hiF: 60)), Body(Blk(hiF: 63)))); // T1 threshold is 5 °F

    [Fact]
    public void Temperature_OneChangePerDay_FreezeBeatsMagnitude()
    {
        // Low falls through freezing AND the high moves a lot: still one Temperature change, the freeze (Safety).
        var c = Assert.Single(Detect(
            Body(Blk(loF: 35, hiF: 50)),
            Body(Blk(loF: 28, hiF: 65))));
        Assert.Equal(ChangePhenomenon.Temperature, c.Phenomenon);
        Assert.Equal(ChangeTier.Safety, c.Tier);
        Assert.Equal(ChangeDirection.Appearing, c.Direction);
    }

    // ── wind (gate thresholds) ────────────────────────────────────────────────

    [Fact]
    public void Wind_AdvisoryAdd_Strengthening()
    {
        var c = Assert.Single(Detect(Body(Blk(windMax: 10)), Body(Blk(windMax: 28)))); // advisory line 25 kt
        Assert.Equal(ChangePhenomenon.Wind, c.Phenomenon);
        Assert.Equal(ChangeDirection.Strengthening, c.Direction);
    }

    [Fact]
    public void Wind_AtSafetyFloor_IsSafetyTier()
    {
        var c = Assert.Single(Detect(Body(Blk(windMax: 10)), Body(Blk(windMax: 36)))); // ≥34 kt
        Assert.Equal(ChangeTier.Safety, c.Tier);
    }

    [Fact]
    public void Wind_SubThreshold_NoChange() =>
        Assert.Empty(Detect(Body(Blk(windMax: 10)), Body(Blk(windMax: 18)))); // T1 delta 12 kt, no advisory cross

    // ── windowing ─────────────────────────────────────────────────────────────

    [Fact]
    public void Windowing_ConsecutiveSameDirection_MergeToOneWindow()
    {
        var prior = Body(Blk(0), Blk(6), Blk(12));
        var final = Body(
            Blk(0, precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Rain),
            Blk(6, precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Rain),
            Blk(12, precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Rain));
        var c = Assert.Single(Detect(prior, final));
        Assert.Equal(Now, c.Window.StartUtc);
        Assert.Equal(Now.AddHours(18), c.Window.EndUtc); // 3 blocks × 6 h
    }

    [Fact]
    public void Windowing_GapSplitsIntoTwoChanges()
    {
        var prior = Body(Blk(0), Blk(6), Blk(12));
        var final = Body(
            Blk(0, precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Rain),
            Blk(6), // dry gap
            Blk(12, precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Rain));
        Assert.Equal(2, Detect(prior, final).Count(c => c.Phenomenon == ChangePhenomenon.Rain));
    }

    // ── ranking + tokens ──────────────────────────────────────────────────────

    [Fact]
    public void Ranking_SafetyBeforeAmbient_TokensInOrder()
    {
        // A far-horizon ambient rain onset (block 4 = 72 h+, Ambient) and a near severe onset.
        var prior = Body(Blk(0), Blk(90));
        var final = Body(
            Blk(0, precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Thunderstorm, severe: true),
            Blk(90, precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Rain));
        var changes = Detect(prior, final);
        Assert.Equal(ChangeTier.Safety, changes[0].Tier);
        Assert.Equal("ch1", changes[0].SummaryToken);
        Assert.Equal("ch2", changes[1].SummaryToken);
        Assert.True((int)changes[0].Tier <= (int)changes[1].Tier);
    }

    // ── horizon ───────────────────────────────────────────────────────────────

    [Fact]
    public void Horizon_BeyondLastTier_Excluded() =>
        // A block at 130 h (past the 120 h horizon) with new rain is not detected.
        Assert.Empty(Detect(Body(Blk(130)), Body(Blk(130, precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Rain))));

    // ── /code-review fixes ────────────────────────────────────────────────────

    [Fact]
    public void RolledInBlock_NoPriorCounterpart_IsNotNews()
    {
        // A block that only rolled into the horizon since the last send (no prior
        // counterpart) is not news by itself (WX-108 horizon-edge convention) — precip
        // and severe must skip it just like the gate and the temp/wind arms do.
        var prior = Body(Blk(0));
        var final = Body(Blk(0), Blk(6, precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Rain));
        Assert.Empty(Detect(prior, final));
    }

    [Fact]
    public void Wind_Run_ReportsPeak_NotLastBlock()
    {
        var prior = Body(Blk(0, windMax: 10), Blk(6, windMax: 10), Blk(12, windMax: 10));
        var final = Body(Blk(0, windMax: 28), Blk(6, windMax: 40), Blk(12, windMax: 30));
        var c = Assert.Single(Detect(prior, final));
        Assert.Equal(ChangePhenomenon.Wind, c.Phenomenon);
        var wind = Assert.Single(c.Quantities, q => q.Kind == QuantityKind.Wind);
        Assert.Equal(40, wind.Value); // the run's peak, not the trailing 30 kt
    }

    [Fact]
    public void ClearingSnow_KeepsSafetyTier_FromPriorBlock()
    {
        // A clearing frozen-precip hazard must not de-escalate below Safety just because
        // the (cleared) final block no longer carries snow — WX-81 counts a removed hazard
        // as safety news at any horizon.
        var c = Assert.Single(Detect(
            Body(Blk(precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Snow)),
            Body(Blk())));
        Assert.Equal(ChangePhenomenon.Snow, c.Phenomenon);
        Assert.Equal(ChangeDirection.Clearing, c.Direction);
        Assert.Equal(ChangeTier.Safety, c.Tier);
    }

    // ── the keystone invariant ────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(InvariantCases))]
    public void EveryEmittedChange_PassesTheConsistencyValidator(ForecastSnapshotBody prior, ForecastSnapshotBody final)
    {
        var changes = Detect(prior, final);
        var report = new StructuredReportBody { Changes = changes };
        // The validator is the detector's inverse — it must never reject the detector's output.
        ForecastReconciler.ValidateChangeSnapshotConsistency(report, final, prior, Utc);
    }

    [Fact]
    public void Strengthening_FromInteriorBlockRise_IsEmittedAndPassesConsistency_WX204()
    {
        // WX-204 regression: the per-block consistency check must SEE a real interior change AND accept
        // it, where the old window-max aggregate false-rejected it as a phantom (the prod degrade).
        // WX-284 step 2 reshapes this scenario: block0 stays severe on BOTH sides, so its Likely->Certain
        // tier bump is now pinned flat — a severe block's wording is the constant "severe storms possible",
        // never likely/expected, so an internal tier bump is not a reader-visible change (see
        // Severe_TierBump_WhileSevere_IsNotAChange). The only real change left is block6 going non-severe
        // -> severe: a severe-storms ONSET in the 06-12 window. The detector must emit that onset AND the
        // per-block oracle must accept it (the aggregate severe/expectation on the Thunderstorm axis is
        // backed per-block, not masked).
        var prior = Body(
            Blk(0, precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Thunderstorm, severe: true),
            Blk(6, precip: PrecipExpectation.Certain, phenom: PrecipPhenomenon.Thunderstorm, severe: false));
        var final = Body(
            Blk(0, precip: PrecipExpectation.Certain, phenom: PrecipPhenomenon.Thunderstorm, severe: true),
            Blk(6, precip: PrecipExpectation.Certain, phenom: PrecipPhenomenon.Thunderstorm, severe: true));

        var changes = Detect(prior, final);
        // block0 is flat (severe both sides, tier pinned); block6 is a severe-storms onset.
        Assert.Contains(changes, c => c.Phenomenon == ChangePhenomenon.Thunderstorm && c.Direction == ChangeDirection.Appearing);
        Assert.DoesNotContain(changes, c => c.Phenomenon == ChangePhenomenon.Thunderstorm && c.Direction == ChangeDirection.Strengthening);

        // Must not throw — the per-block oracle backs the severe onset (pre-WX-204 this raised ChangeConsistencyException).
        var report = new StructuredReportBody { Changes = changes };
        ForecastReconciler.ValidateChangeSnapshotConsistency(report, final, prior, Utc);
    }

    [Fact]
    public void RainAppearing_OverPriorSnowBlock_IsNotSafetyTier_AndPassesConsistency_WX207()
    {
        // WX-207 regression: a block going Snow->Rain. The detector emits a Rain APPEARING and a
        // Snow CLEARING. The appearing rain is NOT a safety event — only the snow clearing is — so
        // PrecipTier must not inherit Safety from the prior snow block for the appearing rain, or the
        // consistency validator rejects it ("tier 'Safety' (Rain) but no final_snapshot block carries
        // a safety-grade signal"). The snow clearing legitimately stays Safety (a removed hazard).
        var prior = Body(Blk(precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Snow));
        var final = Body(Blk(precip: PrecipExpectation.Certain, phenom: PrecipPhenomenon.Rain));

        var changes = Detect(prior, final);
        var rain = Assert.Single(changes, c => c.Phenomenon == ChangePhenomenon.Rain);
        Assert.Equal(ChangeDirection.Appearing, rain.Direction);
        Assert.NotEqual(ChangeTier.Safety, rain.Tier);   // appearing rain is not safety-tier

        // Must not throw — pre-WX-207 the Safety-tiered Rain appearing raised ChangeConsistencyException.
        var report = new StructuredReportBody { Changes = changes };
        ForecastReconciler.ValidateChangeSnapshotConsistency(report, final, prior, Utc);
    }

    public static IEnumerable<object[]> InvariantCases() => new List<object[]>
    {
        new object[] { Body(Blk()), Body(Blk(precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Rain)) },
        new object[] { Body(Blk(precip: PrecipExpectation.Possible, phenom: PrecipPhenomenon.Rain)), Body(Blk(precip: PrecipExpectation.Certain, phenom: PrecipPhenomenon.Rain)) },
        new object[] { Body(Blk(precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Rain)), Body(Blk()) },
        new object[] { Body(Blk()), Body(Blk(precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Thunderstorm, severe: true)) },
        new object[] { Body(Blk(windMax: 10)), Body(Blk(windMax: 55, severe: true)) },
        new object[] { Body(Blk(0), Blk(6), Blk(12)), Body(Blk(0, precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Snow), Blk(6, precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Snow), Blk(12)) },
        // WX-204 (review): a standalone-severe block (severe, no precip) becomes a severe THUNDERSTORM
        // (severe + precip). The detector emits a standalone-Severe Clearing (it is no longer standalone)
        // plus a Thunderstorm Appearing; the per-block consistency net must back BOTH — so the severe
        // helpers must scope to standalone-severe (PrecipPhenomenon null), mirroring DetectSevere.
        new object[] { Body(Blk(windMax: 55, severe: true)), Body(Blk(precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Thunderstorm, severe: true)) },
    };
}