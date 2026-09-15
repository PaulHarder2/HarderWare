using MetarParser.Data.Entities;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

// WX-506: the change-band call's input. The band is written from these facts alone, so they must state every
// computed change's prior -> now values correctly and must not present an unchanged window as a change.
public class ChangeSummaryFactsTests
{
    private static readonly TimeZoneInfo Cdt =
        TimeZoneInfo.CreateCustomTimeZone("Test-CDT", TimeSpan.FromHours(-5), "Test CDT", "Test CDT");

    // Production snapshots behind send 9341 (paul_en, Spring TX, 2026-09-15 16:29Z): 22206 is the forecast
    // last SENT (send 9336, 12:04Z), 22226 the one reconciled for 9341. Tuesday afternoon (17Z) is possible
    // thunderstorm in BOTH; Wednesday afternoon (2026-09-16 17Z) goes none -> likely thunderstorm.
    // 9341's model-written band said the opposite of both.
    private const string Send9341PriorJson = """
        {"schemaVersion":5,"blocks":[{"startUtc":"2026-09-15T05:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":27.8,"max":28.8},"windKt":{"min":4,"max":6},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-15T11:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":27.4,"max":33.3},"windKt":{"min":2,"max":5},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-15T17:00:00Z","skyState":"partly_cloudy","obscuration":"none","temperatureCelsius":{"min":34.7,"max":37.3},"windKt":{"min":6,"max":10},"precipExpectation":"possible","precipPhenomenon":"thunderstorm","severeFlag":false},{"startUtc":"2026-09-15T23:00:00Z","skyState":"partly_cloudy","obscuration":"none","temperatureCelsius":{"min":29.9,"max":34.9},"windKt":{"min":7,"max":10},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-16T05:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":27.9,"max":29.5},"windKt":{"min":1,"max":6},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-16T11:00:00Z","skyState":"mostly_cloudy","obscuration":"none","temperatureCelsius":{"min":27.5,"max":32},"windKt":{"min":0,"max":6},"precipExpectation":"likely","precipPhenomenon":"thunderstorm","severeFlag":false},{"startUtc":"2026-09-16T17:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":33.1,"max":35.7},"windKt":{"min":4,"max":4},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-16T23:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":29.5,"max":33.7},"windKt":{"min":5,"max":8},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-17T05:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":27.3,"max":29},"windKt":{"min":4,"max":5},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-17T11:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":26.7,"max":32},"windKt":{"min":5,"max":8},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-17T17:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":32.8,"max":33.7},"windKt":{"min":8,"max":10},"precipExpectation":"possible","precipPhenomenon":"thunderstorm","severeFlag":false},{"startUtc":"2026-09-17T23:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":28.9,"max":32.2},"windKt":{"min":5,"max":8},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-18T05:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":27,"max":28.5},"windKt":{"min":2,"max":4},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-18T11:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":26.4,"max":31.9},"windKt":{"min":2,"max":6},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-18T17:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":33.2,"max":35.4},"windKt":{"min":7,"max":9},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-18T23:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":28.1,"max":33.5},"windKt":{"min":5,"max":9},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-19T05:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":26,"max":27.7},"windKt":{"min":2,"max":4},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-19T11:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":25.5,"max":31.6},"windKt":{"min":1,"max":5},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-19T17:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":33.2,"max":35.8},"windKt":{"min":5,"max":9},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-19T23:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":28.4,"max":33.8},"windKt":{"min":6,"max":10},"precipExpectation":"none","severeFlag":false}]}
        """;

