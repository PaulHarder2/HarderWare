using MetarParser.Data.Entities;

using WxInterp;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

/// <summary>
/// WX-129 deterministic renderer: the same unit-neutral structured input must
/// render to per-recipient reports that differ only by units (and language),
/// with faithful conversions and no prose-entangled values. Times use UTC as the
/// locality timezone so local calendar days equal UTC days (deterministic).
/// </summary>
public class StructuredReportRendererTests
{
    private static readonly DateTime AnchorUtc = new(2026, 6, 8, 18, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    // Send instant for the renderer's WX-188 day-trim. Set before every fixture's earliest
    // block (06:00Z Jun 8) so the full grid renders with no day trimmed — existing assertions
    // are unaffected; the trim itself is exercised by the dedicated tests at the bottom.
    private static readonly DateTime RenderNow = new(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc);

    // WX-171: the renderer reads atomic phrases from the DB-backed store; build one from the
    // migration seed and resolve each language's TemplateSet + culture (T/C) for the calls below.
    private static readonly WxReport.Svc.LanguageTemplateStore Store = SeedTemplateStore.Build();
    private static WxReport.Svc.TemplateSet T(string lang) => Store.ForLanguage(lang);
    private static System.Globalization.CultureInfo C(string lang) => Store.CultureFor(lang);

    private static Recipient Imperial() => new()
    {
        RecipientId = "t-en",
        Name = "Test",
        Email = "t@example.com",
        TempUnit = "F",
        PressureUnit = "inHg",
        WindSpeedUnit = "mph",
        PrecipUnit = "in",
    };

    private static Recipient Metric() => new()
    {
        RecipientId = "t-m",
        Name = "Test",
        Email = "t@example.com",
        TempUnit = "C",
        PressureUnit = "kPa",
        WindSpeedUnit = "kph",
        PrecipUnit = "mm",
    };

    private static WeatherSnapshot Observation() => new()
    {
        ObservationAvailable = true,
        LocalityName = "Spring",
        ObservationTimeUtc = AnchorUtc,
        StationIcao = "KDWH",
        SkyLayers = [new SkyLayer { Coverage = SkyCoverage.Overcast, HeightFeet = 3000 }],
        VisibilityStatuteMiles = 10,
        WindDirectionDeg = 180,
        WindSpeedKt = 12,
        WindGustKt = 22,
        WeatherPhenomena = [new SnapshotWeather { Intensity = WeatherIntensity.Light, Precipitation = [PrecipitationType.Rain] }],
        TemperatureCelsius = 31.0,
        TemperatureFahrenheit = 87.8,
        DewPointCelsius = 24.0,
        AltimeterInHg = 29.92,
    };

    private static ForecastSnapshotBody Forecast() => new()
    {
        Blocks =
        [
            new() { StartUtc = new(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc), SkyState = SkyState.MostlyCloudy, Obscuration = Obscuration.None, TemperatureCelsius = new(24, 31), WindKt = new(5, 12), PrecipExpectation = PrecipExpectation.Likely, PrecipPhenomenon = PrecipPhenomenon.Thunderstorm, SevereFlag = false },
            new() { StartUtc = new(2026, 6, 8, 18, 0, 0, DateTimeKind.Utc), SkyState = SkyState.Overcast, Obscuration = Obscuration.None, TemperatureCelsius = new(26, 33), WindKt = new(8, 15), PrecipExpectation = PrecipExpectation.Certain, PrecipPhenomenon = PrecipPhenomenon.Rain, SevereFlag = false },
            new() { StartUtc = new(2026, 6, 9, 0, 0, 0, DateTimeKind.Utc), SkyState = SkyState.PartlyCloudy, Obscuration = Obscuration.None, TemperatureCelsius = new(22, 28), WindKt = new(3, 8), PrecipExpectation = PrecipExpectation.None, PrecipPhenomenon = null, SevereFlag = false },
            new() { StartUtc = new(2026, 6, 9, 6, 0, 0, DateTimeKind.Utc), SkyState = SkyState.Clear, Obscuration = Obscuration.None, TemperatureCelsius = new(20, 26), WindKt = new(2, 6), PrecipExpectation = PrecipExpectation.None, PrecipPhenomenon = null, SevereFlag = false },
        ],
    };

    // A severe afternoon thunderstorm followed by evening rain, same local day —
    // exercises the severe-sentence cell and the degraded hazard banner.
    private static ForecastSnapshotBody SevereForecast() => new()
    {
        Blocks =
        [
            new() { StartUtc = new(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc), SkyState = SkyState.Overcast, Obscuration = Obscuration.None, TemperatureCelsius = new(26, 33), WindKt = new(8, 20), PrecipExpectation = PrecipExpectation.Likely, PrecipPhenomenon = PrecipPhenomenon.Thunderstorm, SevereFlag = true },
            new() { StartUtc = new(2026, 6, 8, 18, 0, 0, DateTimeKind.Utc), SkyState = SkyState.Overcast, Obscuration = Obscuration.None, TemperatureCelsius = new(24, 31), WindKt = new(5, 12), PrecipExpectation = PrecipExpectation.Certain, PrecipPhenomenon = PrecipPhenomenon.Rain, SevereFlag = false },
        ],
    };

    // A severe block carrying NO precipitation (a damaging-wind event) — forms no
    // episode, so it exercises the day-level severe path that must still surface it.
    private static ForecastSnapshotBody SevereNoPrecipForecast() => new()
    {
        Blocks =
        [
            new() { StartUtc = new(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc), SkyState = SkyState.MostlyCloudy, Obscuration = Obscuration.None, TemperatureCelsius = new(28, 36), WindKt = new(20, 40), PrecipExpectation = PrecipExpectation.None, PrecipPhenomenon = null, SevereFlag = true },
        ],
    };

    // One rain episode spanning the 06Z and 12Z blocks (morning into afternoon) —
    // exercises the range-label timing.
    private static ForecastSnapshotBody SpanningForecast() => new()
    {
        Blocks =
        [
            new() { StartUtc = new(2026, 6, 8, 6, 0, 0, DateTimeKind.Utc), SkyState = SkyState.Overcast, Obscuration = Obscuration.None, TemperatureCelsius = new(20, 26), WindKt = new(3, 8), PrecipExpectation = PrecipExpectation.Likely, PrecipPhenomenon = PrecipPhenomenon.Rain, SevereFlag = false },
            new() { StartUtc = new(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc), SkyState = SkyState.Overcast, Obscuration = Obscuration.None, TemperatureCelsius = new(24, 30), WindKt = new(4, 10), PrecipExpectation = PrecipExpectation.Likely, PrecipPhenomenon = PrecipPhenomenon.Rain, SevereFlag = false },
        ],
    };

    // A day with a MISSING interior block: 00Z and 12Z present (both rain), 06Z absent. The two
    // same-phrase bands must NOT merge across the gap, or the label would fabricate coverage of
    // the 06-12 window the snapshot has no block for (WX-190 review).
    private static ForecastSnapshotBody GappedForecast() => new()
    {
        Blocks =
        [
            new() { StartUtc = new(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc), SkyState = SkyState.Overcast, Obscuration = Obscuration.None, TemperatureCelsius = new(20, 26), WindKt = new(3, 8), PrecipExpectation = PrecipExpectation.Likely, PrecipPhenomenon = PrecipPhenomenon.Rain, SevereFlag = false },
            new() { StartUtc = new(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc), SkyState = SkyState.Overcast, Obscuration = Obscuration.None, TemperatureCelsius = new(24, 30), WindKt = new(4, 10), PrecipExpectation = PrecipExpectation.Likely, PrecipPhenomenon = PrecipPhenomenon.Rain, SevereFlag = false },
        ],
    };

    // A full local day (all four 6-hour blocks) of clear, dry weather — every band shares
    // one phrase, so the cell must collapse to a single whole-day "00-24" line (WX-190).
    private static ForecastSnapshotBody UniformClearForecast() => new()
    {
        Blocks =
        [
            new() { StartUtc = new(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc), SkyState = SkyState.Clear, Obscuration = Obscuration.None, TemperatureCelsius = new(18, 24), WindKt = new(2, 6), PrecipExpectation = PrecipExpectation.None, PrecipPhenomenon = null, SevereFlag = false },
            new() { StartUtc = new(2026, 6, 8, 6, 0, 0, DateTimeKind.Utc), SkyState = SkyState.Clear, Obscuration = Obscuration.None, TemperatureCelsius = new(20, 27), WindKt = new(3, 7), PrecipExpectation = PrecipExpectation.None, PrecipPhenomenon = null, SevereFlag = false },
            new() { StartUtc = new(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc), SkyState = SkyState.Clear, Obscuration = Obscuration.None, TemperatureCelsius = new(24, 31), WindKt = new(4, 9), PrecipExpectation = PrecipExpectation.None, PrecipPhenomenon = null, SevereFlag = false },
            new() { StartUtc = new(2026, 6, 8, 18, 0, 0, DateTimeKind.Utc), SkyState = SkyState.Clear, Obscuration = Obscuration.None, TemperatureCelsius = new(21, 28), WindKt = new(2, 6), PrecipExpectation = PrecipExpectation.None, PrecipPhenomenon = null, SevereFlag = false },
        ],
    };

    // A severe thunderstorm in the 00-06 pre-dawn band — exercises the prose day-binding
    // (banner must read "{weekday} 00-06", never a floating "overnight"; WX-190).
    private static ForecastSnapshotBody SevereOvernightForecast() => new()
    {
        Blocks =
        [
            new() { StartUtc = new(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc), SkyState = SkyState.Overcast, Obscuration = Obscuration.None, TemperatureCelsius = new(20, 26), WindKt = new(10, 25), PrecipExpectation = PrecipExpectation.Likely, PrecipPhenomenon = PrecipPhenomenon.Thunderstorm, SevereFlag = true },
        ],
    };

    // A single local day (all four blocks) where the morning has rain and the afternoon/evening
    // are clear. Rendered with a mid-day nowUtc, the 00-06 and 06-12 bands are fully elapsed and
    // must drop from the Conditions cell (WX-195) — while the day's High/Low still span all four
    // blocks (incl. the elapsed morning low). 00Z min 15°C is the day's low; 12Z max 33°C the high.
    private static ForecastSnapshotBody TodayPartlyElapsedForecast() => new()
    {
        Blocks =
        [
            new() { StartUtc = new(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc), SkyState = SkyState.Overcast, Obscuration = Obscuration.None, TemperatureCelsius = new(20, 26), WindKt = new(3, 8), PrecipExpectation = PrecipExpectation.Likely, PrecipPhenomenon = PrecipPhenomenon.Rain, SevereFlag = false },
            new() { StartUtc = new(2026, 6, 8, 6, 0, 0, DateTimeKind.Utc), SkyState = SkyState.Overcast, Obscuration = Obscuration.None, TemperatureCelsius = new(15, 22), WindKt = new(4, 10), PrecipExpectation = PrecipExpectation.Likely, PrecipPhenomenon = PrecipPhenomenon.Rain, SevereFlag = false },
            new() { StartUtc = new(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc), SkyState = SkyState.Clear, Obscuration = Obscuration.None, TemperatureCelsius = new(25, 33), WindKt = new(5, 11), PrecipExpectation = PrecipExpectation.None, PrecipPhenomenon = null, SevereFlag = false },
            new() { StartUtc = new(2026, 6, 8, 18, 0, 0, DateTimeKind.Utc), SkyState = SkyState.Clear, Obscuration = Obscuration.None, TemperatureCelsius = new(23, 29), WindKt = new(3, 8), PrecipExpectation = PrecipExpectation.None, PrecipPhenomenon = null, SevereFlag = false },
        ],
    };

    // Closing prose carries one of every quantity-token kind + a time token; no
    // unit words adjacent (the renderer appends units).
    private const string ClosingTokens =
        "Highs near {q:temp:33.5}, winds to {q:wind:22} gusting {q:gust:30}, pressure {q:pressure:1013.2}, rainfall {q:precip_mm:12}, clearing after {q:time:2026-06-08T21:00:00Z}.";

    private static StructuredReportBody Body(string? changeSummary = null) => new()
    {
        Changes = changeSummary is null
            ? []
            : [new ReportChange { Tier = ChangeTier.Plans, Phenomenon = ChangePhenomenon.Thunderstorm, Direction = ChangeDirection.Appearing, Window = new(AnchorUtc, AnchorUtc.AddHours(6)), Quantities = [], SummaryToken = "ch1" }],
        Narrative = new Dictionary<string, NarrativeSections>
        {
            ["en"] = new() { ChangeSummary = changeSummary, Closing = ClosingTokens },
        },
    };

    [Fact]
    public void SameInput_ImperialVsMetric_DiffersOnlyByUnits()
    {
        var en = StructuredReportRenderer.Render(Body(), Forecast(), Observation(), Imperial(), T("en"), C("en"), Utc, ReportKind.Scheduled, RenderNow);
        var me = StructuredReportRenderer.Render(Body(), Forecast(), Observation(), Metric(), T("en"), C("en"), Utc, ReportKind.Scheduled, RenderNow);

        // Imperial conversions from canonical °C / kt / hPa / mm.
        Assert.Contains("92°F", en);          // 33.5°C
        Assert.Contains("25 mph", en);        // 22 kt
        Assert.Contains("35 mph", en);        // 30 kt
        Assert.Contains("29.92 inHg", en);    // 1013.2 hPa
        Assert.Contains("0.47 in", en);       // 12 mm
        Assert.DoesNotContain("°C", en);

        // Metric conversions of the same tokens.
        Assert.Contains("34°C", me);
        Assert.Contains("41 km/h", me);
        Assert.Contains("56 km/h", me);
        Assert.Contains("101.3 kPa", me);
        Assert.Contains("12 mm", me);
        Assert.DoesNotContain("°F", me);
    }

    [Fact]
    public void TempRangeToken_RendersOneConvertedSpan_PerRecipientUnit()
    {
        // WX-228: a {q:temp_range:lo:hi} token (canonical °C) renders as a single
        // converted span under one unit suffix — 36–37 °C → 97–99 °F for an imperial
        // recipient, kept as 36–37 °C for a metric one.
        var report = new StructuredReportBody
        {
            Changes = [],
            Narrative = new Dictionary<string, NarrativeSections>
            {
                ["en"] = new() { ChangeSummary = null, Closing = "Highs hold {q:temp_range:36:37} all week." },
            },
        };

        var en = StructuredReportRenderer.Render(report, Forecast(), Observation(), Imperial(), T("en"), C("en"), Utc, ReportKind.Scheduled, RenderNow);
        var me = StructuredReportRenderer.Render(report, Forecast(), Observation(), Metric(), T("en"), C("en"), Utc, ReportKind.Scheduled, RenderNow);

        Assert.Contains("97–99°F", en);
        Assert.DoesNotContain("°C", en);
        Assert.Contains("36–37°C", me);
        Assert.DoesNotContain("°F", me);
    }

    [Fact]
    public void FallbackBand_IsDirectionAware_WhenChangeSummaryAbsent()
    {
        // WX-189 (CodeRabbit #3): when the band must show (unscheduled) but the model's
        // changeSummary prose is absent/rejected, the deterministic fallback names each
        // computed change direction-aware — a CLEARING change reads as ending, not arriving.
        var report = new StructuredReportBody
        {
            Changes =
            [
                new ReportChange { Tier = ChangeTier.Safety, Phenomenon = ChangePhenomenon.Thunderstorm, Direction = ChangeDirection.Appearing, Window = new(AnchorUtc, AnchorUtc.AddHours(6)), Quantities = [], SummaryToken = "ch1" },
                new ReportChange { Tier = ChangeTier.Plans, Phenomenon = ChangePhenomenon.Rain, Direction = ChangeDirection.Clearing, Window = new(AnchorUtc, AnchorUtc.AddHours(6)), Quantities = [], SummaryToken = "ch2" },
            ],
            Narrative = new Dictionary<string, NarrativeSections>
            {
                ["en"] = new() { ChangeSummary = null, Closing = "Quiet weather ahead." },
            },
        };

        var en = StructuredReportRenderer.Render(report, Forecast(), Observation(), Imperial(), T("en"), C("en"), Utc, ReportKind.Unscheduled, RenderNow);
        Assert.Contains("Severe storms developing", en);  // Safety-tier convective → severe lead + appearing gerund
        Assert.Contains("Rain ending", en);               // clearing reads as ending, not a bare "Rain —"
    }

    [Fact]
    public void TimeToken_RendersInLocalityTimeAndLocale()
    {
        var en = StructuredReportRenderer.Render(Body(), Forecast(), Observation(), Imperial(), T("en"), C("en"), Utc, ReportKind.Scheduled, RenderNow);
        Assert.Contains("9:00 PM", en);  // 21:00Z in UTC locality
    }

    [Fact]
    public void ExtendedForecast_OneRowPerLocalDay_WithDailyHiLo()
    {
        var en = StructuredReportRenderer.Render(Body(), Forecast(), Observation(), Imperial(), T("en"), C("en"), Utc, ReportKind.Scheduled, RenderNow);

        // Two local days → two Low/High pairs (WX-236: Low printed above High).
        Assert.Equal(2, CountOccurrences(en, "High:"));
        // Forecast() is two PARTIAL days (WX-234 gating): Jun 8 has only afternoon+evening blocks
        // (12Z, 18Z) — an afternoon high but no pre-dawn/morning low; Jun 9 has only overnight+morning
        // blocks (00Z, 06Z) — a dawn low but no afternoon high. So each day shows one numeric extreme
        // and one em dash, not the pre-WX-234 four numeric values.
        Assert.Contains("91°F", en);          // Jun 8 High: 33°C (afternoon block present)
        Assert.Contains("68°F", en);          // Jun 9 Low: 20°C (morning block present)
        Assert.DoesNotContain("75°F", en);    // Jun 8 Low suppressed — no pre-dawn/morning block
        Assert.DoesNotContain("82°F", en);    // Jun 9 High suppressed — no afternoon block
        Assert.Contains("Low: —", en);        // Jun 8's suppressed low renders as an em dash
        Assert.Contains("High: —", en);       // Jun 9's suppressed high renders as an em dash
        Assert.Contains("Forecast for Spring", en);
    }

    [Fact]
    public void Conditions_TilesDayIntoClockBands_NotCollapsedToPeak()
    {
        var en = StructuredReportRenderer.Render(Body(), Forecast(), Observation(), Imperial(), T("en"), C("en"), Utc, ReportKind.Scheduled, RenderNow);
        // WX-148 Class 2 / WX-190: Day 1 tiles into its two present bands — a thunderstorm
        // at 12Z (12-18) then rain at 18Z (18-24) — each its own clock-band line, rather
        // than collapsing to the single highest-expectation one.
        Assert.Contains("12-18 — Storms likely", en);
        Assert.Contains("18-24 — Rain expected", en);
        // Day 2: 00Z PartlyCloudy (no precip) → sky phrase; 06Z Clear → "Clear and dry".
        Assert.Contains("00-06 — Partly cloudy", en);
        Assert.Contains("06-12 — Clear and dry", en);
        Assert.DoesNotContain("Partly cloudy and dry", en);  // PartlyCloudy is not the clear-day "and dry"
    }

    [Fact]
    public void Conditions_SevereBand_IsEmphasizedAndClockBound()
    {
        // WX-190: a severe band is bold and labeled by its clock range; the grid row's
        // date binds the hazard to its calendar day, so no floating "overnight" is used.
        var en = StructuredReportRenderer.Render(Body(), SevereForecast(), Observation(), Imperial(), T("en"), C("en"), Utc, ReportKind.Scheduled, RenderNow);
        Assert.Contains("<strong>12-18 — Severe storms likely</strong>", en);
        Assert.Contains("18-24 — Rain expected", en);
    }

    [Fact]
    public void Conditions_SevereWithoutPrecip_StillSurfacesHazard()
    {
        // Regression guard: a severe block with no precipitation must not collapse to a
        // benign sky phrase — its band leads with the generic "Severe weather" hazard.
        var en = StructuredReportRenderer.Render(Body(), SevereNoPrecipForecast(), Observation(), Imperial(), T("en"), C("en"), Utc, ReportKind.Scheduled, RenderNow);
        // Generic "Severe weather" — not storm-specific — because the severe block carries no precip (a wind event).
        Assert.Contains("<strong>12-18 — Severe weather likely</strong>", en);
    }

    [Fact]
    public void Conditions_AdjacentBandsSharingConditions_Merge()
    {
        // WX-190: rain in both the 06Z (06-12) and 12Z (12-18) bands shares one phrase,
        // so the two adjacent bands merge into a single "06-18" line.
        var en = StructuredReportRenderer.Render(Body(), SpanningForecast(), Observation(), Imperial(), T("en"), C("en"), Utc, ReportKind.Scheduled, RenderNow);
        Assert.Contains("06-18 — Rain likely", en);
    }

    [Fact]
    public void Conditions_MissingInteriorBlock_DoesNotMergeAcrossGap()
    {
        // WX-190 (review): two same-phrase bands separated by a missing interior block must render
        // as separate clock-band lines, never merged into a span that fabricates coverage of the gap.
        var en = StructuredReportRenderer.Render(Body(), GappedForecast(), Observation(), Imperial(), T("en"), C("en"), Utc, ReportKind.Scheduled, RenderNow);
        Assert.Contains("00-06 — Rain likely", en);
        Assert.Contains("12-18 — Rain likely", en);
        Assert.DoesNotContain("00-18", en);   // the 06-12 gap must not be bridged
    }

    [Fact]
    public void Conditions_Today_DropsElapsedLeadingBands_ButHiLoSpanWholeDay()
    {
        // WX-195: at mid-day, today's already-elapsed bands (00-06, 06-12) must drop from the
        // Conditions cell so it leads with the CURRENT band — but the day's High/Low must still
        // span ALL blocks (incl. the elapsed morning low), per the WX-176/WX-188 invariant.
        var nowUtc = new DateTime(2026, 6, 8, 13, 0, 0, DateTimeKind.Utc);  // 00-06Z and 06-12Z fully elapsed
        var en = StructuredReportRenderer.Render(Body(), TodayPartlyElapsedForecast(), Observation(), Imperial(), T("en"), C("en"), Utc, ReportKind.Scheduled, nowUtc);
        Assert.Contains("12-24 — Clear and dry", en);   // leads with the current band; 12-18 + 18-24 merge
        Assert.DoesNotContain("00-06", en);             // elapsed morning bands dropped
        Assert.DoesNotContain("06-12", en);
        Assert.DoesNotContain("Rain likely", en);       // the elapsed rain bands are gone from the cell
        Assert.Contains("91°F", en);                    // High from the 12Z block (33°C) — whole-day
        Assert.Contains("59°F", en);                    // Low from the ELAPSED 06Z block (15°C) — proves Hi/Lo span all blocks
    }

    [Fact]
    public void Conditions_UniformDay_CollapsesToWholeDayBand()
    {
        // WX-190: when every band of a day shares one condition, the four bands merge into
        // a single whole-day "00-24" line rather than four identical rows.
        var en = StructuredReportRenderer.Render(Body(), UniformClearForecast(), Observation(), Imperial(), T("en"), C("en"), Utc, ReportKind.Scheduled, RenderNow);
        Assert.Contains("00-24 — Clear and dry", en);
        Assert.DoesNotContain("06-12", en);  // no intermediate band labels survive the merge
    }

    [Fact]
    public void Grid_RendersTwentyFourHourClockLegend()
    {
        // WX-190: the 24-hour-clock key is one glance from the band labels it explains.
        // WX-184: it now sits ABOVE the grid — directly beneath the "Forecast for …"
        // heading rule — left-justified to match the Current Conditions station line.
        var en = StructuredReportRenderer.Render(Body(), Forecast(), Observation(), Imperial(), T("en"), C("en"), Utc, ReportKind.Scheduled, RenderNow);
        Assert.Contains("Times use a 24-hour clock: 00 = midnight, 12 = noon, 24 = midnight.", en);
        Assert.DoesNotContain("12 AM", en);  // the banned contradictory anchors never appear
        Assert.DoesNotContain("12 PM", en);
        // WX-184: left-justified caption styling (matches the CC station line), not WX-195's centered form.
        Assert.Contains("font-size:13px;color:#6b8fa8;font-style:italic;margin:2px 0 4px;\">Times use a 24-hour clock", en);
        // WX-184: the legend precedes the forecast grid table (above it, not below).
        var legendIdx = en.IndexOf("Times use a 24-hour clock", StringComparison.Ordinal);
        var gridTableIdx = en.IndexOf("<table", en.IndexOf("Forecast for", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(legendIdx >= 0 && legendIdx < gridTableIdx, "legend should render above the forecast grid");
    }

    [Fact]
    public void Conditions_PreDawnBand_UsesClockLabel_NeverOvernight()
    {
        // WX-190: the 00-06 pre-dawn block is labeled by its clock range in the grid,
        // never the floating "Overnight"/"overnight" daypart word.
        var en = StructuredReportRenderer.Render(Body(), SevereOvernightForecast(), Observation(), Imperial(), T("en"), C("en"), Utc, ReportKind.Scheduled, RenderNow);
        Assert.Contains("<strong>00-06 — Severe storms likely</strong>", en);
        Assert.DoesNotContain("Overnight", en);
        Assert.DoesNotContain("overnight", en);
    }

    [Fact]
    public void HazardBanner_PreDawnBlock_BoundByClockRangeNotOvernight()
    {
        // WX-190: the degraded hazard banner is prose with no grid row to bind the day,
        // so the pre-dawn block is bound by its clock range ("{weekday} 00-06"), never a
        // floating "overnight"/"{weekday} night" that a US reader hears as the next day.
        var nowUtc = new DateTime(2026, 6, 8, 2, 0, 0, DateTimeKind.Utc);  // within the 00-06Z severe block
        var en = StructuredReportRenderer.RenderDegraded(SevereOvernightForecast(), Observation(), Imperial(), T("en"), C("en"), Utc, nowUtc);
        var expectedDay = nowUtc.ToString("dddd", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        Assert.Contains($"Severe storms in your forecast — {expectedDay} 00-06.", en);
        Assert.DoesNotContain("overnight", en);
    }

    [Fact]
    public void RenderDegraded_LeadsWithHazardBanner_NoSummaryNoChangeBand()
    {
        // WX-148 tier-aware degrade: a narrative-less hazard report — deterministic
        // banner + conditions + grid, with NO What's-changed band and NO summary
        // (a summary we cannot deliver is left out, not apologized for).
        var nowUtc = new DateTime(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc);  // within the 12–18Z severe block
        var en = StructuredReportRenderer.RenderDegraded(SevereForecast(), Observation(), Imperial(), T("en"), C("en"), Utc, nowUtc);
        // Full deterministic banner (SevereForecast is convective → "Severe storms"); weekday
        // computed so the assertion can't drift, and "in your forecast" is banner-specific
        // (the grid cell says "Severe storms likely …", never "in your forecast").
        var expectedDay = nowUtc.ToString("dddd", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        Assert.Contains($"Severe storms in your forecast — {expectedDay} afternoon.", en);
        Assert.Contains("Reported Conditions", en);             // conditions table present (WX-184 relabel)
        Assert.Contains("Forecast for Spring", en);             // forecast grid present
        Assert.DoesNotContain("What's changed:", en);           // no change band
        Assert.DoesNotContain("In summary:", en);               // no closing/summary
    }

    [Fact]
    public void SkyPhrase_RanksByCloudiness_NotEnumOrdinal()
    {
        // A Broken layer plus a "no significant cloud" layer must read as cloudy,
        // not Clear: NoSignificantCloud has a HIGHER enum ordinal than Broken, so a
        // raw max-ordinal pick (the pre-fix bug) would have masked the real cloud.
        var snap = new WeatherSnapshot
        {
            ObservationAvailable = true,
            LocalityName = "Spring",
            ObservationTimeUtc = AnchorUtc,
            SkyLayers =
            [
                new SkyLayer { Coverage = SkyCoverage.Broken, HeightFeet = 4000 },
                new SkyLayer { Coverage = SkyCoverage.NoSignificantCloud },
            ],
            TemperatureCelsius = 20.0,
        };
        var en = StructuredReportRenderer.Render(Body(), Forecast(), snap, Imperial(), T("en"), C("en"), Utc, ReportKind.Scheduled, RenderNow);
        Assert.Contains("Low mostly cloudy", en);  // Broken@4000 ft → Low prefix; NSC ignored
    }

    [Fact]
    public void CurrentConditions_TableFromObservation()
    {
        var en = StructuredReportRenderer.Render(Body(), Forecast(), Observation(), Imperial(), T("en"), C("en"), Utc, ReportKind.Scheduled, RenderNow);
        Assert.Contains("Reported Conditions", en);   // WX-184 relabel (was "Current Conditions")
        Assert.Contains("Low overcast", en);          // overcast at 3000 ft
        Assert.Contains("S at 14 mph, gusting 25 mph", en);  // 180°, 12 kt→14 mph, gust 22 kt→25 mph
        Assert.Contains("Light rain", en);
        Assert.Contains("88°F", en);                   // 31°C
        Assert.Contains("Good", en);                   // 10 SM visibility band
    }

    [Fact]
    public void ChangeBand_RendersOnlyWhenNarrativeCarriesOne()
    {
        var scheduled = StructuredReportRenderer.Render(Body(), Forecast(), Observation(), Imperial(), T("en"), C("en"), Utc, ReportKind.Scheduled, RenderNow);
        Assert.DoesNotContain("What's changed:", scheduled);
        Assert.DoesNotContain("Unscheduled Update", scheduled);
        Assert.Contains("Scheduled Report", scheduled);   // the type label is always shown (WX-154)

        var unscheduled = StructuredReportRenderer.Render(
            Body("{ch1}Thunderstorms now expected this afternoon."), Forecast(), Observation(), Imperial(), T("en"), C("en"), Utc, ReportKind.Unscheduled, RenderNow);
        Assert.Contains("What's changed:", unscheduled);
        Assert.Contains("Thunderstorms now expected this afternoon.", unscheduled);
        Assert.Contains("Unscheduled Update", unscheduled);
    }

    [Fact]
    public void NoObservation_OmitsTableShowsNote()
    {
        var snap = new WeatherSnapshot
        {
            ObservationAvailable = false,
            ObservationUnavailableNote = "No recent observation from any nearby station.",
            LocalityName = "Spring",
        };
        var en = StructuredReportRenderer.Render(Body(), Forecast(), snap, Imperial(), T("en"), C("en"), Utc, ReportKind.Scheduled, RenderNow);

        Assert.Contains("No recent observation from any nearby station.", en);
        Assert.DoesNotContain("Relative Humidity", en);  // CC table omitted
        Assert.Contains("Forecast for Spring", en);        // forecast grid still rendered
    }

    [Fact]
    public void RenderWelcome_IsWelcomeOnly_NamesLocalityAndScheduleTimes_NoWeather()
    {
        var welcome = StructuredReportRenderer.RenderWelcome(Imperial(), T("en"), C("en"), "Spring", Utc, new[] { 7, 12 });

        Assert.Contains("Welcome to WxReport!", welcome);
        Assert.Contains("Spring", welcome);             // locality named
        Assert.Contains("7 AM and 12 PM", welcome);     // localized + joined send times
        // No weather content in a welcome-only email.
        Assert.DoesNotContain("Reported Conditions", welcome);
        Assert.DoesNotContain("Forecast for", welcome);
        Assert.DoesNotContain("In summary:", welcome);
    }

    [Fact]
    public void WelcomePlainText_MatchesVocabularyAndNamesLocality()
    {
        var plain = StructuredReportRenderer.WelcomePlainText(Imperial(), T("en"), C("en"), "Spring", new[] { 7 });
        Assert.Contains("Welcome to WxReport!", plain);
        Assert.Contains("Spring", plain);
        Assert.DoesNotContain("<", plain);  // plain text, no markup
    }

    [Fact]
    public void CurrentConditions_ShowsStationSubtitle_WhenStationDiffersFromLocality()
    {
        // Spring's nearest station (KDWH, also in Spring) had no data, so the report
        // fell back to KIAH in Houston — a genuinely different town from the locality,
        // so the "at <city>, <airport>" attribution subtitle appears.
        var snap = new WeatherSnapshot
        {
            ObservationAvailable = true,
            LocalityName = "Spring",
            StationMunicipality = "Houston",
            StationName = "George Bush Intercontinental Airport",
            ObservationTimeUtc = AnchorUtc,
            StationIcao = "KIAH",
            SkyLayers = [new SkyLayer { Coverage = SkyCoverage.Overcast, HeightFeet = 3000 }],
            VisibilityStatuteMiles = 10,
            TemperatureCelsius = 31.0,
        };
        var en = StructuredReportRenderer.Render(
            Body(), Forecast(), snap, Imperial(), T("en"), C("en"), Utc, ReportKind.Scheduled, RenderNow);
        Assert.Contains("at Houston, George Bush Intercontinental Airport", en);

        // No subtitle when the station has no distinct municipality/name (default Observation()).
        var plain = StructuredReportRenderer.Render(
            Body(), Forecast(), Observation(), Imperial(), T("en"), C("en"), Utc, ReportKind.Scheduled, RenderNow);
        Assert.DoesNotContain(" at ", plain.Replace("S at 14 mph", "", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutChangeBand_SuppressesBand_KeepsClosing()
    {
        // A change summary normally renders the "What's changed:" band...
        var withBand = StructuredReportRenderer.Render(
            Body("{ch1}Storms moving in."), Forecast(), Observation(), Imperial(), T("en"), C("en"), Utc, ReportKind.Scheduled, RenderNow);
        Assert.Contains("What's changed:", withBand);

        // ...WX-182's band-free copy (a cached scheduled re-send narrates no change) drops
        // the band but keeps the closing prose.
        var bandFree = ReportWorker.WithoutChangeBand(Body("{ch1}Storms moving in."));
        var rendered = StructuredReportRenderer.Render(
            bandFree, Forecast(), Observation(), Imperial(), T("en"), C("en"), Utc, ReportKind.Scheduled, RenderNow);
        Assert.DoesNotContain("What's changed:", rendered);
        Assert.Contains("92°F", rendered);  // closing prose still rendered (Highs near 33.5°C → 92°F)
    }

    [Fact]
    public void WithoutChangeBand_EmptiesChangesAndSummary_StaysSerializable()
    {
        var bandFree = ReportWorker.WithoutChangeBand(Body("{ch1}Storms moving in."));
        Assert.Empty(bandFree.Changes);
        Assert.All(bandFree.Narrative.Values, n => Assert.Null(n.ChangeSummary));
        Assert.All(bandFree.Narrative.Values, n => Assert.False(string.IsNullOrWhiteSpace(n.Closing)));
        // Empty changes → no anchors required → blank changeSummary is valid; the band-free
        // body still serializes (it's what the cached send persists as its audit).
        var roundTrip = StructuredReportBody.Deserialize(bandFree.Serialize());
        Assert.Empty(roundTrip.Changes);
    }

    // ── WX-188: the Extended Forecast grid drops calendar days wholly past at send time ──

    // Two local days (UTC locality → local day = UTC day): a Jun 8 evening block that ends
    // at 00:00Z Jun 9, plus a full Jun 9. Mirrors the 2270 shape — a 00Z-run snapshot whose
    // first local day is the prior evening, wholly past once the report is built after midnight.
    private static ForecastSnapshotBody TwoLocalDayForecast() => new()
    {
        Blocks =
        [
            new() { StartUtc = new(2026, 6, 8, 18, 0, 0, DateTimeKind.Utc), SkyState = SkyState.Overcast, Obscuration = Obscuration.None, TemperatureCelsius = new(26, 33), WindKt = new(8, 15), PrecipExpectation = PrecipExpectation.Certain, PrecipPhenomenon = PrecipPhenomenon.Thunderstorm, SevereFlag = false },
            new() { StartUtc = new(2026, 6, 9, 0, 0, 0, DateTimeKind.Utc), SkyState = SkyState.PartlyCloudy, Obscuration = Obscuration.None, TemperatureCelsius = new(22, 28), WindKt = new(3, 8), PrecipExpectation = PrecipExpectation.None, PrecipPhenomenon = null, SevereFlag = false },
            new() { StartUtc = new(2026, 6, 9, 6, 0, 0, DateTimeKind.Utc), SkyState = SkyState.Clear, Obscuration = Obscuration.None, TemperatureCelsius = new(21, 27), WindKt = new(2, 6), PrecipExpectation = PrecipExpectation.None, PrecipPhenomenon = null, SevereFlag = false },
        ],
    };

    [Fact]
    public void Grid_DropsWhollyPastDay_LeadsWithCurrentDay()
    {
        // Built at 01:00Z Jun 9 (after local midnight): the Jun 8 evening block ended at
        // 00:00Z Jun 9, so Jun 8 is wholly past and must not appear; the grid leads with Jun 9.
        var nowUtc = new DateTime(2026, 6, 9, 1, 0, 0, DateTimeKind.Utc);
        var en = StructuredReportRenderer.Render(Body(), TwoLocalDayForecast(), Observation(), Imperial(), T("en"), C("en"), Utc, ReportKind.Unscheduled, nowUtc);

        Assert.Contains("Jun 9", en);
        // Wholly-past day gone from the grid. Assert the grid's exact date-cell form
        // ("ddd MMM d" → "Mon Jun 8"), not a bare "Jun 8": the header renders full-month
        // "June 8" and the WX-184 Current Conditions obs-time line renders comma'd
        // "Mon, Jun 8, 6:00 PM" — neither contains the space-separated grid form.
        Assert.DoesNotContain("Mon Jun 8", en);
        // Exactly one day row survives (one "High:" per grid row): dropping the leading day
        // leaves no empty/placeholder row, and the retained day is whole.
        Assert.Equal(1, CountOccurrences(en, "High:"));
    }

    [Fact]
    public void Grid_RetainedDay_HighLowSpanWholeCalendarDay_IncludingElapsedBlocks()
    {
        // A current day whose HOT block (33°C) is the already-elapsed morning, with cooler blocks
        // remaining. The day is retained (the afternoon block is still active); its High must be
        // the whole-day max (33°C → 91°F), NOT a remaining-blocks-only 28°C → 82°F. This guards
        // the rule "don't change the period over which a retained day's max/min are computed."
        var forecast = new ForecastSnapshotBody
        {
            Blocks =
            [
                new() { StartUtc = new(2026, 6, 9, 6, 0, 0, DateTimeKind.Utc), SkyState = SkyState.Clear, Obscuration = Obscuration.None, TemperatureCelsius = new(24, 33), WindKt = new(3, 8), PrecipExpectation = PrecipExpectation.None, PrecipPhenomenon = null, SevereFlag = false },
                new() { StartUtc = new(2026, 6, 9, 12, 0, 0, DateTimeKind.Utc), SkyState = SkyState.PartlyCloudy, Obscuration = Obscuration.None, TemperatureCelsius = new(23, 28), WindKt = new(4, 9), PrecipExpectation = PrecipExpectation.None, PrecipPhenomenon = null, SevereFlag = false },
                new() { StartUtc = new(2026, 6, 9, 18, 0, 0, DateTimeKind.Utc), SkyState = SkyState.Clear, Obscuration = Obscuration.None, TemperatureCelsius = new(22, 26), WindKt = new(2, 6), PrecipExpectation = PrecipExpectation.None, PrecipPhenomenon = null, SevereFlag = false },
            ],
        };
        var nowUtc = new DateTime(2026, 6, 9, 13, 0, 0, DateTimeKind.Utc);  // the morning block (ends 12:00Z) has already elapsed
        var en = StructuredReportRenderer.Render(Body(), forecast, Observation(), Imperial(), T("en"), C("en"), Utc, ReportKind.Scheduled, nowUtc);

        Assert.Contains("High: 91°F", en);        // whole-day max, drawn from the elapsed morning block
        Assert.DoesNotContain("High: 82°F", en);  // not a remaining-blocks-only max (that would be the bug)
    }

    // WX-234 / WX-236: three consecutive local days exercising the daily-extreme gating —
    //  • Jun 8 LEADING partial: afternoon + evening only (12Z, 18Z), no pre-dawn/morning → no Low.
    //  • Jun 9 FULL: all four blocks → both extremes.
    //  • Jun 10 TRAILING partial: pre-dawn + morning only (00Z, 06Z), no afternoon → no High.
    private static ForecastSnapshotBody PartialEdgeDaysForecast() => new()
    {
        Blocks =
        [
            new() { StartUtc = new(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc), SkyState = SkyState.Clear, Obscuration = Obscuration.None, TemperatureCelsius = new(25, 32), WindKt = new(4, 9), PrecipExpectation = PrecipExpectation.None, PrecipPhenomenon = null, SevereFlag = false },
            new() { StartUtc = new(2026, 6, 8, 18, 0, 0, DateTimeKind.Utc), SkyState = SkyState.Clear, Obscuration = Obscuration.None, TemperatureCelsius = new(23, 29), WindKt = new(3, 8), PrecipExpectation = PrecipExpectation.None, PrecipPhenomenon = null, SevereFlag = false },
            new() { StartUtc = new(2026, 6, 9, 0, 0, 0, DateTimeKind.Utc), SkyState = SkyState.Clear, Obscuration = Obscuration.None, TemperatureCelsius = new(19, 25), WindKt = new(2, 6), PrecipExpectation = PrecipExpectation.None, PrecipPhenomenon = null, SevereFlag = false },
            new() { StartUtc = new(2026, 6, 9, 6, 0, 0, DateTimeKind.Utc), SkyState = SkyState.Clear, Obscuration = Obscuration.None, TemperatureCelsius = new(18, 24), WindKt = new(3, 7), PrecipExpectation = PrecipExpectation.None, PrecipPhenomenon = null, SevereFlag = false },
            new() { StartUtc = new(2026, 6, 9, 12, 0, 0, DateTimeKind.Utc), SkyState = SkyState.Clear, Obscuration = Obscuration.None, TemperatureCelsius = new(26, 34), WindKt = new(5, 11), PrecipExpectation = PrecipExpectation.None, PrecipPhenomenon = null, SevereFlag = false },
            new() { StartUtc = new(2026, 6, 9, 18, 0, 0, DateTimeKind.Utc), SkyState = SkyState.Clear, Obscuration = Obscuration.None, TemperatureCelsius = new(22, 28), WindKt = new(3, 8), PrecipExpectation = PrecipExpectation.None, PrecipPhenomenon = null, SevereFlag = false },
            new() { StartUtc = new(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc), SkyState = SkyState.PartlyCloudy, Obscuration = Obscuration.None, TemperatureCelsius = new(20, 26), WindKt = new(2, 6), PrecipExpectation = PrecipExpectation.None, PrecipPhenomenon = null, SevereFlag = false },
            new() { StartUtc = new(2026, 6, 10, 6, 0, 0, DateTimeKind.Utc), SkyState = SkyState.Clear, Obscuration = Obscuration.None, TemperatureCelsius = new(21, 27), WindKt = new(3, 7), PrecipExpectation = PrecipExpectation.None, PrecipPhenomenon = null, SevereFlag = false },
        ],
    };

    [Fact]
    public void Grid_TemperatureCell_RendersLowBeforeHigh()  // WX-236
    {
        // The grid's daypart columns run chronologically (00-06 … 18-24); the Low we report is this
        // morning's minimum — the earliest point of the day — so the pair must read Low above High.
        // UniformClearForecast is one FULL day, so both extremes are numeric (no em dash to confuse
        // the ordering): low 18°C → 64°F, high 31°C → 88°F.
        var en = StructuredReportRenderer.Render(Body(), UniformClearForecast(), Observation(), Imperial(), T("en"), C("en"), Utc, ReportKind.Scheduled, RenderNow);

        int lowIdx = en.IndexOf("Low: 64°F", StringComparison.Ordinal);
        int highIdx = en.IndexOf("High: 88°F", StringComparison.Ordinal);
        Assert.True(lowIdx >= 0 && highIdx >= 0, "both extremes present in the grid");
        Assert.True(lowIdx < highIdx, "Low must render above High in the grid temperature cell");
    }

    [Fact]
    public void Grid_PartialDay_SuppressesUnsupportedExtreme_AsEmDash()  // WX-234
    {
        var en = StructuredReportRenderer.Render(Body(), PartialEdgeDaysForecast(), Observation(), Imperial(), T("en"), C("en"), Utc, ReportKind.Scheduled, RenderNow);

        // Jun 8 LEADING partial (afternoon+evening only): a daytime high (32°C → 90°F), no overnight low.
        Assert.Contains("90°F", en);
        Assert.Contains("Low: —", en);        // its suppressed overnight low is an em dash
        // Jun 10 TRAILING partial (pre-dawn+morning only): a dawn low (20°C → 68°F), no daytime high —
        // the WX-234 pseudo-high case.
        Assert.Contains("68°F", en);
        Assert.Contains("High: —", en);       // its suppressed daytime high is an em dash
        // The FULL middle day (Jun 9) shows both numeric extremes — never an em dash.
        Assert.Contains("Low: 64°F", en);     // 18°C
        Assert.Contains("High: 93°F", en);    // 34°C
    }

    [Fact]
    public void Grid_And_Summary_GateExtremesByTheSameRule()  // WX-234
    {
        // Same body, one DayPartBands rule: the grid's numeric (non-em-dash) highs/lows must equal the
        // counts TemperatureRangeSummarizer.DailyHighsLows feeds the prose, so the two cannot drift.
        var body = PartialEdgeDaysForecast();
        var en = StructuredReportRenderer.Render(Body(), body, Observation(), Imperial(), T("en"), C("en"), Utc, ReportKind.Scheduled, RenderNow);
        var (highs, lows) = TemperatureRangeSummarizer.DailyHighsLows(body, RenderNow, Utc);

        int rows = CountOccurrences(en, "High:");               // one Low/High label pair per grid row
        int emDashHighs = CountOccurrences(en, "High: —");
        int emDashLows = CountOccurrences(en, "Low: —");
        Assert.Equal(highs.Count, rows - emDashHighs);          // grid numeric highs == summary highs
        Assert.Equal(lows.Count, rows - emDashLows);            // grid numeric lows  == summary lows
    }

    [Fact]
    public void DayPartBands_GatesAfternoonAndDawnWindow()  // WX-234 (shared predicate)
    {
        // The afternoon (peak-heating) 12:00 band holds the daily high; the morning-half bands
        // (pre-dawn 00:00, morning 06:00) bracket the dawn low. Evening (18:00) holds neither.
        Assert.True(DayPartBands.HasAfternoon(12));
        Assert.False(DayPartBands.HasAfternoon(0));
        Assert.False(DayPartBands.HasAfternoon(6));
        Assert.False(DayPartBands.HasAfternoon(18));
        Assert.True(DayPartBands.HasDawnWindow(0));
        Assert.True(DayPartBands.HasDawnWindow(6));
        Assert.False(DayPartBands.HasDawnWindow(12));
        Assert.False(DayPartBands.HasDawnWindow(18));
    }

    [Fact]
    public void Grid_OmittedEntirely_WhenEveryDayIsWhollyPast()
    {
        // Every block fully elapsed → no day survives → the section is omitted (no header-only grid).
        var nowUtc = new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc);
        var en = StructuredReportRenderer.Render(Body(), Forecast(), Observation(), Imperial(), T("en"), C("en"), Utc, ReportKind.Scheduled, nowUtc);

        Assert.DoesNotContain("Forecast for Spring", en);
    }

    [Fact]
    public void Grid_DayWhoseLastBlockEndsExactlyAtNow_IsDropped()
    {
        // Exact boundary: the Jun 8 evening block ends at 00:00:00Z Jun 9. With nowUtc set to
        // that instant, StartUtc + 6h == nowUtc, which the strict `> nowUtc` active-window test
        // (SevereBlocks.NotFullyElapsed) treats as fully elapsed → Jun 8 is dropped and the grid
        // leads with Jun 9 (the block whose window closes exactly at the send instant is past).
        var nowUtc = new DateTime(2026, 6, 9, 0, 0, 0, DateTimeKind.Utc);
        var en = StructuredReportRenderer.Render(Body(), TwoLocalDayForecast(), Observation(), Imperial(), T("en"), C("en"), Utc, ReportKind.Unscheduled, nowUtc);

        Assert.DoesNotContain("Mon Jun 8", en);  // dropped from the grid (see Grid_DropsWhollyPastDay for the substring-form rationale)
        Assert.Contains("Jun 9", en);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }
}