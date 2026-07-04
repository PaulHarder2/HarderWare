using MetarParser.Data.Entities;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

// WX-156: the deterministic severe-weather subject prefix. These lock the
// qualification rule (wind branch standalone; convective branch needs Likely+),
// the 24-hour window, and the earliest-qualifying-block noun choice.

public class SevereSubjectPrefixTests
{
    private static readonly DateTime Now = new(2026, 6, 10, 9, 0, 0, DateTimeKind.Utc);

    // WX-171: the severe noun now resolves from the DB-backed template store, not a hard-coded
    // vocabulary. Build it once from the migration seed and resolve the en TemplateSet.
    private static readonly LanguageTemplateStore Store = SeedTemplateStore.Build();
    private static readonly TemplateSet En = Store.ForLanguage("en");

    // A forecast block with the severe-relevant fields set; the rest are inert defaults.
    private static ForecastSnapshotBlock Block(
        DateTime startUtc, bool severe, int maxWindKt, PrecipExpectation precip,
        PrecipPhenomenon? phenomenon = null) => new()
        {
            StartUtc = startUtc,
            SkyState = SkyState.Overcast,
            Obscuration = Obscuration.None,
            TemperatureCelsius = new MinMax<double>(20.0, 28.0),
            WindKt = new MinMax<int>(5, maxWindKt),
            PrecipExpectation = precip,
            PrecipPhenomenon = phenomenon,
            SevereFlag = severe,
        };

    private static ForecastSnapshotBody Body(params ForecastSnapshotBlock[] blocks) => new()
    {
        SchemaVersion = ForecastSnapshotBody.SchemaVersionCurrent,
        Blocks = blocks,
    };

    [Fact]
    public void WindBranch_SevereFiftyKt_Prefixes_StandaloneEvenWithoutPrecip()
    {
        // 55 kt damaging wind, no precip at all → wind branch qualifies on its own.
        var body = Body(Block(Now.AddHours(3), severe: true, maxWindKt: 55, PrecipExpectation.None));
        Assert.Equal("Severe weather", SevereSubjectPrefix.Evaluate(body, Now, En));
    }

    [Fact]
    public void ConvectiveBranch_Likely_Thunderstorm_Prefixes()
    {
        // Sub-50 kt wind → SevereFlag can only be CAPE-driven; PrecipExpectation Likely clears the gate.
        var body = Body(Block(Now.AddHours(4), severe: true, maxWindKt: 20, PrecipExpectation.Likely, PrecipPhenomenon.Thunderstorm));
        Assert.Equal("Severe storms", SevereSubjectPrefix.Evaluate(body, Now, En));
    }

    [Fact]
    public void SevereFlag_OnlyPossiblePrecip_Prefixes()
    {
        // WX-160 Option A: SevereFlag is now authoritative, so a CAPE-driven severe block
        // (wind < 50, precip only Possible) DOES get a prefix. WX-156 originally excluded
        // this as "mere instability", but that filter rested on a premise WX-160 broke
        // (sub-50 sustained no longer implies CAPE-only — it can be a 50 kt gust); we now
        // trust SevereFlag and match the body hazard banner, which keys on it directly.
        var body = Body(Block(Now.AddHours(4), severe: true, maxWindKt: 20, PrecipExpectation.Possible, PrecipPhenomenon.Thunderstorm));
        Assert.Equal("Severe storms", SevereSubjectPrefix.Evaluate(body, Now, En));
    }

    [Fact]
    public void SevereFlag_DryGustSevere_Prefixes()
    {
        // The WX-160 case the Option-A change exists for: a gust-severe block carries
        // SevereFlag with sustained < 50 and no precip (windKt is sustained-only, the
        // gust drove the flag). It must still get a severe subject prefix.
        var body = Body(Block(Now.AddHours(3), severe: true, maxWindKt: 30, PrecipExpectation.None));
        Assert.Equal("Severe weather", SevereSubjectPrefix.Evaluate(body, Now, En));
    }

    [Fact]
    public void OffsetLocality_SevereOnLocalBoundary_InWindow_Prefixes()
    {
        // CDT-local-aligned blocks (StartUtc on local day-part boundaries, WX-155). The window is
        // instant-based, so an offset locality behaves identically: this 11Z (6 AM CDT) block is in window.
        var body = Body(
            Block(new DateTime(2026, 6, 10, 5, 0, 0, DateTimeKind.Utc), severe: false, maxWindKt: 10, PrecipExpectation.None),
            Block(new DateTime(2026, 6, 10, 11, 0, 0, DateTimeKind.Utc), severe: true, maxWindKt: 58, PrecipExpectation.None));
        Assert.Equal("Severe weather", SevereSubjectPrefix.Evaluate(body, Now, En));
    }

    [Fact]
    public void EarliestQualifyingBlock_SuppliesTheNoun()
    {
        // Two qualifying blocks; the earliest is convective (thunderstorm) → its noun wins, matching the body banner.
        var body = Body(
            Block(Now.AddHours(2), severe: true, maxWindKt: 20, PrecipExpectation.Likely, PrecipPhenomenon.Thunderstorm),
            Block(Now.AddHours(14), severe: true, maxWindKt: 60, PrecipExpectation.None));
        Assert.Equal("Severe storms", SevereSubjectPrefix.Evaluate(body, Now, En));
    }

    [Fact]
    public void NotSevere_NoPrefix()
        => Assert.Null(SevereSubjectPrefix.Evaluate(
            Body(Block(Now.AddHours(3), severe: false, maxWindKt: 20, PrecipExpectation.Likely, PrecipPhenomenon.Rain)),
            Now, En));

    [Fact]
    public void SevereBeyond24h_NoPrefix()
        => Assert.Null(SevereSubjectPrefix.Evaluate(
            Body(Block(Now.AddHours(30), severe: true, maxWindKt: 55, PrecipExpectation.None)),
            Now, En));

    [Fact]
    public void SeverePastBlock_NoPrefix()
        // Block ended an hour ago (start −7 h, +6 h duration) → not in window.
        => Assert.Null(SevereSubjectPrefix.Evaluate(
            Body(Block(Now.AddHours(-7), severe: true, maxWindKt: 55, PrecipExpectation.None)),
            Now, En));
}