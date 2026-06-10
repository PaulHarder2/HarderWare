// Unit tests for GfsSnapshotBuilder — the WX-77 provisional snapshot builder.
// Each [Fact] pins one mapping rule or edge case from the WX-77 grooming
// conversation; threshold values live in GfsSnapshotBuilder and the tests
// pick representative inputs on either side of those boundaries.

using MetarParser.Data.Entities;

using WxInterp;

using Xunit;

namespace WxInterp.Tests;

public class GfsSnapshotBuilderTests
{
    private static readonly DateTime BlockStart = new(2026, 5, 27, 0, 0, 0, DateTimeKind.Utc);

    // ── fixture helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Build a single hourly point.  Defaults are intentionally neutral — mild
    /// temperature, light wind, partly cloudy, dry, calm CAPE — so individual
    /// tests can override just the fields they care about.
    /// </summary>
    private static GfsHourlyPoint Hour(
        int hourOffset,
        float? tmpC = 15f,
        float? dwpC = 5f,
        float? windKt = 5f,
        float? precipMmHr = 0f,
        float? tcdcPct = 30f,
        float? capeJKg = 0f,
        DateTime? baseTime = null)
    {
        var t = (baseTime ?? BlockStart).AddHours(hourOffset);
        return new GfsHourlyPoint
        {
            ValidTimeUtc = t,
            TmpC = tmpC,
            DwpC = dwpC,
            WindKt = windKt,
            WindDirDeg = 180,
            PrecipMmHr = precipMmHr,
            TcdcPct = tcdcPct,
            CapeJKg = capeJKg,
        };
    }

    private static GfsHourlyForecast Forecast(params GfsHourlyPoint[] hours)
        => new() { ModelRunUtc = BlockStart, Hours = hours };

    /// <summary>Six hours of neutral weather forming one full 00–06Z block.</summary>
    private static GfsHourlyForecast NeutralBlock(DateTime? baseTime = null)
        => Forecast(
            Hour(0, baseTime: baseTime), Hour(1, baseTime: baseTime), Hour(2, baseTime: baseTime),
            Hour(3, baseTime: baseTime), Hour(4, baseTime: baseTime), Hour(5, baseTime: baseTime));

    /// <summary>Test convenience: build with a tz, defaulting to UTC (the pre-WX-155 grid) so the tz-agnostic mapping tests stay terse. Production's <see cref="GfsSnapshotBuilder.Build"/> requires the tz — only this test shim defaults it.</summary>
    private static ForecastSnapshotBody Build(GfsHourlyForecast forecast, TimeZoneInfo? tz = null)
        => GfsSnapshotBuilder.Build(forecast, tz ?? TimeZoneInfo.Utc);

    // ── block emission ───────────────────────────────────────────────────────

    [Fact]
    public void Build_EmptyHours_ProducesNoBlocks()
    {
        var body = Build(new GfsHourlyForecast { ModelRunUtc = BlockStart, Hours = [] });
        Assert.Empty(body.Blocks);
    }

    [Fact]
    public void Build_NeutralFullBlock_EmitsOneBlock()
    {
        var body = Build(NeutralBlock());
        Assert.Single(body.Blocks);
    }

    [Fact]
    public void Build_BlockWithFewerThanFourHours_IsSkipped()
    {
        // Three hours is below MinHoursPerBlock (4) — skipped.
        var body = Build(Forecast(Hour(0), Hour(1), Hour(2)));
        Assert.Empty(body.Blocks);
    }

    [Fact]
    public void Build_BlockWithFourHoursOfPresentData_IsEmitted()
    {
        // Four hours is at the threshold — emitted.
        var body = Build(Forecast(Hour(0), Hour(1), Hour(2), Hour(3)));
        Assert.Single(body.Blocks);
    }

