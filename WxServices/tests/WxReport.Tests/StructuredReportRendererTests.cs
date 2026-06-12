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

    private static Recipient Spanish() => new()
    {
        RecipientId = "t-es",
        Name = "Prueba",
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
            ["es"] = new() { ChangeSummary = changeSummary is null ? null : "{ch1}Tormentas en camino.", Closing = "Máximas cerca de {q:temp:33.5}, lluvia de {q:precip_mm:12}." },
        },
    };

    [Fact]
    public void SameInput_ImperialVsMetric_DiffersOnlyByUnits()
    {
        var en = StructuredReportRenderer.Render(Body(), Forecast(), Observation(), Imperial(), "en", Utc, ReportKind.Scheduled);
        var me = StructuredReportRenderer.Render(Body(), Forecast(), Observation(), Metric(), "en", Utc, ReportKind.Scheduled);

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
    public void TimeToken_RendersInLocalityTimeAndLocale()
    {
        var en = StructuredReportRenderer.Render(Body(), Forecast(), Observation(), Imperial(), "en", Utc, ReportKind.Scheduled);
        Assert.Contains("9:00 PM", en);  // 21:00Z in UTC locality
    }

    [Fact]
    public void ExtendedForecast_OneRowPerLocalDay_WithDailyHiLo()
    {
        var en = StructuredReportRenderer.Render(Body(), Forecast(), Observation(), Imperial(), "en", Utc, ReportKind.Scheduled);

        // Two local days → two High/Low pairs.
        Assert.Equal(2, CountOccurrences(en, "High:"));
        // Day 1 (Jun 8): max 33°C→91°F, min 24°C→75°F. Day 2 (Jun 9): 28°C→82°F, 20°C→68°F.
        Assert.Contains("91°F", en);
        Assert.Contains("75°F", en);
        Assert.Contains("82°F", en);
        Assert.Contains("68°F", en);
        Assert.Contains("Forecast for Spring", en);
    }

    [Fact]
    public void Conditions_SplitDayIntoEpisodes_NotCollapsedToPeak()
    {
        var en = StructuredReportRenderer.Render(Body(), Forecast(), Observation(), Imperial(), "en", Utc, ReportKind.Scheduled);
        // WX-148 Class 2: Day 1 has two episodes — a thunderstorm at 12Z (afternoon)
        // then rain at 18Z (evening) — and BOTH must surface as labeled lines rather
        // than collapse to the single highest-expectation one.
        Assert.Contains("Afternoon — storms likely", en);
        Assert.Contains("Evening — rain expected", en);
        // Day 2: PartlyCloudy, no precip → just the sky phrase, not the clear-day "and dry".
        Assert.Contains("Partly cloudy", en);
        Assert.DoesNotContain("Partly cloudy and dry", en);
    }

    [Fact]
    public void Conditions_SevereDay_TimedSentenceWithSecondaryEpisode()
    {
        // A severe day breaks the labeled-line rhythm into a single sentence: the
        // severe clause leads with its timing, the secondary episode joined by "then".
        var en = StructuredReportRenderer.Render(Body(), SevereForecast(), Observation(), Imperial(), "en", Utc, ReportKind.Scheduled);
        Assert.Contains("Severe storms likely in the afternoon, then rain in the evening", en);
    }

    [Fact]
    public void Conditions_SevereWithoutPrecip_StillSurfacesHazard()
    {
        // Regression guard: a severe block with no precipitation forms no episode, but
        // must not collapse to a benign sky phrase — the day-level severe path leads
        // with a generic hazard line timed by the severe block.
        var en = StructuredReportRenderer.Render(Body(), SevereNoPrecipForecast(), Observation(), Imperial(), "en", Utc, ReportKind.Scheduled);
        // Generic "Severe weather" — not storm-specific — because the severe block carries no precip (a wind event).
        Assert.Contains("Severe weather likely in the afternoon", en);
    }

    [Fact]
    public void Conditions_EpisodeSpanningBuckets_RendersRangeLabel()
    {
        // A single rain episode spanning the 06Z and 12Z blocks crosses the
        // morning/afternoon boundary, so it is timed as a range, not one bucket.
        var en = StructuredReportRenderer.Render(Body(), SpanningForecast(), Observation(), Imperial(), "en", Utc, ReportKind.Scheduled);
        Assert.Contains("Morning–afternoon — rain likely", en);
    }

    [Fact]
    public void RenderDegraded_LeadsWithHazardBanner_NoSummaryNoChangeBand()
    {
        // WX-148 tier-aware degrade: a narrative-less hazard report — deterministic
        // banner + conditions + grid, with NO What's-changed band and NO summary
        // (a summary we cannot deliver is left out, not apologized for).
        var nowUtc = new DateTime(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc);  // within the 12–18Z severe block
        var en = StructuredReportRenderer.RenderDegraded(SevereForecast(), Observation(), Imperial(), "en", Utc, nowUtc);
        // Full deterministic banner (SevereForecast is convective → "Severe storms"); weekday
        // computed so the assertion can't drift, and "in your forecast" is banner-specific
        // (the grid cell says "Severe storms likely …", never "in your forecast").
        var expectedDay = nowUtc.ToString("dddd", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        Assert.Contains($"Severe storms in your forecast — {expectedDay} afternoon.", en);
        Assert.Contains("Current Conditions", en);              // conditions table present
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
        var en = StructuredReportRenderer.Render(Body(), Forecast(), snap, Imperial(), "en", Utc, ReportKind.Scheduled);
        Assert.Contains("Low mostly cloudy", en);  // Broken@4000 ft → Low prefix; NSC ignored
    }

    [Fact]
    public void CurrentConditions_TableFromObservation()
    {
        var en = StructuredReportRenderer.Render(Body(), Forecast(), Observation(), Imperial(), "en", Utc, ReportKind.Scheduled);
        Assert.Contains("Current Conditions", en);
        Assert.Contains("Low overcast", en);          // overcast at 3000 ft
        Assert.Contains("S at 14 mph, gusting 25 mph", en);  // 180°, 12 kt→14 mph, gust 22 kt→25 mph
        Assert.Contains("Light rain", en);
        Assert.Contains("88°F", en);                   // 31°C
        Assert.Contains("Good", en);                   // 10 SM visibility band
    }

    [Fact]
    public void Spanish_UsesEsNarrativeAndLabels()
    {
        var es = StructuredReportRenderer.Render(Body(), Forecast(), Observation(), Spanish(), "es", Utc, ReportKind.Scheduled);
        Assert.Contains("Condiciones actuales", es);
        Assert.Contains("En resumen:", es);
        Assert.Contains("Pronóstico para Spring", es);
        Assert.Contains("Máximas cerca de 34°C", es);  // es closing prose, metric temp
        Assert.DoesNotContain("Current Conditions", es);
    }

    [Fact]
    public void ChangeBand_RendersOnlyWhenNarrativeCarriesOne()
    {
        var scheduled = StructuredReportRenderer.Render(Body(), Forecast(), Observation(), Imperial(), "en", Utc, ReportKind.Scheduled);
        Assert.DoesNotContain("What's changed:", scheduled);
        Assert.DoesNotContain("Unscheduled Update", scheduled);
        Assert.Contains("Scheduled Report", scheduled);   // the type label is always shown (WX-154)

        var unscheduled = StructuredReportRenderer.Render(
            Body("{ch1}Thunderstorms now expected this afternoon."), Forecast(), Observation(), Imperial(), "en", Utc, ReportKind.Unscheduled);
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
        var en = StructuredReportRenderer.Render(Body(), Forecast(), snap, Imperial(), "en", Utc, ReportKind.Scheduled);

        Assert.Contains("No recent observation from any nearby station.", en);
        Assert.DoesNotContain("Relative Humidity", en);  // CC table omitted
        Assert.Contains("Forecast for Spring", en);        // forecast grid still rendered
    }

    [Fact]
    public void RenderWelcome_IsWelcomeOnly_NamesLocalityAndScheduleTimes_NoWeather()
    {
        var welcome = StructuredReportRenderer.RenderWelcome(Imperial(), "en", "Spring", Utc, new[] { 7, 12 });

        Assert.Contains("Welcome to WxReport!", welcome);
        Assert.Contains("Spring", welcome);             // locality named
        Assert.Contains("7 AM and 12 PM", welcome);     // localized + joined send times
        // No weather content in a welcome-only email.
        Assert.DoesNotContain("Current Conditions", welcome);
        Assert.DoesNotContain("Forecast for", welcome);
        Assert.DoesNotContain("In summary:", welcome);
    }

    [Fact]
    public void RenderWelcome_LocalizesToSpanish()
    {
        var welcome = StructuredReportRenderer.RenderWelcome(Spanish(), "es", "Spring", Utc, new[] { 7 });
        Assert.Contains("¡Bienvenido a WxReport!", welcome);
        Assert.Contains("Spring", welcome);
        Assert.DoesNotContain("Welcome to WxReport!", welcome);
    }

    [Fact]
    public void WelcomePlainText_MatchesVocabularyAndNamesLocality()
    {
        var plain = StructuredReportRenderer.WelcomePlainText(Imperial(), "en", "Spring", new[] { 7 });
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
            Body(), Forecast(), snap, Imperial(), "en", Utc, ReportKind.Scheduled);
        Assert.Contains("at Houston, George Bush Intercontinental Airport", en);

        // No subtitle when the station has no distinct municipality/name (default Observation()).
        var plain = StructuredReportRenderer.Render(
            Body(), Forecast(), Observation(), Imperial(), "en", Utc, ReportKind.Scheduled);
        Assert.DoesNotContain(" at ", plain.Replace("S at 14 mph", "", StringComparison.Ordinal));
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