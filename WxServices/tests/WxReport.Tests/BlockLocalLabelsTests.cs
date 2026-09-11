using MetarParser.Data.Entities;

using WxInterp;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

/// <summary>
/// WX-504 — the per-report <c>block_local_labels</c> (<c>BlockLocalLabels.Build</c>) injected into the
/// reconciler user message. Uses the real US Central zone rather than a fixed offset, because the
/// guarantee under test is DST-correctness: a block's label comes from the local wall clock, never from
/// a fixed offset and never from <c>startUtc + 6h</c>.
/// </summary>
public class BlockLocalLabelsTests
{
    private static readonly TimeZoneInfo Central = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Central Standard Time" : "America/Chicago");

    private static ForecastSnapshotBlock Block(DateTime startUtc) => new()
    {
        StartUtc = startUtc,
        SkyState = SkyState.Overcast,
        Obscuration = Obscuration.None,
        TemperatureCelsius = new MinMax<double>(15.0, 25.0),
        WindKt = new MinMax<int>(3, 8),
        PrecipExpectation = PrecipExpectation.None,
        SevereFlag = false,
    };

    private static ForecastSnapshotBody Body(params DateTime[] starts) =>
        new() { Blocks = starts.Select(Block).ToList() };

    private static DateTime Utc(int month, int day, int hour) =>
        new(2026, month, day, hour, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Build_LabelsAnOrdinaryDaylightTimeDay_FromTheLocalClock()
    {
        // Friday 2026-09-11 in CDT (UTC-5): local 00/06/12/18 fall at 05Z/11Z/17Z/23Z. Supplied out of
        // order so the chronological sort is exercised. A fixed CST offset would call 05Z Thursday evening.
        var result = BlockLocalLabels.Build(
            Body(Utc(9, 11, 17), Utc(9, 11, 5), Utc(9, 11, 23), Utc(9, 11, 11)), Central);

        Assert.Contains("block_local_labels", result);
        Assert.Contains("2026-09-11T05:00:00Z = Fri 2026-09-11 early hours (00:00-06:00)", result);
        Assert.Contains("2026-09-11T11:00:00Z = Fri 2026-09-11 morning (06:00-12:00)", result);
        Assert.Contains("2026-09-11T17:00:00Z = Fri 2026-09-11 afternoon (12:00-18:00)", result);
        Assert.Contains("2026-09-11T23:00:00Z = Fri 2026-09-11 evening (18:00-24:00)", result);

        // Every consecutive pair, not a sample: the input order (17, 05, 23, 11) satisfies 05<11 and 17<23 by
        // itself, so checking only those two pairs passes with the sort deleted.
        var positions = new[] { "T05:00:00Z", "T11:00:00Z", "T17:00:00Z", "T23:00:00Z" }
            .Select(key => result.IndexOf(key, StringComparison.Ordinal))
            .ToArray();
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.Order().ToArray(), positions);
    }

    [Fact]
    public void Build_SpringForward_TheFiveHourEarlyBlockKeepsItsLocalSpan()
    {
        // Sunday 2026-03-08: 00:00 CST = 06Z, the clocks jump at 02:00, so 06:00 CDT = 11Z — the early-hours
        // block is five UTC hours long. Its span must still read 00:00-06:00, not 00:00-07:00.
        var result = BlockLocalLabels.Build(Body(Utc(3, 8, 6), Utc(3, 8, 11), Utc(3, 8, 17)), Central);

        Assert.Contains("2026-03-08T06:00:00Z = Sun 2026-03-08 early hours (00:00-06:00)", result);
        Assert.Contains("2026-03-08T11:00:00Z = Sun 2026-03-08 morning (06:00-12:00)", result);
        Assert.Contains("2026-03-08T17:00:00Z = Sun 2026-03-08 afternoon (12:00-18:00)", result);
    }

    [Fact]
    public void Build_FallBack_TheSevenHourEarlyBlockKeepsItsLocalSpan()
    {
        // Sunday 2026-11-01: 00:00 CDT = 05Z, the clocks fall back at 02:00, so 06:00 CST = 12Z — the
        // early-hours block is seven UTC hours long. Its span must still read 00:00-06:00, not 00:00-05:00,
        // and the 00Z block that follows belongs to Sunday evening, not Monday.
        var result = BlockLocalLabels.Build(
            Body(Utc(11, 1, 5), Utc(11, 1, 12), Utc(11, 1, 18), Utc(11, 2, 0)), Central);

        Assert.Contains("2026-11-01T05:00:00Z = Sun 2026-11-01 early hours (00:00-06:00)", result);
        Assert.Contains("2026-11-01T12:00:00Z = Sun 2026-11-01 morning (06:00-12:00)", result);
        Assert.Contains("2026-11-02T00:00:00Z = Sun 2026-11-01 evening (18:00-24:00)", result);
    }

    [Fact]
    public void BuildUserMessage_CarriesTheBlockLabels()
    {
        // Wiring guard: Build can be correct and still never reach the model. Assert on the message the
        // reconciler actually sends, not on Build.
        var observation = new WeatherSnapshot
        {
            StationIcao = "KDWH",
            LocalityName = "Spring",
            ObservationTimeUtc = Utc(9, 11, 4),
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

        var message = ForecastReconciler.BuildUserMessage(
            observation, Body(Utc(9, 11, 5), Utc(9, 11, 11)), gfsModelRunUtc: null,
            tafIssuanceUtc: null, tafValidToUtc: null, prior: null, Central, nowUtc: Utc(9, 11, 5),
            changedSinceLastSend: [], dayNameReference: "");

        Assert.Contains("block_local_labels", message);
        Assert.Contains("2026-09-11T05:00:00Z = Fri 2026-09-11 early hours (00:00-06:00)", message);
    }

    [Fact]
    public void Build_KeysEachLabelByTheStartUtcStringTheSnapshotJsonCarries()
    {
        // The model pairs a label with a block by its startUtc string, so the key must be byte-identical to
        // the value in the snapshot JSON the same message carries.
        var body = Body(Utc(9, 11, 5), Utc(9, 11, 11));
        var json = body.Serialize();
        var labels = BlockLocalLabels.Build(body, Central);

        foreach (var key in new[] { "2026-09-11T05:00:00Z", "2026-09-11T11:00:00Z" })
        {
            Assert.Contains($"\"startUtc\":\"{key}\"", json);
            Assert.Contains(key + " = ", labels);
        }
    }

    [Fact]
    public void Build_ReturnsEmpty_WhenThereAreNoBlocks() =>
        Assert.Equal("", BlockLocalLabels.Build(Body(), Central));
}