    [Fact]
    public void Build_BlockWithNoTemperatureData_IsSkipped()
    {
        var body = Build(Forecast(
            Hour(0, tmpC: null), Hour(1, tmpC: null), Hour(2, tmpC: null),
            Hour(3, tmpC: null), Hour(4, tmpC: null), Hour(5, tmpC: null)));
        Assert.Empty(body.Blocks);
    }

    [Fact]
    public void Build_StartUtcAlignedTo6Z()
    {
        var body = Build(NeutralBlock());
        var startHour = body.Blocks[0].StartUtc.Hour;
        Assert.True(startHour is 0 or 6 or 12 or 18, $"Block start hour {startHour} not aligned to 6Z grid.");
    }

    [Fact]
    public void Build_RespectsMaxBlocksCap()
    {
        // 30 6-hour blocks = 180 hours; builder should cap at 24.
        var hours = Enumerable.Range(0, 30 * 6).Select(i => Hour(i)).ToArray();
        var body = Build(Forecast(hours));
        Assert.Equal(24, body.Blocks.Count);
    }

    // ── sky state ────────────────────────────────────────────────────────────

    [Fact]
    public void SkyState_AllLowCover_Clear()
    {
        var body = Build(Forecast(
            Hour(0, tcdcPct: 5), Hour(1, tcdcPct: 10), Hour(2, tcdcPct: 15),
            Hour(3, tcdcPct: 19), Hour(4, tcdcPct: 5), Hour(5, tcdcPct: 0)));
        Assert.Equal(SkyState.Clear, body.Blocks[0].SkyState);
    }

    [Fact]
    public void SkyState_MaxBelowPartlyCeiling_PartlyCloudy()
    {
        var body = Build(Forecast(
            Hour(0, tcdcPct: 20), Hour(1, tcdcPct: 35), Hour(2, tcdcPct: 50),
            Hour(3, tcdcPct: 55), Hour(4, tcdcPct: 30), Hour(5, tcdcPct: 25)));
        Assert.Equal(SkyState.PartlyCloudy, body.Blocks[0].SkyState);
    }

    [Fact]
    public void SkyState_MaxBelowMostlyCeiling_MostlyCloudy()
    {
        var body = Build(Forecast(
            Hour(0, tcdcPct: 60), Hour(1, tcdcPct: 70), Hour(2, tcdcPct: 80),
            Hour(3, tcdcPct: 86), Hour(4, tcdcPct: 65), Hour(5, tcdcPct: 70)));
        Assert.Equal(SkyState.MostlyCloudy, body.Blocks[0].SkyState);
    }

    [Fact]
    public void SkyState_AnyHourAtOrAboveOvercast_Overcast()
    {
        var body = Build(Forecast(
            Hour(0, tcdcPct: 60), Hour(1, tcdcPct: 90), Hour(2, tcdcPct: 95),
            Hour(3, tcdcPct: 100), Hour(4, tcdcPct: 80), Hour(5, tcdcPct: 70)));
        Assert.Equal(SkyState.Overcast, body.Blocks[0].SkyState);
    }

    [Fact]
    public void SkyState_MaxAtClearBoundary_StaysClear()
    {
        // The SkyClearMaxPct ceiling is inclusive: TCDC = 20 maps to Clear, not
        // PartlyCloudy.  Pinned to prevent a regression to strict `<` comparison.
        var body = Build(Forecast(
            Hour(0, tcdcPct: 10), Hour(1, tcdcPct: 15), Hour(2, tcdcPct: 18),
            Hour(3, tcdcPct: 20), Hour(4, tcdcPct: 10), Hour(5, tcdcPct: 5)));
        Assert.Equal(SkyState.Clear, body.Blocks[0].SkyState);
    }

    // ── precipitation expectation ────────────────────────────────────────────

    [Fact]
    public void PrecipExpectation_NoWetHours_None()
    {
        var body = Build(NeutralBlock());
        Assert.Equal(PrecipExpectation.None, body.Blocks[0].PrecipExpectation);
        Assert.Null(body.Blocks[0].PrecipPhenomenon);
    }

