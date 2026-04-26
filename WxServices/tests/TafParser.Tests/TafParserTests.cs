using Xunit;

namespace TafParser.Tests;

/// <summary>
/// Unit tests for <see cref="TafParser"/>.
/// </summary>
public class TafParserTests
{
    // ── header fields ─────────────────────────────────────────────────────────

    [Fact]
    public void Parse_SetsReportTypeAndStation()
    {
        var r = TafParser.Parse("TAF EGLL 221130Z 2212/2318 27015KT 9999 FEW030");
        Assert.Equal("TAF", r.ReportType);
        Assert.Equal("EGLL", r.Station);
    }

    [Fact]
    public void Parse_SetsIssuanceTime()
    {
        var r = TafParser.Parse("TAF EGLL 221130Z 2212/2318 27015KT 9999 FEW030");
        Assert.Equal(22, r.IssuanceDay);
        Assert.Equal(11, r.IssuanceHour);
        Assert.Equal(30, r.IssuanceMinute);
    }

    [Fact]
    public void Parse_SetsValidityPeriod()
    {
        var r = TafParser.Parse("TAF EGLL 221130Z 2212/2318 27015KT 9999 FEW030");
        Assert.Equal(22, r.ValidFromDay);
        Assert.Equal(12, r.ValidFromHour);
        Assert.Equal(23, r.ValidToDay);
        Assert.Equal(18, r.ValidToHour);
    }

    [Fact]
    public void Parse_StoresRaw()
    {
        const string raw = "TAF EGLL 221130Z 2212/2318 27015KT 9999 FEW030";
        var r = TafParser.Parse(raw);
        Assert.Equal(raw, r.Raw);
    }

    [Fact]
    public void Parse_Amendment_SetsReportType()
    {
        var r = TafParser.Parse("TAF AMD EGLL 221130Z 2212/2318 27015KT 9999 FEW030");
        Assert.Equal("TAF AMD", r.ReportType);
    }

    [Fact]
    public void Parse_Correction_SetsReportType()
    {
        var r = TafParser.Parse("TAF COR EGLL 221130Z 2212/2318 27015KT 9999 FEW030");
        Assert.Equal("TAF COR", r.ReportType);
    }

    // ── base period wind ──────────────────────────────────────────────────────

    [Fact]
    public void Parse_DecodesBaseWind()
    {
        var r = TafParser.Parse("TAF EGLL 221130Z 2212/2318 27015KT 9999 FEW030");
        Assert.NotNull(r.Wind);
        Assert.Equal(270, r.Wind!.Direction);
        Assert.Equal(15, r.Wind.Speed);
        Assert.Equal("KT", r.Wind.Unit);
    }

    [Fact]
    public void Parse_DecodesBaseWindWithGust()
    {
        var r = TafParser.Parse("TAF EGLL 221130Z 2212/2318 27020G35KT 9999 FEW030");
        Assert.NotNull(r.Wind);
        Assert.Equal(35, r.Wind!.Gust);
    }

    [Fact]
    public void Parse_VariableWind()
    {
        var r = TafParser.Parse("TAF EGLL 221130Z 2212/2318 VRB05KT 9999 SKC");
        Assert.NotNull(r.Wind);
        Assert.True(r.Wind!.IsVariable);
        Assert.Equal(5, r.Wind.Speed);
    }

    // ── base period visibility ────────────────────────────────────────────────

    [Fact]
    public void Parse_DecodesMetricVisibility()
    {
        var r = TafParser.Parse("TAF EGLL 221130Z 2212/2318 27015KT 9999 FEW030");
        Assert.NotNull(r.Visibility);
        Assert.Equal(9999, r.Visibility!.DistanceMeters);
    }

    [Fact]
    public void Parse_DecodesCavok()
    {
        var r = TafParser.Parse("TAF EGLL 221130Z 2212/2318 27015KT CAVOK");
        Assert.NotNull(r.Visibility);
        Assert.True(r.Visibility!.Cavok);
    }

    // ── base period sky ───────────────────────────────────────────────────────

    [Fact]
    public void Parse_DecodesBaseSkyConditions()
    {
        var r = TafParser.Parse("TAF EGLL 221130Z 2212/2318 27015KT 9999 FEW020 SCT060 BKN120");
        Assert.Equal(3, r.Sky.Count);
        Assert.Equal("FEW", r.Sky[0].Cover);
        Assert.Equal(2000, r.Sky[0].HeightFeet);
        Assert.Equal("SCT", r.Sky[1].Cover);
        Assert.Equal("BKN", r.Sky[2].Cover);
    }

    [Fact]
    public void Parse_SkyConditionWithCb()
    {
        var r = TafParser.Parse("TAF EGLL 221130Z 2212/2318 27015KT 9999 FEW020CB");
        Assert.Equal("CB", r.Sky[0].CloudType);
    }

