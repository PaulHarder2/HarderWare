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
    public void From_WithObservation_EncodesStationAndTime()
    {
        var snap = new WeatherSnapshot { ObservationAvailable = true, StationIcao = "KDWH", ObservationTimeUtc = T0 };
        var id = InputIdentity.From(snap);
        Assert.StartsWith("KDWH@", id.Metar);
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
    public void NewObservationTime_FlipsMetarComponentOnly()
    {
        // The 2026-04-21 KDWH double-send: a fresh METAR (later observation time)
        // for the same station advances only the METAR component, so the cycle is
        // routed to Claude's gate — which then judges whether it is news.
        var prior = new WeatherSnapshot { ObservationAvailable = true, StationIcao = "KDWH", ObservationTimeUtc = T0 };
        var later = new WeatherSnapshot { ObservationAvailable = true, StationIcao = "KDWH", ObservationTimeUtc = T0.AddHours(4) };
        var changed = InputIdentity.From(later).ChangedSourcesSince(InputIdentity.From(prior).Serialize());
        Assert.Equal(new[] { TriggerSource.Metar }, changed);
    }
}