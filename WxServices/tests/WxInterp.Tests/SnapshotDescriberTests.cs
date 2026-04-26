// Unit tests for SnapshotDescriber.
// Verifies that key weather fields appear correctly in the text description
// sent to the Claude API.

using WxInterp;

using WxReport.Svc;

using Xunit;

namespace WxInterp.Tests;

public class SnapshotDescriberTests
{
    private static WeatherSnapshot Base() => new()
    {
        StationIcao = "KDWH",
        LocalityName = "Spring",
        ObservationTimeUtc = new DateTime(2026, 3, 27, 12, 55, 0, DateTimeKind.Utc),
        IsAutomated = true,
        WindDirectionDeg = 180,
        WindIsVariable = false,
        WindSpeedKt = 12,
        WindGustKt = null,
        Cavok = false,
        VisibilityStatuteMiles = 10.0,
        TemperatureCelsius = 22.0,
        TemperatureFahrenheit = 71.6,
        DewPointCelsius = 15.0,
        AltimeterInHg = 29.98,
        SkyLayers = [],
        WeatherPhenomena = [],
        ForecastPeriods = [],
    };

    // ── station / observation ─────────────────────────────────────────────────

    [Fact]
    public void Describe_IncludesStationAndLocality()
    {
        var text = SnapshotDescriber.Describe(Base(), TimeZoneInfo.Utc);
        Assert.Contains("KDWH", text);
        Assert.Contains("Spring", text);
    }

    [Fact]
    public void Describe_IncludesObservationDate()
        => Assert.Contains("2026-03-27", SnapshotDescriber.Describe(Base(), TimeZoneInfo.Utc));

    [Fact]
    public void Describe_AutomatedStation_IncludesAutomatedTag()
        => Assert.Contains("automated", SnapshotDescriber.Describe(Base(), TimeZoneInfo.Utc));