    // ── weather phenomena ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_DecodesWeatherPhenomenon()
    {
        var r = TafParser.Parse("TAF EGLL 221130Z 2212/2318 27015KT 5000 TSRA BKN020CB");
        Assert.Single(r.Weather);
        Assert.Equal("TS", r.Weather[0].Descriptor);
        Assert.Contains("RA", r.Weather[0].Precipitation);
    }

    // ── change periods ────────────────────────────────────────────────────────

    [Fact]
    public void Parse_DecodesTempoChangePeriod()
    {
        var r = TafParser.Parse(
            "TAF EGLL 221130Z 2212/2318 27015KT 9999 FEW030 " +
            "TEMPO 2215/2220 27020G35KT BKN020");

        Assert.Single(r.ChangePeriods);
        var p = r.ChangePeriods[0];
        Assert.Equal("TEMPO", p.ChangeType);
        Assert.Equal(22, p.FromDay);
        Assert.Equal(15, p.FromHour);
        Assert.Equal(22, p.ToDay);
        Assert.Equal(20, p.ToHour);
        Assert.NotNull(p.Wind);
        Assert.Equal(20, p.Wind!.Speed);
        Assert.Equal(35, p.Wind.Gust);
    }

    [Fact]
    public void Parse_DecodesBecmgChangePeriod()
    {
        var r = TafParser.Parse(
            "TAF EGLL 221130Z 2212/2318 27015KT 9999 FEW030 " +
            "BECMG 2300/2302 24010KT");

        Assert.Single(r.ChangePeriods);
        var p = r.ChangePeriods[0];
        Assert.Equal("BECMG", p.ChangeType);
        Assert.Equal(240, p.Wind!.Direction);
        Assert.Equal(10, p.Wind.Speed);
    }

    [Fact]
    public void Parse_DecodesFmChangePeriod()
    {
        var r = TafParser.Parse(
            "TAF EGLL 221130Z 2212/2318 27015KT 9999 FEW030 " +
            "FM221800 24012KT 8000 SCT025");

        Assert.Single(r.ChangePeriods);
        var p = r.ChangePeriods[0];
        Assert.Equal("FM", p.ChangeType);
        Assert.Equal(22, p.FromDay);
        Assert.Equal(18, p.FromHour);
        Assert.NotNull(p.Wind);
        Assert.Equal(240, p.Wind!.Direction);
    }

    [Fact]
    public void Parse_DecodesProb30TempoPeriod()
    {
        var r = TafParser.Parse(
            "TAF EGLL 221130Z 2212/2318 27015KT 9999 FEW030 " +
            "PROB30 TEMPO 2218/2222 TSRA BKN010CB");

        Assert.Single(r.ChangePeriods);
        var p = r.ChangePeriods[0];
        Assert.Equal("PROB30 TEMPO", p.ChangeType);
        Assert.Single(p.Weather);
        Assert.Equal("TS", p.Weather[0].Descriptor);
    }

    [Fact]
    public void Parse_DecodesMultipleChangePeriods()
    {
        var r = TafParser.Parse(
            "TAF EGLL 221130Z 2212/2318 27015KT 9999 FEW030 " +
            "TEMPO 2215/2220 27025G40KT BKN020 " +
            "BECMG 2300/2302 24010KT");

        Assert.Equal(2, r.ChangePeriods.Count);
        Assert.Equal("TEMPO", r.ChangePeriods[0].ChangeType);
        Assert.Equal("BECMG", r.ChangePeriods[1].ChangeType);
    }

    // ── error handling ────────────────────────────────────────────────────────

    [Fact]
    public void Parse_MissingReportType_Throws()
    {
        Assert.Throws<TafParseException>(() =>
            TafParser.Parse("EGLL 221130Z 2212/2318 27015KT 9999 FEW030"));
    }

    [Fact]
    public void Parse_MissingStation_Throws()
    {
        Assert.Throws<TafParseException>(() =>
            TafParser.Parse("TAF 221130Z 2212/2318 27015KT 9999 FEW030"));
    }

    [Fact]
    public void Parse_MissingIssuanceTime_Throws()
    {
        Assert.Throws<TafParseException>(() =>
            TafParser.Parse("TAF EGLL 2212/2318 27015KT 9999 FEW030"));
    }

    [Fact]
    public void Parse_MissingValidityPeriod_Throws()
    {
        Assert.Throws<TafParseException>(() =>
            TafParser.Parse("TAF EGLL 221130Z 27015KT 9999 FEW030"));
    }

    [Fact]
    public void Parse_InvalidWindDirection_TreatedAsUnparsed()
    {
        var r = TafParser.Parse("TAF EGLL 221130Z 2212/2318 37015KT 9999 FEW030");
        Assert.Null(r.Wind);
        Assert.Contains("37015KT", r.UnparsedGroups);
    }
}