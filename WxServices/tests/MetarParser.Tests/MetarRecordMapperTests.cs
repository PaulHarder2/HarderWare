using MetarParser.Data;
using MetarParser.Data.Entities;

using Xunit;

namespace MetarParser.Tests;

public class MetarRecordMapperTests
{
    // ── InferObservationUtc ──────────────────────────────────────────────────

    [Fact]
    public void InferObservationUtc_DayEqualsToday_UsesCurrentMonthAndYear()
    {
        var reference = new DateTime(2026, 3, 15, 10, 0, 0, DateTimeKind.Utc);
        var result = MetarRecordMapper.InferObservationUtc(15, 12, 20, reference);
        Assert.Equal(new DateTime(2026, 3, 15, 12, 20, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void InferObservationUtc_DayBeforeToday_UsesCurrentMonthAndYear()
    {
        var reference = new DateTime(2026, 3, 15, 10, 0, 0, DateTimeKind.Utc);
        var result = MetarRecordMapper.InferObservationUtc(10, 12, 20, reference);
        Assert.Equal(new DateTime(2026, 3, 10, 12, 20, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void InferObservationUtc_DayAfterToday_RollsBackToPreviousMonth()
    {
        var reference = new DateTime(2026, 3, 15, 10, 0, 0, DateTimeKind.Utc);
        var result = MetarRecordMapper.InferObservationUtc(20, 12, 20, reference);
        Assert.Equal(new DateTime(2026, 2, 20, 12, 20, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void InferObservationUtc_DayAfterTodayInJanuary_RollsBackToDecemberPreviousYear()
    {
        var reference = new DateTime(2026, 1, 10, 10, 0, 0, DateTimeKind.Utc);
        var result = MetarRecordMapper.InferObservationUtc(20, 12, 20, reference);
        Assert.Equal(new DateTime(2025, 12, 20, 12, 20, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void InferObservationUtc_ResultAlwaysHasUtcKind()
    {
        var reference = new DateTime(2026, 3, 15, 10, 0, 0, DateTimeKind.Utc);
        var result = MetarRecordMapper.InferObservationUtc(15, 12, 20, reference);
        Assert.Equal(DateTimeKind.Utc, result.Kind);
    }

    // ── ToEntity field mapping ───────────────────────────────────────────────

    [Fact]
    public void ToEntity_MapsWindFields()
    {
        var report = MetarParser.Parse("METAR EGLL 221220Z 27015G25KT 9999 FEW030 10/05 Q1018");
        var entity = MetarRecordMapper.ToEntity(report);

        Assert.Equal(270, entity.WindDirection);
        Assert.Equal(15, entity.WindSpeed);
        Assert.Equal(25, entity.WindGust);
        Assert.Equal("KT", entity.WindUnit);
        Assert.False(entity.WindIsVariable);
    }

    [Fact]
    public void ToEntity_MapsVisibilityFields()
    {
        var report = MetarParser.Parse("METAR EGLL 221220Z 27015KT 9999 FEW030 10/05 Q1018");
        var entity = MetarRecordMapper.ToEntity(report);

        Assert.Equal(9999, entity.VisibilityM);
        Assert.False(entity.VisibilityCavok);
        Assert.False(entity.VisibilityLessThan);
    }

    [Fact]
    public void ToEntity_NaNDewPoint_StoredAsNull()
    {
        // Construct a report directly to inject a NaN dew point without
        // depending on parser handling of the '//' token.
        var report = new MetarReport
        {
            Raw = "",
            ReportType = "METAR",
            Station = "EGLL",
            Day = 22,
            Hour = 12,
            Minute = 20,
            Temperature = new Temperature { Air = 10, DewPoint = double.NaN },
        };
        var entity = MetarRecordMapper.ToEntity(report);

        Assert.Equal(10, entity.AirTemperatureCelsius);
        Assert.Null(entity.DewPointCelsius);
    }

    [Fact]
    public void ToEntity_PopulatesSkyConditionChildRows()
    {
        var report = MetarParser.Parse(
            "METAR EGLL 221220Z 27015KT 9999 FEW020 SCT040 BKN080 10/05 Q1018");
        var entity = MetarRecordMapper.ToEntity(report);

        Assert.Equal(3, entity.SkyConditions.Count);
        Assert.Equal("FEW", entity.SkyConditions[0].Cover);
        Assert.Equal("SCT", entity.SkyConditions[1].Cover);
        Assert.Equal("BKN", entity.SkyConditions[2].Cover);
    }

    [Fact]
    public void ToEntity_PopulatesRvrChildRows()
    {
        var report = MetarParser.Parse(
            "METAR EGLL 221220Z 27015KT 0400 R28L/0600N R28R/0700U FG OVC001 10/09 Q1018");
        var entity = MetarRecordMapper.ToEntity(report);

        Assert.Equal(2, entity.RunwayVisualRanges.Count);
        Assert.Equal("28L", entity.RunwayVisualRanges[0].Runway);
        Assert.Equal(600, entity.RunwayVisualRanges[0].MeanMeters);
        Assert.Equal("28R", entity.RunwayVisualRanges[1].Runway);
        Assert.Equal(700, entity.RunwayVisualRanges[1].MeanMeters);
    }

    [Fact]
    public void ToEntity_SetsReportTypeAndStation()
    {
        var report = MetarParser.Parse("METAR EGLL 221220Z 27015KT 9999 FEW030 10/05 Q1018");
        var entity = MetarRecordMapper.ToEntity(report);

        Assert.Equal("METAR", entity.ReportType);
        Assert.Equal("EGLL", entity.StationIcao);
    }

    [Fact]
    public void ToEntity_StoresRawReport()
    {
        const string raw = "METAR EGLL 221220Z 27015KT 9999 FEW030 10/05 Q1018";
        var entity = MetarRecordMapper.ToEntity(MetarParser.Parse(raw));
        Assert.Equal(raw, entity.RawReport);
    }
}