    [Fact]
    public void PrecipExpectation_OneWetHour_Possible()
    {
        var body = Build(Forecast(
            Hour(0, precipMmHr: 0.5f), Hour(1), Hour(2), Hour(3), Hour(4), Hour(5)));
        Assert.Equal(PrecipExpectation.Possible, body.Blocks[0].PrecipExpectation);
    }

    [Fact]
    public void PrecipExpectation_TwoWetHours_Likely()
    {
        var body = Build(Forecast(
            Hour(0, precipMmHr: 0.5f), Hour(1, precipMmHr: 0.5f),
            Hour(2), Hour(3), Hour(4), Hour(5)));
        Assert.Equal(PrecipExpectation.Likely, body.Blocks[0].PrecipExpectation);
    }

    [Fact]
    public void PrecipExpectation_OneHeavyHour_Likely()
    {
        var body = Build(Forecast(
            Hour(0, precipMmHr: 3.0f), Hour(1), Hour(2), Hour(3), Hour(4), Hour(5)));
        Assert.Equal(PrecipExpectation.Likely, body.Blocks[0].PrecipExpectation);
    }

    [Fact]
    public void PrecipExpectation_FiveWetHours_Certain()
    {
        var body = Build(Forecast(
            Hour(0, precipMmHr: 0.5f), Hour(1, precipMmHr: 0.5f),
            Hour(2, precipMmHr: 0.5f), Hour(3, precipMmHr: 0.5f),
            Hour(4, precipMmHr: 0.5f), Hour(5)));
        Assert.Equal(PrecipExpectation.Certain, body.Blocks[0].PrecipExpectation);
    }

    [Fact]
    public void PrecipExpectation_TwoHeavyHours_Certain()
    {
        var body = Build(Forecast(
            Hour(0, precipMmHr: 3.0f), Hour(1, precipMmHr: 3.0f),
            Hour(2), Hour(3), Hour(4), Hour(5)));
        Assert.Equal(PrecipExpectation.Certain, body.Blocks[0].PrecipExpectation);
    }

    // ── precipitation phenomenon ─────────────────────────────────────────────

    [Fact]
    public void Phenomenon_WetAndWarm_Rain()
    {
        var body = Build(Forecast(
            Hour(0, precipMmHr: 0.5f, tmpC: 12f), Hour(1, precipMmHr: 0.5f, tmpC: 14f),
            Hour(2, tmpC: 14f), Hour(3, tmpC: 13f), Hour(4, tmpC: 12f), Hour(5, tmpC: 11f)));
        Assert.Equal(PrecipPhenomenon.Rain, body.Blocks[0].PrecipPhenomenon);
    }

    [Fact]
    public void Phenomenon_WetHourWithHighCape_Thunderstorm()
    {
        var body = Build(Forecast(
            Hour(0, precipMmHr: 0.5f, tmpC: 22f, capeJKg: 1500f),
            Hour(1, precipMmHr: 0.5f, tmpC: 22f),
            Hour(2, tmpC: 24f), Hour(3, tmpC: 23f), Hour(4, tmpC: 22f), Hour(5, tmpC: 21f)));
        Assert.Equal(PrecipPhenomenon.Thunderstorm, body.Blocks[0].PrecipPhenomenon);
    }

    [Fact]
    public void Phenomenon_AllWetHoursSubFreezing_Snow()
    {
        var body = Build(Forecast(
            Hour(0, precipMmHr: 0.5f, tmpC: -3f, dwpC: -5f),
            Hour(1, precipMmHr: 0.5f, tmpC: -2f, dwpC: -4f),
            Hour(2, tmpC: -2f, dwpC: -4f), Hour(3, tmpC: -1.5f, dwpC: -3f),
            Hour(4, tmpC: -1f, dwpC: -3f), Hour(5, tmpC: -1f, dwpC: -3f)));
        Assert.Equal(PrecipPhenomenon.Snow, body.Blocks[0].PrecipPhenomenon);
    }

