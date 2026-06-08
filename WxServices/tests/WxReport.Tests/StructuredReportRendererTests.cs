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
        Language = "English",
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
        Language = "English",
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
        Language = "Spanish",
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
            ["en"] = new() { ChangeSummary = changeSummary, CurrentConditions = "n/a", ExtendedForecast = "n/a", Closing = ClosingTokens },
            ["es"] = new() { ChangeSummary = changeSummary is null ? null : "{ch1}Tormentas en camino.", CurrentConditions = "n/d", ExtendedForecast = "n/d", Closing = "Máximas cerca de {q:temp:33.5}, lluvia de {q:precip_mm:12}." },
        },
    };

    [Fact]
    public void SameInput_ImperialVsMetric_DiffersOnlyByUnits()
    {
        var en = StructuredReportRenderer.Render(Body(), Forecast(), Observation(), Imperial(), Utc, isUnscheduled: false);
        var me = StructuredReportRenderer.Render(Body(), Forecast(), Observation(), Metric(), Utc, isUnscheduled: false);

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
        var en = StructuredReportRenderer.Render(Body(), Forecast(), Observation(), Imperial(), Utc, isUnscheduled: false);
        Assert.Contains("9:00 PM", en);  // 21:00Z in UTC locality
    }

    [Fact]
    public void ExtendedForecast_OneRowPerLocalDay_WithDailyHiLo()
    {
        var en = StructuredReportRenderer.Render(Body(), Forecast(), Observation(), Imperial(), Utc, isUnscheduled: false);

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
    public void Conditions_ComposeFromSkyPrecipSevere()
    {
        var en = StructuredReportRenderer.Render(Body(), Forecast(), Observation(), Imperial(), Utc, isUnscheduled: false);
        // Day 1: Overcast (max sky), Certain rain at 18Z (evening) → "Overcast, evening rain expected".
        Assert.Contains("Overcast, evening rain expected", en);
        // Day 2: PartlyCloudy, no precip → just the sky phrase, not the clear-day "and dry".
        Assert.Contains("Partly cloudy", en);
        Assert.DoesNotContain("Partly cloudy and dry", en);
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
        var en = StructuredReportRenderer.Render(Body(), Forecast(), snap, Imperial(), Utc, isUnscheduled: false);
        Assert.Contains("Low mostly cloudy", en);  // Broken@4000 ft → Low prefix; NSC ignored
    }

    [Fact]
    public void CurrentConditions_TableFromObservation()
    {
        var en = StructuredReportRenderer.Render(Body(), Forecast(), Observation(), Imperial(), Utc, isUnscheduled: false);
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
        var es = StructuredReportRenderer.Render(Body(), Forecast(), Observation(), Spanish(), Utc, isUnscheduled: false);
        Assert.Contains("Condiciones actuales", es);
        Assert.Contains("En resumen:", es);
        Assert.Contains("Pronóstico para Spring", es);
        Assert.Contains("Máximas cerca de 34°C", es);  // es closing prose, metric temp
        Assert.DoesNotContain("Current Conditions", es);
    }

    [Fact]
    public void ChangeBand_RendersOnlyWhenNarrativeCarriesOne()
    {
        var scheduled = StructuredReportRenderer.Render(Body(), Forecast(), Observation(), Imperial(), Utc, isUnscheduled: false);
        Assert.DoesNotContain("What's changed:", scheduled);
        Assert.DoesNotContain("Unscheduled update", scheduled);

        var unscheduled = StructuredReportRenderer.Render(
            Body("{ch1}Thunderstorms now expected this afternoon."), Forecast(), Observation(), Imperial(), Utc, isUnscheduled: true);
        Assert.Contains("What's changed:", unscheduled);
        Assert.Contains("Thunderstorms now expected this afternoon.", unscheduled);
        Assert.Contains("Unscheduled update", unscheduled);
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
        var en = StructuredReportRenderer.Render(Body(), Forecast(), snap, Imperial(), Utc, isUnscheduled: false);

        Assert.Contains("No recent observation from any nearby station.", en);
        Assert.DoesNotContain("Relative Humidity", en);  // CC table omitted
        Assert.Contains("Forecast for Spring", en);        // forecast grid still rendered
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