    private const string Send9341FinalJson = """
        {"schemaVersion":5,"blocks":[{"startUtc":"2026-09-15T11:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":27.4,"max":33.3},"windKt":{"min":2,"max":5},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-15T17:00:00Z","skyState":"partly_cloudy","obscuration":"none","temperatureCelsius":{"min":34.9,"max":38},"windKt":{"min":5,"max":11},"precipExpectation":"possible","precipPhenomenon":"thunderstorm","severeFlag":false},{"startUtc":"2026-09-15T23:00:00Z","skyState":"partly_cloudy","obscuration":"none","temperatureCelsius":{"min":30.1,"max":34.4},"windKt":{"min":7,"max":10},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-16T05:00:00Z","skyState":"partly_cloudy","obscuration":"none","temperatureCelsius":{"min":28,"max":29.5},"windKt":{"min":1,"max":6},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-16T11:00:00Z","skyState":"mostly_cloudy","obscuration":"none","temperatureCelsius":{"min":27.3,"max":31.9},"windKt":{"min":0,"max":6},"precipExpectation":"likely","precipPhenomenon":"thunderstorm","severeFlag":false},{"startUtc":"2026-09-16T17:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":33.3,"max":35.7},"windKt":{"min":6,"max":7},"precipExpectation":"likely","precipPhenomenon":"thunderstorm","severeFlag":false},{"startUtc":"2026-09-16T23:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":29.5,"max":33.6},"windKt":{"min":5,"max":7},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-17T05:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":27.3,"max":28.9},"windKt":{"min":4,"max":5},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-17T11:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":26.6,"max":31.3},"windKt":{"min":5,"max":7},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-17T17:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":32.9,"max":34},"windKt":{"min":8,"max":10},"precipExpectation":"possible","precipPhenomenon":"thunderstorm","severeFlag":false},{"startUtc":"2026-09-17T23:00:00Z","skyState":"overcast","obscuration":"none","temperatureCelsius":{"min":28.8,"max":32.7},"windKt":{"min":4,"max":8},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-18T05:00:00Z","skyState":"partly_cloudy","obscuration":"none","temperatureCelsius":{"min":26.9,"max":28.5},"windKt":{"min":2,"max":4},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-18T11:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":26.3,"max":31.8},"windKt":{"min":3,"max":6},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-18T17:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":33.1,"max":35.3},"windKt":{"min":7,"max":9},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-18T23:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":28.1,"max":33.4},"windKt":{"min":5,"max":9},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-19T05:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":26,"max":27.7},"windKt":{"min":1,"max":5},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-19T11:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":25.4,"max":31.5},"windKt":{"min":1,"max":5},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-19T17:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":33.1,"max":35.7},"windKt":{"min":6,"max":9},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-19T23:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":28.3,"max":33.5},"windKt":{"min":6,"max":9},"precipExpectation":"none","severeFlag":false},{"startUtc":"2026-09-20T05:00:00Z","skyState":"clear","obscuration":"none","temperatureCelsius":{"min":26.4,"max":27.9},"windKt":{"min":2,"max":5},"precipExpectation":"none","severeFlag":false}]}
        """;

    private static readonly DateTime Send9341NowUtc = new(2026, 9, 15, 16, 29, 36, DateTimeKind.Utc);