    [Fact]
    public void Phenomenon_NearZeroWithSubFreezingDewpoint_FreezingPrecip()
    {
        var body = Build(Forecast(
            Hour(0, precipMmHr: 0.5f, tmpC: 0.5f, dwpC: -2f),
            Hour(1, precipMmHr: 0.5f, tmpC: 0.8f, dwpC: -2f),
            Hour(2, tmpC: 0.5f, dwpC: -2f), Hour(3, tmpC: 0.3f, dwpC: -2f),
            Hour(4, tmpC: 0.0f, dwpC: -2f), Hour(5, tmpC: -0.2f, dwpC: -2f)));
        Assert.Equal(PrecipPhenomenon.FreezingPrecip, body.Blocks[0].PrecipPhenomenon);
    }

    [Fact]
    public void Phenomenon_WetHoursSpanningZeroCrossing_Mixed()
    {
        // Cold-side wet hours strictly below the freezing-precip band (≤ -1.5°C)
        // so the freezing-precip rule does not fire on a near-zero wet hour and
        // we exercise the span-crossing Mixed rule cleanly.
        var body = Build(Forecast(
            Hour(0, precipMmHr: 0.5f, tmpC: -2f, dwpC: -3f),
            Hour(1, precipMmHr: 0.5f, tmpC: -1.5f, dwpC: -3f),
            Hour(2, precipMmHr: 0.5f, tmpC: 2f, dwpC: 1f),
            Hour(3, precipMmHr: 0.5f, tmpC: 3f, dwpC: 1f),
            Hour(4, tmpC: 4f, dwpC: 2f), Hour(5, tmpC: 5f, dwpC: 2f)));
        Assert.Equal(PrecipPhenomenon.Mixed, body.Blocks[0].PrecipPhenomenon);
    }

    // ── severe flag (provisional, per WX-77; WX-81 may refine) ───────────────

    [Fact]
    public void SevereFlag_HighWind_True()
    {
        var body = Build(Forecast(
            Hour(0, windKt: 55f), Hour(1, windKt: 50f), Hour(2, windKt: 52f),
            Hour(3, windKt: 51f), Hour(4, windKt: 45f), Hour(5, windKt: 40f)));
        Assert.True(body.Blocks[0].SevereFlag);
    }

    [Fact]
    public void SevereFlag_SevereCapeWithWet_True()
    {
        var body = Build(Forecast(
            Hour(0, precipMmHr: 0.5f, capeJKg: 2700f),
            Hour(1), Hour(2), Hour(3), Hour(4), Hour(5)));
        Assert.True(body.Blocks[0].SevereFlag);
    }

    [Fact]
    public void SevereFlag_SevereCapeButDry_False()
    {
        // High CAPE with no wet hour → not severe; capped/un-fired environment.
        var body = Build(Forecast(
            Hour(0, capeJKg: 3000f), Hour(1, capeJKg: 3000f), Hour(2, capeJKg: 3000f),
            Hour(3, capeJKg: 3000f), Hour(4, capeJKg: 3000f), Hour(5, capeJKg: 3000f)));
        Assert.False(body.Blocks[0].SevereFlag);
    }

    [Fact]
    public void SevereFlag_CalmLowCape_False()
    {
        var body = Build(NeutralBlock());
        Assert.False(body.Blocks[0].SevereFlag);
    }

    // ── obscuration ──────────────────────────────────────────────────────────

    [Fact]
    public void Obscuration_AlwaysNoneInV1()
    {
        // GFS ingest carries no obscuration variable; default is None until a
        // future ticket adds a humidity-proxy fog detector.
        var body = Build(NeutralBlock());
        Assert.Equal(Obscuration.None, body.Blocks[0].Obscuration);
    }

    // ── invariants ───────────────────────────────────────────────────────────