    [Fact]
    public void Describe_ManualStation_DoesNotIncludeAutomatedTag()
    {
        var snap = new WeatherSnapshot
        {
            StationIcao = "KDWH",
            LocalityName = "Spring",
            ObservationTimeUtc = new DateTime(2026, 3, 27, 12, 55, 0, DateTimeKind.Utc),
            IsAutomated = false,
            VisibilityStatuteMiles = 10.0,
            SkyLayers = [],
            WeatherPhenomena = [],
            ForecastPeriods = [],
        };
        Assert.DoesNotContain("automated", SnapshotDescriber.Describe(snap, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Describe_IncludesCurrentDateTime()
        // Current date/time line is essential so Claude knows what day it is
        => Assert.Contains("Current date/time:", SnapshotDescriber.Describe(Base(), TimeZoneInfo.Utc));

    // ── wind ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Describe_Wind_IncludesDirectionAndSpeed()
    {
        var text = SnapshotDescriber.Describe(Base(), TimeZoneInfo.Utc);
        Assert.Contains("180", text);
        Assert.Contains("12 kt", text);
    }

    [Fact]
    public void Describe_Wind_WithGust_IncludesGust()
    {
        var snap = new WeatherSnapshot
        {
            StationIcao = "KDWH",
            ObservationTimeUtc = new DateTime(2026, 3, 27, 12, 55, 0, DateTimeKind.Utc),
            WindDirectionDeg = 180,
            WindSpeedKt = 12,
            WindGustKt = 22,
            VisibilityStatuteMiles = 10.0,
            SkyLayers = [],
            WeatherPhenomena = [],
            ForecastPeriods = [],
        };
        Assert.Contains("gusting 22 kt", SnapshotDescriber.Describe(snap, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Describe_Wind_Variable_SaysVariable()
    {
        var snap = new WeatherSnapshot
        {
            StationIcao = "KDWH",
            ObservationTimeUtc = new DateTime(2026, 3, 27, 12, 55, 0, DateTimeKind.Utc),
            WindIsVariable = true,
            WindSpeedKt = 8,
            VisibilityStatuteMiles = 10.0,
            SkyLayers = [],
            WeatherPhenomena = [],
            ForecastPeriods = [],
        };
        Assert.Contains("variable", SnapshotDescriber.Describe(snap, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Describe_Wind_Calm_SaysCalm()
    {
        var snap = new WeatherSnapshot
        {
            StationIcao = "KDWH",
            ObservationTimeUtc = new DateTime(2026, 3, 27, 12, 55, 0, DateTimeKind.Utc),
            WindSpeedKt = 0,
            VisibilityStatuteMiles = 10.0,
            SkyLayers = [],
            WeatherPhenomena = [],
            ForecastPeriods = [],
        };
        Assert.Contains("calm", SnapshotDescriber.Describe(snap, TimeZoneInfo.Utc));
    }

    // ── visibility ────────────────────────────────────────────────────────────

    [Fact]
    public void Describe_Visibility_IncludesValueAndUnit()
    {
        var text = SnapshotDescriber.Describe(Base(), TimeZoneInfo.Utc);
        Assert.Contains("10", text);
        Assert.Contains("SM", text);
    }

    [Fact]
    public void Describe_Cavok_SaysCavok()
    {
        var snap = new WeatherSnapshot
        {
            StationIcao = "KDWH",
            ObservationTimeUtc = new DateTime(2026, 3, 27, 12, 55, 0, DateTimeKind.Utc),
            Cavok = true,
            SkyLayers = [],
            WeatherPhenomena = [],
            ForecastPeriods = [],
        };
        Assert.Contains("CAVOK", SnapshotDescriber.Describe(snap, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Describe_LessThanVisibility_IncludesLessThanSymbol()
    {
        var snap = new WeatherSnapshot
        {
            StationIcao = "KDWH",
            ObservationTimeUtc = new DateTime(2026, 3, 27, 12, 55, 0, DateTimeKind.Utc),
            VisibilityStatuteMiles = 0.25,
            VisibilityLessThan = true,
            SkyLayers = [],
            WeatherPhenomena = [],
            ForecastPeriods = [],
        };
        Assert.Contains("<", SnapshotDescriber.Describe(snap, TimeZoneInfo.Utc));
    }

    // ── sky ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Describe_NoLayers_SaysClear()
        => Assert.Contains("clear", SnapshotDescriber.Describe(Base(), TimeZoneInfo.Utc), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Describe_BrokenLayer_IncludesHeightAndCoverage()
    {
        var snap = new WeatherSnapshot
        {
            StationIcao = "KDWH",
            ObservationTimeUtc = new DateTime(2026, 3, 27, 12, 55, 0, DateTimeKind.Utc),
            VisibilityStatuteMiles = 10.0,
            SkyLayers = [new SkyLayer { Coverage = SkyCoverage.Broken, HeightFeet = 2500 }],
            WeatherPhenomena = [],
            ForecastPeriods = [],
        };
        var text = SnapshotDescriber.Describe(snap, TimeZoneInfo.Utc);
        Assert.Contains("Broken", text);
        Assert.Contains("2500", text);
    }

    [Fact]
    public void Describe_CbLayer_IncludesCumulonimbus()
    {
        var snap = new WeatherSnapshot
        {
            StationIcao = "KDWH",
            ObservationTimeUtc = new DateTime(2026, 3, 27, 12, 55, 0, DateTimeKind.Utc),
            VisibilityStatuteMiles = 10.0,
            SkyLayers = [new SkyLayer { Coverage = SkyCoverage.Few, HeightFeet = 3000, CloudType = CloudType.Cumulonimbus }],
            WeatherPhenomena = [],
            ForecastPeriods = [],
        };
        Assert.Contains("Cumulonimbus", SnapshotDescriber.Describe(snap, TimeZoneInfo.Utc));
    }

    // ── temperature ───────────────────────────────────────────────────────────

    [Fact]
    public void Describe_IncludesFahrenheitAndCelsius()
    {
        var text = SnapshotDescriber.Describe(Base(), TimeZoneInfo.Utc);
        Assert.Contains("°F", text);
        Assert.Contains("°C", text);
    }

    [Fact]
    public void Describe_IncludesDewPoint()
    {
        var text = SnapshotDescriber.Describe(Base(), TimeZoneInfo.Utc);
        Assert.Contains("Dew point", text);
        Assert.Contains("15", text);
    }

    // ── altimeter ─────────────────────────────────────────────────────────────

    [Fact]
    public void Describe_IncludesAltimeterInHg()
    {
        var text = SnapshotDescriber.Describe(Base(), TimeZoneInfo.Utc);
        Assert.Contains("29.98", text);
        Assert.Contains("inHg", text);
    }

    // ── forecast ─────────────────────────────────────────────────────────────

    [Fact]
    public void Describe_NoTaf_SaysForecastNotAvailable()
    {
        var snap = new WeatherSnapshot
        {
            StationIcao = "KDWH",
            ObservationTimeUtc = new DateTime(2026, 3, 27, 12, 55, 0, DateTimeKind.Utc),
            VisibilityStatuteMiles = 10.0,
            TafStationIcao = null,
            SkyLayers = [],
            WeatherPhenomena = [],
            ForecastPeriods = [],
        };
        Assert.Contains("not available", SnapshotDescriber.Describe(snap, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Describe_WithTaf_IncludesTafStationAndPeriodType()
    {
        var snap = new WeatherSnapshot
        {
            StationIcao = "KDWH",
            ObservationTimeUtc = new DateTime(2026, 3, 27, 12, 55, 0, DateTimeKind.Utc),
            VisibilityStatuteMiles = 10.0,
            TafStationIcao = "KIAH",
            SkyLayers = [],
            WeatherPhenomena = [],
            ForecastPeriods =
            [
                new ForecastPeriod
                {
                    ChangeType       = ForecastChangeType.Base,
                    ValidFromUtc     = new DateTime(2026, 3, 27, 12, 0, 0, DateTimeKind.Utc),
                    ValidToUtc       = new DateTime(2026, 3, 28, 12, 0, 0, DateTimeKind.Utc),
                    WindSpeedKt      = 10,
                    WindDirectionDeg = 180,
                    SkyLayers        = [],
                    WeatherPhenomena = [],
                },
            ],
        };
        var text = SnapshotDescriber.Describe(snap, TimeZoneInfo.Utc);
        Assert.Contains("KIAH", text);
        Assert.Contains("Base", text);
    }

    [Fact]
    public void Describe_ForecastPeriod_IncludesFullDate()
    {
        // Forecast period times must include the date so Claude knows the day of week
        var snap = new WeatherSnapshot
        {
            StationIcao = "KDWH",
            ObservationTimeUtc = new DateTime(2026, 3, 27, 12, 55, 0, DateTimeKind.Utc),
            VisibilityStatuteMiles = 10.0,
            TafStationIcao = "KIAH",
            SkyLayers = [],
            WeatherPhenomena = [],
            ForecastPeriods =
            [
                new ForecastPeriod
                {
                    ChangeType       = ForecastChangeType.Base,
                    ValidFromUtc     = new DateTime(2026, 3, 27, 12, 0, 0, DateTimeKind.Utc),
                    ValidToUtc       = new DateTime(2026, 3, 28, 12, 0, 0, DateTimeKind.Utc),
                    SkyLayers        = [],
                    WeatherPhenomena = [],
                },
            ],
        };
        var text = SnapshotDescriber.Describe(snap, TimeZoneInfo.Utc);
        Assert.Contains("2026-03-27", text);
        Assert.Contains("2026-03-28", text);
    }
}