    // Before every block in the synthetic fixtures below, so nothing is elapsed unless a test says so.
    private static readonly DateTime EarlyNowUtc = new(2026, 6, 9, 4, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Send9341_Facts_NameWednesdayAfternoonRain_AndNotTuesdayAfternoon()
    {
        var prior = ForecastSnapshotBody.Deserialize(Send9341PriorJson);
        var final = ForecastSnapshotBody.Deserialize(Send9341FinalJson);
        var changes = DeterministicChangeDetector.Detect(prior, final, new SignificanceGateConfig(), Send9341NowUtc, Cdt);

        var facts = ChangeSummaryFacts.Build(changes, prior, final, Cdt, Send9341NowUtc);

        Assert.Contains("Wed 2026-09-16 afternoon (12:00-18:00): precipitation was none, now possible rain", facts);
        // Tuesday afternoon's precipitation did not change, so no fact may say it moved.
        Assert.DoesNotContain("Tue 2026-09-15 afternoon (12:00-18:00): precipitation", facts);
    }

    [Fact]
    public void Facts_StatePriorAndNow_WithLabelsAndUtcKeys()
    {
        var prior = Body(Block("2026-06-09T11:00:00Z", "none", null), Block("2026-06-09T17:00:00Z", "none", null));
        var final = Body(Block("2026-06-09T11:00:00Z", "possible", "rain"), Block("2026-06-09T17:00:00Z", "certain", "rain"));
        var changes = new[]
        {
            Change(ChangePhenomenon.Rain, ChangeDirection.Appearing, "2026-06-09T11:00:00Z", "2026-06-09T23:00:00Z"),
        };

        var facts = ChangeSummaryFacts.Build(changes, prior, final, Cdt, EarlyNowUtc);

        Assert.Contains("1. rain appearing [importance: plans-affecting — for your judgment, never write it]", facts);
        Assert.Contains("window: Tue 2026-06-09 morning (06:00-12:00) [first block 2026-06-09T11:00:00Z] through Tue 2026-06-09 afternoon (12:00-18:00) [last block 2026-06-09T17:00:00Z]", facts);
        Assert.Contains("Tue 2026-06-09 morning (06:00-12:00): precipitation was none, now possible rain", facts);
        Assert.Contains("Tue 2026-06-09 afternoon (12:00-18:00): precipitation was none, now certain rain", facts);
    }

    [Fact]
    public void Facts_FoldLikelyToPossible_AndMarkSevere()
    {
        // The recipient axis the detector compares on: likely reads as possible; a severe block says so.
        var prior = Body(Block("2026-06-09T11:00:00Z", "likely", "rain"));
        var final = Body(Block("2026-06-09T11:00:00Z", "possible", "thunderstorm", severe: true));
        var changes = new[] { Change(ChangePhenomenon.Thunderstorm, ChangeDirection.Appearing, "2026-06-09T11:00:00Z", "2026-06-09T17:00:00Z") };

        var facts = ChangeSummaryFacts.Build(changes, prior, final, Cdt, EarlyNowUtc);

        Assert.Contains("precipitation was possible rain, now severe thunderstorm", facts);
    }

    [Fact]
    public void Facts_FirstSend_SaysNotInPriorForecast()
    {
        var final = Body(Block("2026-06-09T11:00:00Z", "possible", "rain"));
        var changes = new[] { Change(ChangePhenomenon.Rain, ChangeDirection.Appearing, "2026-06-09T11:00:00Z", "2026-06-09T17:00:00Z") };

        var facts = ChangeSummaryFacts.Build(changes, prior: null, final, Cdt, EarlyNowUtc);

        Assert.Contains("precipitation was (not in prior forecast), now possible rain", facts);
    }

    [Fact]
    public void Facts_Temperature_StateTheDayAsPublishedAndNow()
    {
        // At 12:00Z the prior's 05Z block (00-06 local) has elapsed and holds the day's low, 18 °C. The new forecast
        // no longer carries it, but the past keeps its published value, so the low reads 18 -> 18, not 18 -> 22.
        var nowUtc = new DateTime(2026, 6, 9, 12, 0, 0, DateTimeKind.Utc);
        var prior = Body(Block("2026-06-09T05:00:00Z", "none", null, min: 18, max: 22),
                         Block("2026-06-09T11:00:00Z", "none", null, min: 22, max: 30),
                         Block("2026-06-09T17:00:00Z", "none", null, min: 30, max: 36.5));
        var final = Body(Block("2026-06-09T11:00:00Z", "none", null, min: 22, max: 30),
                         Block("2026-06-09T17:00:00Z", "none", null, min: 31, max: 38));
        var changes = new[] { Change(ChangePhenomenon.Temperature, ChangeDirection.Appearing, "2026-06-09T17:00:00Z", "2026-06-09T23:00:00Z", ChangeTier.Safety) };

        var facts = ChangeSummaryFacts.Build(changes, prior, final, Cdt, nowUtc);

        Assert.Contains("1. temperature appearing [importance: safety-critical — for your judgment, never write it]", facts);
        Assert.Contains("Tue 2026-06-09: °C the day's high was 36.5, now 38; the day's low was 18, now 18", facts);
    }

    [Fact]
    public void Facts_Temperature_LateReport_PastHoursKeepTheirPublishedValues()
    {
        // A report at 19:30 local: the morning and afternoon blocks have elapsed and the new forecast carries only
        // the evening. The day's figures still span the whole day as sent, so a warmer evening that stays below
        // the afternoon's 38 leaves the high at 38 -> 38.
        var nowUtc = new DateTime(2026, 6, 10, 0, 30, 0, DateTimeKind.Utc);
        var prior = Body(Block("2026-06-09T11:00:00Z", "none", null, min: 22, max: 30),
                         Block("2026-06-09T17:00:00Z", "none", null, min: 30, max: 38),
                         Block("2026-06-09T23:00:00Z", "none", null, min: 29, max: 33));
        var final = Body(Block("2026-06-09T23:00:00Z", "none", null, min: 29, max: 36));
        var changes = new[] { Change(ChangePhenomenon.Temperature, ChangeDirection.Strengthening, "2026-06-09T23:00:00Z", "2026-06-10T05:00:00Z") };

        var facts = ChangeSummaryFacts.Build(changes, prior, final, Cdt, nowUtc);

        Assert.Contains("Tue 2026-06-09: °C the day's high was 38, now 38; the day's low was 22, now 22", facts);
    }

    [Fact]
    public void Facts_Wind_StatesTheDayPeakAsPublishedAndNow()
    {
        var prior = Body(Block("2026-06-09T11:00:00Z", "none", null), Block("2026-06-09T17:00:00Z", "none", null));
        var final = Body(Block("2026-06-09T11:00:00Z", "none", null), BlockWind("2026-06-09T17:00:00Z", 20, 30));
        var changes = new[] { Change(ChangePhenomenon.Wind, ChangeDirection.Strengthening, "2026-06-09T17:00:00Z", "2026-06-09T23:00:00Z") };

        var facts = ChangeSummaryFacts.Build(changes, prior, final, Cdt, EarlyNowUtc);

        Assert.Contains("window: Tue 2026-06-09 afternoon (12:00-18:00)", facts);
        Assert.Contains("Tue 2026-06-09: the day's peak sustained wind kt was 12, now 30", facts);
        Assert.DoesNotContain("20..30", facts);
    }

    [Fact]
    public void Facts_Wind_LateReport_AnElapsedPeakStays()
    {
        // The afternoon's 30 kt has passed; a new evening of 20 kt leaves the day's peak at 30 -> 30.
        var nowUtc = new DateTime(2026, 6, 10, 0, 30, 0, DateTimeKind.Utc);
        var prior = Body(BlockWind("2026-06-09T17:00:00Z", 20, 30), BlockWind("2026-06-09T23:00:00Z", 5, 10));
        var final = Body(BlockWind("2026-06-09T23:00:00Z", 15, 20));
        var changes = new[] { Change(ChangePhenomenon.Wind, ChangeDirection.Strengthening, "2026-06-09T23:00:00Z", "2026-06-10T05:00:00Z") };

        var facts = ChangeSummaryFacts.Build(changes, prior, final, Cdt, nowUtc);

        Assert.Contains("Tue 2026-06-09: the day's peak sustained wind kt was 30, now 30", facts);
    }

    [Fact]
    public void Facts_WindShift_StatesNoValue()
    {
        // The block has no direction field, so a shift has no before/now value to show; the facts say so rather
        // than print identical speeds the band could read as "nothing changed" or fill with an invented direction.
        var body = Body(Block("2026-06-09T17:00:00Z", "none", null));
        var changes = new[] { Change(ChangePhenomenon.WindShift, ChangeDirection.Shifting, "2026-06-09T17:00:00Z", "2026-06-09T23:00:00Z") };

        var facts = ChangeSummaryFacts.Build(changes, body, body, Cdt, EarlyNowUtc);

        Assert.Contains("wind direction shifts; the forecast data carries no wind direction, so state none", facts);
        Assert.DoesNotContain("sustained wind", facts);
    }

    [Fact]
    public void Facts_NoChanges_IsEmpty() =>
        Assert.Equal("", ChangeSummaryFacts.Build([], null, Body(Block("2026-06-09T11:00:00Z", "none", null)), Cdt, EarlyNowUtc));

    private static ForecastSnapshotBody Body(params string[] blocks) =>
        ForecastSnapshotBody.Deserialize("{\"schemaVersion\":5,\"blocks\":[" + string.Join(",", blocks) + "]}");

    private static string Block(string startUtc, string expectation, string? phenomenon, bool severe = false, double min = 22, double max = 30) =>
        $"{{\"startUtc\":\"{startUtc}\",\"skyState\":\"partly_cloudy\",\"obscuration\":\"none\","
        + $"\"temperatureCelsius\":{{\"min\":{min.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"max\":{max.ToString(System.Globalization.CultureInfo.InvariantCulture)}}},"
        + "\"windKt\":{\"min\":5,\"max\":12},"
        + $"\"precipExpectation\":\"{expectation}\""
        + (phenomenon is null ? "" : $",\"precipPhenomenon\":\"{phenomenon}\"")
        + $",\"severeFlag\":{(severe ? "true" : "false")}}}";

    private static string BlockWind(string startUtc, int min, int max) =>
        Block(startUtc, "none", null).Replace("\"windKt\":{\"min\":5,\"max\":12}", $"\"windKt\":{{\"min\":{min},\"max\":{max}}}");

    private static ReportChange Change(ChangePhenomenon p, ChangeDirection d, string startUtc, string endUtc, ChangeTier tier = ChangeTier.Plans) => new()
    {
        Tier = tier,
        Phenomenon = p,
        Direction = d,
        Window = new ChangeWindow(
            DateTime.Parse(startUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            DateTime.Parse(endUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal)),
        SummaryToken = "ch1",
    };
}