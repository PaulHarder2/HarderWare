using System.Globalization;

using MetarParser.Data.Entities;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

/// <summary>
/// WX-246 — the per-report day-name reference (`DayNameReference.Build`) injected into the
/// reconciler user message. Uses a fixed −6h offset zone so local-date math is deterministic and
/// DST-independent; asserts stable Spanish/English day names + invariant date anchors, and derives
/// the Albanian form the same way the code does (its exact string varies by ICU version).
/// </summary>
public class DayNameReferenceTests
{
    private static readonly TimeZoneInfo Tz =
        TimeZoneInfo.CreateCustomTimeZone("utc-6", TimeSpan.FromHours(-6), "UTC-6", "UTC-6");

    private static CultureInfo Culture(string iso) => iso switch
    {
        "es" => CultureInfo.GetCultureInfo("es-ES"),
        "sq" => CultureInfo.GetCultureInfo("sq-AL"),
        _ => CultureInfo.GetCultureInfo("en-US"),
    };

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

    [Fact]
    public void Build_MapsEachDistinctLocalForecastDate_ToItsCultureInfoDayName()
    {
        // Blocks are supplied OUT of chronological order (Wed first) so the OrderBy is exercised:
        // 07-08 18:00Z → 07-08 (Wed); 07-07 03:00Z → 07-06 21:00 local (Mon); 07-07 18:00Z → 07-07 (Tue);
        // 07-07 23:00Z → 07-07 17:00 (Tue — same local day, deduped).
        var body = Body(
            new DateTime(2026, 7, 8, 18, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 7, 3, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 7, 18, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 7, 23, 0, 0, DateTimeKind.Utc));

        var result = DayNameReference.Build(body, new[] { "en", "es" }, Culture, Tz);

        Assert.Contains("day_name_reference", result);
        Assert.Contains("inflected for grammatical agreement", result);

        // Invariant date anchors: three distinct local days, Mon/Tue/Wed.
        Assert.Contains("2026-07-06 (Mon)", result);
        Assert.Contains("2026-07-07 (Tue)", result);
        Assert.Contains("2026-07-08 (Wed)", result);

        // Emitted in CHRONOLOGICAL order despite the out-of-order input (dies if OrderBy is dropped).
        Assert.True(result.IndexOf("2026-07-06 (Mon)") < result.IndexOf("2026-07-07 (Tue)"));
        Assert.True(result.IndexOf("2026-07-07 (Tue)") < result.IndexOf("2026-07-08 (Wed)"));

        // Per-language mappings (Spanish/English names are ICU-stable).
        Assert.Contains("2026-07-07 (Tue) = Tuesday", result);
        Assert.Contains("2026-07-07 (Tue) = martes", result);
        Assert.Contains("2026-07-08 (Wed) = miércoles", result);

        // The two Tuesday blocks collapse to one entry (distinct local dates).
        Assert.Equal(1, result.Split(new[] { "= martes" }, StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Build_UsesTheTargetCultureDayName_ForEachLanguage()
    {
        var body = Body(new DateTime(2026, 7, 7, 18, 0, 0, DateTimeKind.Utc)); // Tue local
        var result = DayNameReference.Build(body, new[] { "sq" }, Culture, Tz);

        // Derive the Albanian form the same way the code does — its exact string is ICU-dependent.
        var expected = Culture("sq").DateTimeFormat.GetDayName(DayOfWeek.Tuesday);
        Assert.Contains($"2026-07-07 (Tue) = {expected}", result);
    }

    [Fact]
    public void Build_ReturnsEmpty_WhenNoBlocksOrNoLanguages()
    {
        Assert.Equal("", DayNameReference.Build(Body(), new[] { "es" }, Culture, Tz));
        Assert.Equal("", DayNameReference.Build(
            Body(new DateTime(2026, 7, 7, 18, 0, 0, DateTimeKind.Utc)), Array.Empty<string>(), Culture, Tz));
    }
}