    [Fact]
    public void Body_RoundTripsThroughJsonSchema()
    {
        var body = Build(Forecast(
            Hour(0, precipMmHr: 0.5f, tmpC: 12f), Hour(1, precipMmHr: 0.5f, tmpC: 14f),
            Hour(2, tmpC: 14f), Hour(3, tmpC: 13f), Hour(4, tmpC: 12f), Hour(5, tmpC: 11f)));

        var json = body.Serialize();
        var roundTripped = ForecastSnapshotBody.Deserialize(json);

        Assert.Equal(body.Blocks.Count, roundTripped.Blocks.Count);
        Assert.Equal(body.Blocks[0].PrecipPhenomenon, roundTripped.Blocks[0].PrecipPhenomenon);
        Assert.Equal(body.Blocks[0].PrecipExpectation, roundTripped.Blocks[0].PrecipExpectation);
    }

    [Fact]
    public void Build_IsDeterministic_IdenticalInputsProduceByteIdenticalSerialisation()
    {
        var inputs = () => Forecast(
            Hour(0, precipMmHr: 0.5f, tmpC: 12.3f, windKt: 14.7f, tcdcPct: 65f),
            Hour(1, precipMmHr: 0.7f, tmpC: 13.1f, windKt: 16.2f, tcdcPct: 70f),
            Hour(2, precipMmHr: 0.4f, tmpC: 13.8f, windKt: 15.5f, tcdcPct: 72f),
            Hour(3, precipMmHr: 0.6f, tmpC: 14.0f, windKt: 17.1f, tcdcPct: 78f),
            Hour(4, precipMmHr: 0.3f, tmpC: 13.5f, windKt: 14.8f, tcdcPct: 80f),
            Hour(5, precipMmHr: 0.2f, tmpC: 12.9f, windKt: 13.2f, tcdcPct: 75f));

        var first = Build(inputs()).Serialize();
        var second = Build(inputs()).Serialize();

        Assert.Equal(first, second);
    }

    // ── edge cases ───────────────────────────────────────────────────────────

    [Fact]
    public void Build_MissingHoursWithinBlock_StillEmitsIfFourPresent()
    {
        // 4 hours present, 2 absent — meets minimum, should emit.
        var body = Build(Forecast(
            Hour(0), Hour(2), Hour(4), Hour(5)));
        Assert.Single(body.Blocks);
    }

    [Fact]
    public void Build_NoPrecipDay_AllBlocksHaveNoneExpectationAndNoPhenomenon()
    {
        // Two consecutive dry blocks (12 hours total).
        var hours = Enumerable.Range(0, 12).Select(i => Hour(i)).ToArray();
        var body = Build(Forecast(hours));

        Assert.Equal(2, body.Blocks.Count);
        Assert.All(body.Blocks, b =>
        {
            Assert.Equal(PrecipExpectation.None, b.PrecipExpectation);
            Assert.Null(b.PrecipPhenomenon);
        });
    }

    [Fact]
    public void Build_TemperatureRange_ReflectsMinAndMaxAcrossHours()
    {
        var body = Build(Forecast(
            Hour(0, tmpC: 10f), Hour(1, tmpC: 14f), Hour(2, tmpC: 16f),
            Hour(3, tmpC: 18f), Hour(4, tmpC: 15f), Hour(5, tmpC: 12f)));
        Assert.Equal(10.0, body.Blocks[0].TemperatureCelsius.Min);
        Assert.Equal(18.0, body.Blocks[0].TemperatureCelsius.Max);
    }

    [Fact]
    public void Build_WindRange_RoundedToInteger()
    {
        var body = Build(Forecast(
            Hour(0, windKt: 5.4f), Hour(1, windKt: 7.6f), Hour(2, windKt: 9.5f),
            Hour(3, windKt: 11.2f), Hour(4, windKt: 13.8f), Hour(5, windKt: 12.1f)));
        Assert.Equal(5, body.Blocks[0].WindKt.Min);
        Assert.Equal(14, body.Blocks[0].WindKt.Max);
    }

    // ── WX-155: local day-part bucketing ──────────────────────────────────────

