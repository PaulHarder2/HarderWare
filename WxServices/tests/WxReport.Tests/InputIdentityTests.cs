using WxInterp;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

// Unit tests for the WX-80 pre-filter identity: the cheap "has any input changed
// since the last Claude call?" gate. Each trigger source (METAR / TAF / GFS) is
// exercised independently, plus the serialise/parse round-trip the
// RecipientState.LastClaudeInputHash column relies on. The end-to-end no-send
// assertion for the KDWH scenario lives in WX-82's replay test; here we only
// prove that an input advance is detected and routed (significance is Claude's
// call, not the identity's).

public class InputIdentityTests
{
    private static readonly DateTime T0 = new(2026, 4, 21, 9, 53, 0, DateTimeKind.Utc);

    [Fact]
    public void Serialize_RoundTripsThroughParse()
    {
        var id = new InputIdentity("KDWH@2026-04-21T09:53:00.0000000Z", "taf-a", "gfs-a");
        Assert.Equal(id, InputIdentity.Parse(id.Serialize()));
    }

    [Fact]
    public void Parse_NullOrBlank_YieldsAllNone()
    {
        Assert.Equal(new InputIdentity("none", "none", "none"), InputIdentity.Parse(null));
        Assert.Equal(new InputIdentity("none", "none", "none"), InputIdentity.Parse("  "));
    }

    [Theory]
    [InlineData("M:|T:none|G:none")]      // empty value
    [InlineData("M:m1|bogus|G:g1")]       // segment with no "K:" prefix
    [InlineData("X:m1|T:t1|G:g1")]        // unrecognized key
    [InlineData("M:a|M:b|G:g1")]          // duplicate key (only 3 segments, T missing)
    [InlineData("M:m1|T:t1")]             // too few segments
    [InlineData("M:m1|T:t1|G:g1|G:g2")]   // too many segments
    [InlineData("garbage")]
    public void Parse_MalformedSegment_FailsClosedToAllNone(string serialized)
    {
        // A corrupt stored hash must read as the all-"none" baseline so it
        // differs from any real input and routes to Claude, rather than yielding
        // a half-parsed identity that could spuriously match.
        Assert.Equal(new InputIdentity("none", "none", "none"), InputIdentity.Parse(serialized));
    }

    [Fact]
    public void ChangedSourcesSince_Identical_IsEmpty()
    {
        var id = new InputIdentity("m1", "t1", "g1");
        Assert.Empty(id.ChangedSourcesSince(id.Serialize()));
    }

    [Fact]
    public void ChangedSourcesSince_MetarOnly_DetectsMetar()
    {
        var prev = new InputIdentity("m1", "t1", "g1");
        var cur = prev with { Metar = "m2" };
        Assert.Equal(new[] { TriggerSource.Metar }, cur.ChangedSourcesSince(prev.Serialize()));
    }

    [Fact]
    public void ChangedSourcesSince_TafOnly_DetectsTaf()
    {
        var prev = new InputIdentity("m1", "t1", "g1");
        var cur = prev with { Taf = "t2" };
        Assert.Equal(new[] { TriggerSource.Taf }, cur.ChangedSourcesSince(prev.Serialize()));
    }

    [Fact]
    public void ChangedSourcesSince_GfsOnly_DetectsGfs()
    {
        var prev = new InputIdentity("m1", "t1", "g1");
        var cur = prev with { Gfs = "g2" };
        Assert.Equal(new[] { TriggerSource.Gfs }, cur.ChangedSourcesSince(prev.Serialize()));
    }

    [Fact]
    public void ChangedSourcesSince_NullBaseline_TreatsRealInputsAsChanged()
    {
        // A recipient with no stored hash (pre-WX-80 row, or first ever Claude
        // call) must route to Claude rather than silently skipping.
        var cur = new InputIdentity("m1", "none", "g1");
        Assert.Equal(new[] { TriggerSource.Metar, TriggerSource.Gfs }, cur.ChangedSourcesSince(null));
    }

    [Fact]
    public void From_WithObservation_EncodesStationAndMaterialSignature()
    {
        var snap = new WeatherSnapshot { ObservationAvailable = true, StationIcao = "KDWH", ObservationTimeUtc = T0 };
        var id = InputIdentity.From(snap);
        Assert.StartsWith("KDWH;", id.Metar);
        Assert.DoesNotContain("@", id.Metar);   // WX-110: no longer keyed on the raw observation timestamp
        Assert.Equal("none", id.Taf);
        Assert.Equal("none", id.Gfs);
    }

    [Fact]
    public void From_WithoutObservation_MetarIsNone()
    {
        var snap = new WeatherSnapshot { ObservationAvailable = false, StationIcao = "" };
        Assert.Equal("none", InputIdentity.From(snap).Metar);
    }

    [Fact]
    public void From_WithTaf_EncodesIssuance()
    {
        var snap = new WeatherSnapshot { ObservationAvailable = false, StationIcao = "", TafIssuanceUtc = T0 };
        Assert.NotEqual("none", InputIdentity.From(snap).Taf);
    }

