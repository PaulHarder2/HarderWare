// Unit tests for SnapshotFingerprint.
// Verifies that each significant-condition threshold is encoded correctly
// and that two snapshots produce equal or unequal fingerprints as expected.

using WxInterp;

using WxReport.Svc;

using Xunit;

namespace WxInterp.Tests;

public class SnapshotFingerprintTests
{
    private static readonly SignificantChangeConfig DefaultCfg = new()
    {
        WindThresholdKt = 25,
        VisibilityThresholdSm = 3.0,
    };

    // ── helpers ───────────────────────────────────────────────────────────────

    private static WeatherSnapshot Make(
        int? windKt = 5,
        double visSm = 10.0,
        bool cavok = false,
        IReadOnlyList<SkyLayer>? layers = null,
        IReadOnlyList<SnapshotWeather>? wx = null) => new()
        {
            WindSpeedKt = windKt,
            VisibilityStatuteMiles = visSm,
            Cavok = cavok,
            SkyLayers = layers ?? [],
            WeatherPhenomena = wx ?? [],
        };

    // ── wind ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Wind_BelowThreshold_EncodesAsFalse()
        => Assert.Contains("W:False", SnapshotFingerprint.Compute(Make(windKt: 24), DefaultCfg));

    [Fact]
    public void Wind_AtThreshold_EncodesAsTrue()
        => Assert.Contains("W:True", SnapshotFingerprint.Compute(Make(windKt: 25), DefaultCfg));

    [Fact]
    public void Wind_AboveThreshold_EncodesAsTrue()
        => Assert.Contains("W:True", SnapshotFingerprint.Compute(Make(windKt: 40), DefaultCfg));

    [Fact]
    public void Wind_Null_EncodesAsFalse()
        => Assert.Contains("W:False", SnapshotFingerprint.Compute(Make(windKt: null), DefaultCfg));

    // ── visibility ────────────────────────────────────────────────────────────

    [Fact]
    public void Visibility_AboveThreshold_EncodesAsFalse()
        => Assert.Contains("V:False", SnapshotFingerprint.Compute(Make(visSm: 3.1), DefaultCfg));

    [Fact]
    public void Visibility_AtThreshold_EncodesAsFalse()
        // 3.0 is not < 3.0
        => Assert.Contains("V:False", SnapshotFingerprint.Compute(Make(visSm: 3.0), DefaultCfg));

    [Fact]
    public void Visibility_BelowThreshold_EncodesAsTrue()
        => Assert.Contains("V:True", SnapshotFingerprint.Compute(Make(visSm: 2.9), DefaultCfg));

    [Fact]
    public void Visibility_Cavok_EncodesAsFalse()
        => Assert.Contains("V:False", SnapshotFingerprint.Compute(Make(cavok: true), DefaultCfg));

    // ── thunderstorm ─────────────────────────────────────────────────────────

    [Fact]
    public void Thunderstorm_Absent_EncodesAsFalse()
        => Assert.Contains("TS:False", SnapshotFingerprint.Compute(Make(), DefaultCfg));

    [Fact]
    public void Thunderstorm_Present_EncodesAsTrue()
    {
        var wx = new[] { new SnapshotWeather { Descriptor = WeatherDescriptor.Thunderstorm, IsRecent = false } };
        Assert.Contains("TS:True", SnapshotFingerprint.Compute(Make(wx: wx), DefaultCfg));
    }

    [Fact]
    public void Thunderstorm_RecentOnly_EncodesAsFalse()
    {
        // RETS — recent thunderstorm — should not count as a current significant condition
        var wx = new[] { new SnapshotWeather { Descriptor = WeatherDescriptor.Thunderstorm, IsRecent = true } };
        Assert.Contains("TS:False", SnapshotFingerprint.Compute(Make(wx: wx), DefaultCfg));
    }

    // ── precipitation ─────────────────────────────────────────────────────────

    [Fact]
    public void Precipitation_Absent_EncodesAsFalse()
        => Assert.Contains("PR:False", SnapshotFingerprint.Compute(Make(), DefaultCfg));

    [Fact]
    public void Precipitation_Present_EncodesAsTrue()
    {
        var wx = new[] { new SnapshotWeather { Precipitation = [PrecipitationType.Rain], IsRecent = false } };
        Assert.Contains("PR:True", SnapshotFingerprint.Compute(Make(wx: wx), DefaultCfg));
    }

    [Fact]
    public void Precipitation_RecentOnly_EncodesAsFalse()
    {
        // RERA — recent rain — should not count as a current significant condition
        var wx = new[] { new SnapshotWeather { Precipitation = [PrecipitationType.Rain], IsRecent = true } };
        Assert.Contains("PR:False", SnapshotFingerprint.Compute(Make(wx: wx), DefaultCfg));
    }

    // ── change detection ─────────────────────────────────────────────────────

    [Fact]
    public void SameConditions_ProduceEqualFingerprints()
    {
        var snap = Make(windKt: 10, visSm: 8.0);
        Assert.Equal(
            SnapshotFingerprint.Compute(snap, DefaultCfg),
            SnapshotFingerprint.Compute(snap, DefaultCfg));
    }

    [Fact]
    public void WindCrossesThreshold_ProducesDifferentFingerprints()
    {
        Assert.NotEqual(
            SnapshotFingerprint.Compute(Make(windKt: 20), DefaultCfg),
            SnapshotFingerprint.Compute(Make(windKt: 30), DefaultCfg));
    }

    [Fact]
    public void VisibilityCrossesThreshold_ProducesDifferentFingerprints()
    {
        Assert.NotEqual(
            SnapshotFingerprint.Compute(Make(visSm: 5.0), DefaultCfg),
            SnapshotFingerprint.Compute(Make(visSm: 1.5), DefaultCfg));
    }

    [Fact]
    public void MinorWindChange_BothBelowThreshold_ProducesEqualFingerprints()
    {
        Assert.Equal(
            SnapshotFingerprint.Compute(Make(windKt: 10), DefaultCfg),
            SnapshotFingerprint.Compute(Make(windKt: 15), DefaultCfg));
    }
}