    // America/Chicago is CDT (UTC-5) on the May date and spans the spring-forward
    // transition on the March date. Used to prove blocks anchor to local day-parts
    // (00/06/12/18 local), not the UTC 6-hour grid.
    private static readonly TimeZoneInfo Cdt = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    [Fact]
    public void Build_OffsetTimezone_BucketsByLocalDayPart()
    {
        // 11Z..16Z on 2026-05-27 is 6 AM..11 AM CDT — entirely the local MORNING
        // day-part (06–12), whose block starts at 06:00 local = 11:00Z. The 12:00Z
        // hour (7 AM CDT) lands here, in MORNING — not in a 12:00Z "afternoon"
        // block as the pre-WX-155 UTC grid would have placed it.
        var hours = Enumerable.Range(11, 6).Select(h => Hour(h)).ToArray();
        var body = Build(Forecast(hours), Cdt);

        var block = Assert.Single(body.Blocks);
        Assert.Equal(new DateTime(2026, 5, 27, 11, 0, 0, DateTimeKind.Utc),
            DateTime.SpecifyKind(block.StartUtc, DateTimeKind.Utc));
        var localStart = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(block.StartUtc, DateTimeKind.Utc), Cdt);
        Assert.Equal(6, localStart.Hour);  // local morning boundary
    }

    [Fact]
    public void Build_UtcTimezone_PreservesUtcGrid()
    {
        // Same 11Z..16Z input with UTC → the pre-WX-155 grid: 11Z floors to the
        // 06Z block (one hour, below the 4-hour minimum, dropped) and 12Z..16Z to
        // the 12:00Z block. The bulk lands at 12:00Z — the very straddle WX-155
        // fixes for offset zones, absent here because UTC has no offset.
        var hours = Enumerable.Range(11, 6).Select(h => Hour(h)).ToArray();
        var body = GfsSnapshotBuilder.Build(Forecast(hours), TimeZoneInfo.Utc);

        var block = Assert.Single(body.Blocks);
        Assert.Equal(new DateTime(2026, 5, 27, 12, 0, 0, DateTimeKind.Utc),
            DateTime.SpecifyKind(block.StartUtc, DateTimeKind.Utc));
    }

    [Fact]
    public void Build_AcrossDstTransition_BlocksStayOnLocalDayPartBoundaries()
    {
        // Spring-forward: 2026-03-08, 02:00 CST -> 03:00 CDT. Build a day-plus of
        // hourly data; every emitted block must still start on a local 0/6/12/18
        // boundary — the transition never lands on a 6-hour mark.
        var dayStartUtc = new DateTime(2026, 3, 8, 6, 0, 0, DateTimeKind.Utc);  // 00:00 CST
        var hours = Enumerable.Range(0, 30).Select(h => Hour(h, baseTime: dayStartUtc)).ToArray();
        var body = Build(Forecast(hours), Cdt);

        Assert.NotEmpty(body.Blocks);
        Assert.All(body.Blocks, b =>
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(b.StartUtc, DateTimeKind.Utc), Cdt);
            Assert.Equal(0, local.Hour % 6);
            Assert.Equal(0, local.Minute);
        });
    }

    [Fact]
    public void Build_MidnightDstTransitionZone_DoesNotThrow()
    {
        // America/Santiago springs forward AT 00:00 local (e.g. 2025-09-07), so the
        // overnight day-part boundary is an invalid local time. The flooring must
        // step out of the gap rather than let TimeZoneInfo.ConvertTimeToUtc throw —
        // the locality timezone can be any IANA zone, not just US 02:00-transition ones.
        var santiago = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Pacific SA Standard Time" : "America/Santiago");
        var dayStartUtc = new DateTime(2025, 9, 7, 0, 0, 0, DateTimeKind.Utc);
        var hours = Enumerable.Range(0, 30).Select(h => Hour(h, baseTime: dayStartUtc)).ToArray();

        var body = GfsSnapshotBuilder.Build(Forecast(hours), santiago);  // must not throw

        Assert.NotEmpty(body.Blocks);
    }
}