    [Fact]
    public void SameMaterialObservation_LaterTime_DoesNotChangeMetar()
    {
        // WX-110: a fresh METAR with a later observation time but materially identical
        // weather (same station / wind / visibility / sky / temperature band /
        // phenomena) is evidence Claude has already evaluated, so the pre-filter must
        // NOT route it to the gate again — this closes the hourly-re-observation cost
        // leak. Under WX-80 the raw timestamp alone flipped the component; WX-110
        // narrows the trigger to a *material* change.
        var prior = ObsSnap("KDWH", T0, windKt: 8, tempF: 72, visMiles: 10);
        var later = ObsSnap("KDWH", T0.AddHours(4), windKt: 8, tempF: 72, visMiles: 10);
        var changed = InputIdentity.From(later).ChangedSourcesSince(InputIdentity.From(prior).Serialize());
        Assert.Empty(changed);
    }

    [Fact]
    public void MateriallyDifferentObservation_ChangesMetarOnly()
    {
        // A genuine change in the observed weather (here wind jumping from band 0 to
        // band 2) still advances only the METAR component, routing the cycle to
        // Claude's gate — which decides whether it is news.
        var prior = ObsSnap("KDWH", T0, windKt: 8, tempF: 72, visMiles: 10);
        var later = ObsSnap("KDWH", T0.AddHours(1), windKt: 40, tempF: 72, visMiles: 10);
        var changed = InputIdentity.From(later).ChangedSourcesSince(InputIdentity.From(prior).Serialize());
        Assert.Equal(new[] { TriggerSource.Metar }, changed);
    }

    [Theory]
    [InlineData(0, null, 0)]
    [InlineData(16, null, 0)]
    [InlineData(17, null, 1)]   // half tropical-storm force
    [InlineData(33, null, 1)]
    [InlineData(34, null, 2)]   // tropical-storm / gale force
    [InlineData(47, null, 2)]
    [InlineData(48, null, 3)]   // storm force
    [InlineData(63, null, 3)]
    [InlineData(64, null, 4)]   // hurricane force
    [InlineData(10, 40, 2)]     // gust dominates sustained
    public void WindBand_BinsAtForecasterThresholds(int? speed, int? gust, int expected)
        => Assert.Equal(expected, InputIdentity.WindBand(speed, gust));

    [Theory]
    [InlineData(0.25, false, false, 0)]
    [InlineData(0.99, false, false, 0)]
    [InlineData(1.0, false, false, 1)]
    [InlineData(1.0, false, true, 0)]    // reported as "<1 mi"
    [InlineData(10.0, false, false, 1)]
    [InlineData(null, false, false, 1)]  // unreported -> unremarkable
    [InlineData(0.25, true, false, 1)]   // CAVOK overrides
    public void VisibilityBand_SplitsAtOneMile(double? vis, bool cavok, bool lessThan, int expected)
        => Assert.Equal(expected, InputIdentity.VisibilityBand(vis, cavok, lessThan));

    [Theory]
    [InlineData(null, "na")]
    [InlineData(72.0, "14")]   // floor(72/5) = 14
    [InlineData(74.9, "14")]
    [InlineData(75.0, "15")]
    [InlineData(-2.0, "-1")]   // floor(-0.4) = -1
    public void TemperatureBand_FiveDegreeBins(double? tempF, string expected)
        => Assert.Equal(expected, InputIdentity.TemperatureBand(tempF));

    [Fact]
    public void SkyBand_TakesDensestLayer()
    {
        var layers = new List<SkyLayer>
        {
            new() { Coverage = SkyCoverage.Few },
            new() { Coverage = SkyCoverage.Broken },
            new() { Coverage = SkyCoverage.Scattered },
        };
        Assert.Equal(2, InputIdentity.SkyBand(layers));            // BKN dominates
        Assert.Equal(0, InputIdentity.SkyBand(new List<SkyLayer>())); // clear sky
    }

    [Fact]
    public void WeatherSignature_OrderIndependentAndExcludesRecent()
    {
        var a = new List<SnapshotWeather>
        {
            new() { Descriptor = WeatherDescriptor.Thunderstorm, Precipitation = new[] { PrecipitationType.Rain } },
        };
        var b = new List<SnapshotWeather>
        {
            new() { Precipitation = new[] { PrecipitationType.Rain } },
            new() { Descriptor = WeatherDescriptor.Thunderstorm },
            new() { Obscuration = WeatherObscuration.Fog, IsRecent = true }, // recent: excluded
        };
        Assert.Equal(InputIdentity.WeatherSignature(a), InputIdentity.WeatherSignature(b));
        Assert.DoesNotContain("fog", InputIdentity.WeatherSignature(b));
    }

    private static WeatherSnapshot ObsSnap(
        string icao, DateTime obsUtc, int? windKt = null, double? tempF = null, double? visMiles = null) =>
        new()
        {
            ObservationAvailable = true,
            StationIcao = icao,
            ObservationTimeUtc = obsUtc,
            WindSpeedKt = windKt,
            TemperatureFahrenheit = tempF,
            VisibilityStatuteMiles = visMiles,
        };
}