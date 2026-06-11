using System;
using System.Collections.Generic;

using MetarParser.Data.Entities;

using WxInterp;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

// WX-160: the deterministic TAF→block merge that makes the WX-114 significance gate
// TAF-aware. Verifies the gate verdict on a merged GFS+TAF body (the archetype
// gust-only nudge suppresses; a real sustained/precip/severe escalation fires) and
// the merge's own field semantics: windKt is sustained-only, gust touches a gate
// decision solely through the 50-kt severe rule, and a TAF prevails within its
// coverage while GFS stands outside it.
public class TafBlockProjectorTests
{
    private static readonly DateTime Now = new(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly SignificanceGateConfig Cfg = new();

    // TAF validity end used by most tests — just past the +12h period windows the Per
    // helper produces, so intended coverage isn't clipped while far blocks stay outside.
    private static readonly DateTime TafValidTo = Now.AddHours(13);

    private static double FtoC(double f) => (f - 32.0) * 5.0 / 9.0;

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

    // A TAF change period covering [fromH, toH) hours from Now.
    private static ForecastPeriod Per(
        ForecastChangeType type, double fromH, double toH,
        int? sustained = null, int? gust = null,
        PrecipitationType? precip = null, WeatherDescriptor? descriptor = null)
    {
        var wx = new List<SnapshotWeather>();
        if (precip.HasValue || descriptor.HasValue)
            wx.Add(new SnapshotWeather
            {
                Intensity = WeatherIntensity.Moderate,
                Descriptor = descriptor,
                Precipitation = precip.HasValue ? new[] { precip.Value } : Array.Empty<PrecipitationType>(),
            });
        return new ForecastPeriod
        {
            ChangeType = type,
            ValidFromUtc = Now.AddHours(fromH),
            ValidToUtc = Now.AddHours(toH),
            WindSpeedKt = sustained,
            WindGustKt = gust,
            WeatherPhenomena = wx,
        };
    }

    private static bool Sig(ForecastSnapshotBody prior, ForecastSnapshotBody current) =>
        SignificanceGate.Evaluate(prior, current, Cfg, Now, Utc).Significant;

    // ── the archetype: a gust-only nudge from a fresh TAF must suppress ──

    [Fact]
    public void GustOnlyNudge_SustainedUnchanged_Suppressed()
    {
        // Baseline and GFS both carry sustained 12 kt. The fresh TAF restates sustained
        // 12 kt but gusting 18 kt (the 23→21 mph archetype was a gust tick). Sustained
        // is unchanged and 18 kt is nowhere near severe, so the merged body matches the
        // baseline and the gate suppresses — no Claude call.
        var prior = Body(Blk(windMax: 12));
        var gfs = Body(Blk(windMax: 12));
        var taf = new[] { Per(ForecastChangeType.From, -1, 12, sustained: 12, gust: 18) };

        var merged = TafBlockProjector.Merge(gfs, taf, TafValidTo);
        Assert.Equal(12, merged.Blocks[0].WindKt.Max);     // gust excluded
        Assert.False(merged.Blocks[0].SevereFlag);
        Assert.False(Sig(prior, merged));
    }

    [Fact]
    public void SustainedBandCrossing_Fires()
    {
        // The fresh TAF brings sustained 26 kt — across the 25 kt advisory line. Real
        // news: the gate must fire.
        var prior = Body(Blk(windMax: 12));
        var gfs = Body(Blk(windMax: 12));
        var taf = new[] { Per(ForecastChangeType.From, -1, 12, sustained: 26) };

        var merged = TafBlockProjector.Merge(gfs, taf, TafValidTo);
        Assert.Equal(26, merged.Blocks[0].WindKt.Max);
        Assert.True(Sig(prior, merged));
    }

    [Fact]
    public void NewPrecipInTaf_Fires()
    {
        // Baseline and GFS dry; the fresh TAF introduces rain → precip-add fires.
        var prior = Body(Blk());
        var gfs = Body(Blk());
        var taf = new[] { Per(ForecastChangeType.From, -1, 12, sustained: 10, precip: PrecipitationType.Rain) };

        var merged = TafBlockProjector.Merge(gfs, taf, TafValidTo);
        Assert.NotEqual(PrecipExpectation.None, merged.Blocks[0].PrecipExpectation);
        Assert.True(Sig(prior, merged));
    }

    [Fact]
    public void GustReaches50_SevereByDefinition_WindStaysSustained()
    {
        // Sustained 30, gusting 55. The 50-kt rule makes the block severe (gust OR
        // sustained), but windKt.max stays the sustained 30 — gust never lands in windKt.
        var prior = Body(Blk(windMax: 30, severe: false));
        var gfs = Body(Blk(windMax: 30, severe: false));
        var taf = new[] { Per(ForecastChangeType.From, -1, 12, sustained: 30, gust: 55) };

        var merged = TafBlockProjector.Merge(gfs, taf, TafValidTo);
        Assert.Equal(30, merged.Blocks[0].WindKt.Max);
        Assert.True(merged.Blocks[0].SevereFlag);
        Assert.True(Sig(prior, merged));     // severe-add fires at any horizon
    }

    [Fact]
    public void SustainedReaches50_SevereByDefinition()
    {
        var gfs = Body(Blk(windMax: 20, severe: false));
        var taf = new[] { Per(ForecastChangeType.From, -1, 12, sustained: 50) };

        var merged = TafBlockProjector.Merge(gfs, taf, TafValidTo);
        Assert.True(merged.Blocks[0].SevereFlag);
        Assert.Equal(50, merged.Blocks[0].WindKt.Max);
    }

    [Fact]
    public void TempoFluctuation_RaisesBlockMaxSustained()
    {
        // Prevailing 12 kt with a TEMPO group to 26 kt overlapping the block: the block's
        // worst-case sustained is the TEMPO value (max-across-overlapping).
        var gfs = Body(Blk(windMax: 12));
        var taf = new[]
        {
            Per(ForecastChangeType.From, -1, 12, sustained: 12),
            Per(ForecastChangeType.Temporary, 0, 3, sustained: 26),
        };

        var merged = TafBlockProjector.Merge(gfs, taf, TafValidTo);
        Assert.Equal(26, merged.Blocks[0].WindKt.Max);
    }

    [Fact]
    public void TafPrevails_DryOverridesGfsWet_WithinCoverage()
    {
        // GFS says rain in the block, but the covering TAF forecasts dry — the TAF
        // prevails in its window (matching the reconciler / the stored baseline), so the
        // merged block is dry. Against a dry baseline, that is not news.
        var prior = Body(Blk());
        var gfs = Body(Blk(precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Rain));
        var taf = new[] { Per(ForecastChangeType.From, -1, 12, sustained: 10) };

        var merged = TafBlockProjector.Merge(gfs, taf, TafValidTo);
        Assert.Equal(PrecipExpectation.None, merged.Blocks[0].PrecipExpectation);
        Assert.False(Sig(prior, merged));
    }

    [Fact]
    public void BlockOutsideTafCoverage_KeepsGfs()
    {
        // A far block beyond the TAF's reach keeps its GFS signal, so a GFS-only change
        // there still reaches the gate. The near block is covered by the TAF and matches
        // the baseline.
        var prior = Body(Blk(hoursFromNow: 0, windMax: 10), Blk(hoursFromNow: 90, severe: false));
        var gfs = Body(Blk(hoursFromNow: 0, windMax: 10), Blk(hoursFromNow: 90, severe: true));
        var taf = new[] { Per(ForecastChangeType.From, -1, 12, sustained: 10) };

        var merged = TafBlockProjector.Merge(gfs, taf, TafValidTo);
        Assert.True(merged.Blocks[1].SevereFlag);     // GFS far-block severe preserved
        Assert.True(Sig(prior, merged));              // severe-add on the far block fires
    }

    [Fact]
    public void NoTafPeriods_ReturnsProvisionalUnchanged()
    {
        var gfs = Body(Blk(windMax: 12));
        Assert.Same(gfs, TafBlockProjector.Merge(gfs, Array.Empty<ForecastPeriod>(), TafValidTo));
    }

    [Fact]
    public void Snow_InTaf_IsFrozenPhenomenon()
    {
        var gfs = Body(Blk());
        var taf = new[] { Per(ForecastChangeType.From, -1, 12, sustained: 10, precip: PrecipitationType.Snow) };

        var merged = TafBlockProjector.Merge(gfs, taf, TafValidTo);
        Assert.Equal(PrecipPhenomenon.Snow, merged.Blocks[0].PrecipPhenomenon);
    }

    [Fact]
    public void BlockBeyondTafValidity_KeepsGfs_NotOverriddenByExpiredTaf()
    {
        // The TAF (valid through +12h) is dry. GFS forecasts rain at +50h — past TAF
        // validity but inside the 120h gate horizon. Coverage MUST be clamped to the TAF
        // validity end; otherwise the expired last prevailing group wipes the far block to
        // dry and the genuine GFS rain is suppressed. Clamped, the far block keeps GFS rain.
        var prior = Body(Blk(hoursFromNow: 0), Blk(hoursFromNow: 50));
        var gfs = Body(Blk(hoursFromNow: 0), Blk(hoursFromNow: 50, precip: PrecipExpectation.Likely, phenom: PrecipPhenomenon.Rain));
        var taf = new[] { Per(ForecastChangeType.From, -1, 12, sustained: 8) };

        var merged = TafBlockProjector.Merge(gfs, taf, Now.AddHours(12));
        Assert.Equal(PrecipExpectation.Likely, merged.Blocks[1].PrecipExpectation);  // GFS rain preserved past TAF end
        Assert.Equal(PrecipPhenomenon.Rain, merged.Blocks[1].PrecipPhenomenon);
        Assert.True(Sig(prior, merged));                                             // precip-add on the far block fires
    }

    [Fact]
    public void Becmg_NewStatePrevailsForward_PastTransitionWindow()
    {
        // A BECMG raising sustained to 28 kt over a short transition window [2h,4h]. Its
        // new state prevails FORWARD (until the next prevailing group / TAF end), not just
        // within [2,4]. A block at +6h — after the BECMG window — must carry the 28 kt; a
        // naive impl that bounded the group at its own ValidTo would leave it at GFS 10.
        var gfs = Body(Blk(hoursFromNow: 0, windMax: 10), Blk(hoursFromNow: 6, windMax: 10));
        var taf = new[]
        {
            Per(ForecastChangeType.From, -1, 2, sustained: 10),
            Per(ForecastChangeType.BecomeGradually, 2, 4, sustained: 28),
        };

        var merged = TafBlockProjector.Merge(gfs, taf, TafValidTo);
        Assert.Equal(28, merged.Blocks[1].WindKt.Max);   // +6h block inherits the BECMG's new prevailing state
    }
}