// Unit tests for the METAR/SPECI parser.
// Test inputs are drawn from WMO Manual 306 examples and real-world reports.

using MetarParser;

using Xunit;

namespace MetarParser.Tests;

public class ParserTests
{
    // ── header fields ────────────────────────────────────────────────────────

    [Fact]
    public void ParsesReportType_Metar()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 9999 FEW030 10/05 Q1018");
        Assert.Equal("METAR", r.ReportType);
    }

    [Fact]
    public void ParsesReportType_Speci()
    {
        var r = Parse("SPECI EGLL 221220Z 27015KT 9999 FEW030 10/05 Q1018");
        Assert.Equal("SPECI", r.ReportType);
    }

    [Fact]
    public void ParsesStation()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 9999 FEW030 10/05 Q1018");
        Assert.Equal("EGLL", r.Station);
    }

    [Fact]
    public void ParsesDateTime()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 9999 FEW030 10/05 Q1018");
        Assert.Equal(22, r.Day);
        Assert.Equal(12, r.Hour);
        Assert.Equal(20, r.Minute);
    }

    [Fact]
    public void RecognisesAutoModifier()
    {
        var r = Parse("METAR EGLL 221220Z AUTO 27015KT 9999 FEW030 10/05 Q1018");
        Assert.True(r.IsAuto);
        Assert.False(r.IsCorrection);
    }

    [Fact]
    public void RecognisesCorrectionModifier()
    {
        var r = Parse("METAR EGLL 221220Z COR 27015KT 9999 FEW030 10/05 Q1018");
        Assert.True(r.IsCorrection);
        Assert.False(r.IsAuto);
    }

    // ── wind ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ParsesWindDirection()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 9999 FEW030 10/05 Q1018");
        Assert.Equal(270, r.Wind!.Direction);
        Assert.False(r.Wind.IsVariable);
    }

    [Fact]
    public void ParsesWindSpeed()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 9999 FEW030 10/05 Q1018");
        Assert.Equal(15, r.Wind!.Speed);
        Assert.Equal("KT", r.Wind.Unit);
    }

    [Fact]
    public void ParsesWindGust()
    {
        var r = Parse("METAR EGLL 221220Z 27015G25KT 9999 FEW030 10/05 Q1018");
        Assert.Equal(25, r.Wind!.Gust);
    }

    [Fact]
    public void ParsesVariableWind()
    {
        var r = Parse("METAR EGLL 221220Z VRB03KT 9999 SKC 10/05 Q1018");
        Assert.True(r.Wind!.IsVariable);
        Assert.Null(r.Wind.Direction);
    }

    [Fact]
    public void ParsesVariableWindSector()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 250V310 9999 FEW030 10/05 Q1018");
        Assert.Equal(250, r.Wind!.VariableFrom);
        Assert.Equal(310, r.Wind.VariableTo);
    }

    [Fact]
    public void ParsesWindInMps()
    {
        var r = Parse("METAR UUDD 221220Z 27008MPS 9999 SKC 10/05 Q1018");
        Assert.Equal("MPS", r.Wind!.Unit);
        Assert.Equal(8, r.Wind.Speed);
    }

    [Fact]
    public void ParsesCalmWind()
    {
        var r = Parse("METAR EGLL 221220Z 00000KT 9999 SKC 10/05 Q1018");
        Assert.Equal(0, r.Wind!.Direction);
        Assert.Equal(0, r.Wind.Speed);
    }

    // ── visibility ───────────────────────────────────────────────────────────

    [Fact]
    public void ParsesVisibility()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 9999 FEW030 10/05 Q1018");
        Assert.Equal(9999, r.Visibility!.DistanceMeters);
        Assert.False(r.Visibility.Cavok);
    }

    [Fact]
    public void ParsesCavok()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT CAVOK 10/05 Q1018");
        Assert.True(r.Visibility!.Cavok);
    }

    [Fact]
    public void ParsesLowVisibility()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 0200 FG FEW001 10/09 Q1018");
        Assert.Equal(200, r.Visibility!.DistanceMeters);
    }

    [Fact]
    public void ParsesMinimumVisibilityWithDirection()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 1500 0800NE FEW010 10/09 Q1018");
        Assert.Equal(800, r.Visibility!.MinimumDistanceMeters);
        Assert.Equal("NE", r.Visibility.MinimumDirection);
    }

    // ── RVR ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ParsesSingleRvr()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 0400 R28L/0600N FG OVC001 10/09 Q1018");
        Assert.Single(r.Rvr);
        Assert.Equal("28L", r.Rvr[0].Runway);
        Assert.Equal(600, r.Rvr[0].MeanMeters);
        Assert.Equal('N', r.Rvr[0].Trend);
    }

    [Fact]
    public void ParsesVariableRvr()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 0400 R28L/0400V0800U FG OVC001 10/09 Q1018");
        Assert.Equal(400, r.Rvr[0].MinMeters);
        Assert.Equal(800, r.Rvr[0].MaxMeters);
        Assert.Null(r.Rvr[0].MeanMeters);
        Assert.Equal('U', r.Rvr[0].Trend);
    }

    [Fact]
    public void ParsesMultipleRvr()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 0400 R28L/0600N R28R/0700U FG OVC001 10/09 Q1018");
        Assert.Equal(2, r.Rvr.Count);
    }

    [Fact]
    public void ParsesBelowMinRvr()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 0050 R28L/M0050 FG OVC001 10/09 Q1018");
        Assert.True(r.Rvr[0].BelowMinimum);
    }

    // ── present weather ──────────────────────────────────────────────────────

    [Fact]
    public void ParsesFog()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 0200 FG FEW001 10/09 Q1018");
        Assert.Single(r.PresentWeather);
        Assert.Equal("FG", r.PresentWeather[0].Obscuration);
    }

    [Fact]
    public void ParsesHeavyRain()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 4000 +RA FEW010 10/09 Q1018");
        Assert.Equal("+", r.PresentWeather[0].Intensity);
        Assert.Contains("RA", r.PresentWeather[0].Precipitation);
    }

    [Fact]
    public void ParsesLightSnowShower()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 4000 -SHSN FEW020CB 10/01 Q1010");
        Assert.Equal("-", r.PresentWeather[0].Intensity);
        Assert.Equal("SH", r.PresentWeather[0].Descriptor);
        Assert.Contains("SN", r.PresentWeather[0].Precipitation);
    }

    [Fact]
    public void ParsesThunderstorm()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 4000 TSRA FEW020CB 10/09 Q1010");
        Assert.Equal("TS", r.PresentWeather[0].Descriptor);
        Assert.Contains("RA", r.PresentWeather[0].Precipitation);
    }

    [Fact]
    public void ParsesVicinityShower()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 9999 VCSH FEW030 10/05 Q1018");
        Assert.Equal("VC", r.PresentWeather[0].Intensity);
        Assert.Equal("SH", r.PresentWeather[0].Descriptor);
    }

    [Fact]
    public void ParsesMultipleWeatherGroups()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 0300 FG BR OVC001 10/09 Q1018");
        Assert.Equal(2, r.PresentWeather.Count);
    }

    // ── sky conditions ────────────────────────────────────────────────────────

    [Fact]
    public void ParsesFewClouds()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 9999 FEW030 10/05 Q1018");
        Assert.Single(r.Sky);
        Assert.Equal("FEW", r.Sky[0].Cover);
        Assert.Equal(3000, r.Sky[0].HeightFeet);
    }

    [Fact]
    public void ParsesCbCloud()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 4000 -TSRA FEW020CB BKN040 10/08 Q1010");
        Assert.Equal("CB", r.Sky[0].CloudType);
    }

    [Fact]
    public void ParsesTcuCloud()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 4000 -SHRA FEW020TCU BKN040 10/08 Q1010");
        Assert.Equal("TCU", r.Sky[0].CloudType);
    }

    [Fact]
    public void ParsesOvercast()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 0200 FG OVC001 10/09 Q1018");
        Assert.Equal("OVC", r.Sky[0].Cover);
        Assert.Equal(100, r.Sky[0].HeightFeet);
    }

    [Fact]
    public void ParsesSkyClear()
    {
        var r = Parse("METAR EGLL 221220Z 00000KT 9999 SKC 10/05 Q1018");
        Assert.Single(r.Sky);
        Assert.Equal("SKC", r.Sky[0].Cover);
    }

    [Fact]
    public void ParsesNsc()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT CAVOK 10/05 Q1018 NSC");
        // NSC present in unparsed (after altimeter outside of sky section)
        // or let's test a proper position:
        var r2 = Parse("METAR EGLL 221220Z 27015KT 9999 NSC 10/05 Q1018");
        Assert.Equal("NSC", r2.Sky[0].Cover);
    }

    [Fact]
    public void ParsesVerticalVisibility()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 0050 FG VV002 10/09 Q1018");
        Assert.True(r.Sky[0].IsVerticalVisibility);
        Assert.Equal(200, r.Sky[0].HeightFeet);
    }

    [Fact]
    public void ParsesMultipleSkyLayers()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 9999 FEW020 SCT040 BKN080 10/05 Q1018");
        Assert.Equal(3, r.Sky.Count);
    }

    // ── temperature / dew point ───────────────────────────────────────────────

    [Fact]
    public void ParsesTemperature()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 9999 FEW030 10/05 Q1018");
        Assert.Equal(10, r.Temperature!.Air);
        Assert.Equal(5, r.Temperature.DewPoint);
    }

    [Fact]
    public void ParsesNegativeTemperature()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 9999 FEW030 M05/M10 Q1018");
        Assert.Equal(-5, r.Temperature!.Air);
        Assert.Equal(-10, r.Temperature.DewPoint);
    }

    [Fact]
    public void ParsesMixedSign()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 9999 FEW030 02/M03 Q1018");
        Assert.Equal(2, r.Temperature!.Air);
        Assert.Equal(-3, r.Temperature.DewPoint);
    }

    // ── altimeter ─────────────────────────────────────────────────────────────

    [Fact]
    public void ParsesQnhHpa()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 9999 FEW030 10/05 Q1018");
        Assert.Equal(1018, r.Altimeter!.Value);
        Assert.Equal("hPa", r.Altimeter.Unit);
    }

    [Fact]
    public void ParsesQnhInhg()
    {
        var r = Parse("METAR KJFK 221220Z 27015KT 9999 FEW030 10/05 A2992");
        Assert.Equal(29.92, r.Altimeter!.Value, precision: 2);
        Assert.Equal("inHg", r.Altimeter.Unit);
    }

    // ── recent weather ────────────────────────────────────────────────────────

    [Fact]
    public void ParsesRecentWeather()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 9999 FEW030 10/05 Q1018 RERA");
        Assert.Single(r.RecentWeather);
        Assert.Contains("RA", r.RecentWeather[0].Precipitation);
    }

    [Fact]
    public void ParsesRecentThunderstorm()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 9999 FEW030 10/05 Q1018 RETS");
        Assert.Equal("TS", r.RecentWeather[0].Descriptor);
    }

    // ── NOSIG / trend ─────────────────────────────────────────────────────────

    [Fact]
    public void ParsesNosig()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 9999 FEW030 10/05 Q1018 NOSIG");
        Assert.Single(r.Trend);
        Assert.Equal("NOSIG", r.Trend[0].ChangeType);
    }

    [Fact]
    public void ParsesBecmgTrend()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 9999 FEW030 10/05 Q1018 BECMG FM1300 TL1400 BKN020");
        Assert.Single(r.Trend);
        var tr = r.Trend[0];
        Assert.Equal("BECMG", tr.ChangeType);
        Assert.Equal("1300", tr.From);
        Assert.Equal("1400", tr.Until);
        Assert.Single(tr.Sky);
        Assert.Equal("BKN", tr.Sky[0].Cover);
    }

    [Fact]
    public void ParsesTempoTrend()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 9999 FEW030 10/05 Q1018 TEMPO 4000 -RA BKN020");
        Assert.Equal("TEMPO", r.Trend[0].ChangeType);
        Assert.Equal(4000, r.Trend[0].Visibility!.DistanceMeters);
        Assert.Single(r.Trend[0].Weather);
    }

    // ── remarks ───────────────────────────────────────────────────────────────

    [Fact]
    public void ParsesRemarks()
    {
        var r = Parse("METAR KJFK 221220Z 27015KT 9999 FEW030 10/05 A2992 RMK AO2 SLP032");
        Assert.Equal("AO2 SLP032", r.Remarks);
    }

    // ── edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public void TrailingEqualsStripped()
    {
        var r = Parse("METAR EGLL 221220Z 27015KT 9999 FEW030 10/05 Q1018=");
        Assert.Equal("EGLL", r.Station);
    }

    [Fact]
    public void ExtraWhitespaceNormalised()
    {
        var r = Parse("METAR  EGLL  221220Z  27015KT  9999  FEW030  10/05  Q1018");
        Assert.Equal("EGLL", r.Station);
    }

    [Fact]
    public void ThrowsOnMissingType()
    {
        Assert.Throws<MetarParseException>(() => Parse("EGLL 221220Z 27015KT 9999 FEW030 10/05 Q1018"));
    }

    [Fact]
    public void ThrowsOnMissingStation()
    {
        Assert.Throws<MetarParseException>(() => Parse("METAR 221220Z 27015KT 9999 FEW030 10/05 Q1018"));
    }

    [Fact]
    public void ThrowsOnMissingDateTime()
    {
        Assert.Throws<MetarParseException>(() => Parse("METAR EGLL 27015KT 9999 FEW030 10/05 Q1018"));
    }

    // ── full real-world examples ──────────────────────────────────────────────

    [Fact]
    public void ParsesComplexMetar_EGLL()
    {
        // Heathrow — multiple layers, trend
        var r = Parse(
            "METAR EGLL 221220Z 27015G25KT 250V310 6000 -RA FEW012 SCT025 BKN080 " +
            "08/06 Q1012 TEMPO 3000 RA BKN010");
        Assert.Equal("EGLL", r.Station);
        Assert.Equal(15, r.Wind!.Speed);
        Assert.Equal(25, r.Wind.Gust);
        Assert.Equal(250, r.Wind.VariableFrom);
        Assert.Equal(6000, r.Visibility!.DistanceMeters);
        Assert.Equal(3, r.Sky.Count);
        Assert.Equal(8, r.Temperature!.Air);
        Assert.Equal(1012, r.Altimeter!.Value);
        Assert.Equal("TEMPO", r.Trend[0].ChangeType);
    }

    [Fact]
    public void ParsesComplexMetar_UUEE()
    {
        // Sheremetyevo in MPS
        var r = Parse(
            "METAR UUEE 221200Z 04005MPS 1200 0600N R24L/0400U FG VV003 M02/M04 Q1021 NOSIG");
        Assert.Equal("MPS", r.Wind!.Unit);
        Assert.Equal(1200, r.Visibility!.DistanceMeters);
        Assert.Equal(600, r.Visibility.MinimumDistanceMeters);
        Assert.Equal("N", r.Visibility.MinimumDirection);
        Assert.Single(r.Rvr);
        Assert.Equal("FG", r.PresentWeather[0].Obscuration);
        Assert.True(r.Sky[0].IsVerticalVisibility);
        Assert.Equal(-2, r.Temperature!.Air);
        Assert.Equal(1021, r.Altimeter!.Value);
        Assert.Equal("NOSIG", r.Trend[0].ChangeType);
    }

    [Fact]
    public void ParsesComplexMetar_KJFK()
    {
        // JFK-style report with inHg (A-prefix) altimeter and remarks.
        // Visibility converted to WMO meters format (9999 = 10 km or more).
        var r = Parse(
            "METAR KJFK 221151Z 32012KT 9999 FEW050 BKN250 22/11 A2998 RMK AO2 SLP153");
        Assert.Equal("inHg", r.Altimeter!.Unit);
        Assert.Equal(29.98, r.Altimeter.Value, precision: 2);
        Assert.Equal("AO2 SLP153", r.Remarks);
    }

    // ── malformed / out-of-range input ───────────────────────────────────────

    [Fact]
    public void WindDirection370_TreatedAsUnparsed()
    {
        // Direction 370 exceeds the maximum of 360 — should be rejected and
        // land in UnparsedGroups; the rest of the report should parse normally.
        var r = Parse("METAR EGLL 221220Z 37015KT 9999 FEW030 10/05 Q1018");
        Assert.Null(r.Wind);
        Assert.Contains("37015KT", r.UnparsedGroups);
        Assert.Equal(9999, r.Visibility!.DistanceMeters);   // rest parsed correctly
        Assert.Single(r.Sky);
    }

    [Fact]
    public void WindDirection360_IsValid()
    {
        // 360° is a valid WMO direction meaning North (same as 000° but with non-zero speed).
        var r = Parse("METAR EGLL 221220Z 36015KT 9999 FEW030 10/05 Q1018");
        Assert.Equal(360, r.Wind!.Direction);
        Assert.Equal(15, r.Wind.Speed);
    }

    [Fact]
    public void WindDirection000_WithNonZeroSpeed_IsValid()
    {
        // Direction 000 with speed > 0 means wind from North — structurally valid.
        var r = Parse("METAR EGLL 221220Z 00015KT 9999 FEW030 10/05 Q1018");
        Assert.Equal(0, r.Wind!.Direction);
        Assert.Equal(15, r.Wind.Speed);
        Assert.False(r.Wind.IsVariable);
    }

    [Fact]
    public void ThrowsOnDayZero()
    {
        Assert.Throws<MetarParseException>(() =>
            Parse("METAR EGLL 001220Z 27015KT 9999 FEW030 10/05 Q1018"));
    }

    [Fact]
    public void ThrowsOnDay32()
    {
        Assert.Throws<MetarParseException>(() =>
            Parse("METAR EGLL 321220Z 27015KT 9999 FEW030 10/05 Q1018"));
    }

    [Fact]
    public void ThrowsOnHour24()
    {
        Assert.Throws<MetarParseException>(() =>
            Parse("METAR EGLL 222412Z 27015KT 9999 FEW030 10/05 Q1018"));
    }

    [Fact]
    public void ThrowsOnMinute60()
    {
        Assert.Throws<MetarParseException>(() =>
            Parse("METAR EGLL 221260Z 27015KT 9999 FEW030 10/05 Q1018"));
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static MetarReport Parse(string s) => MetarParser.Parse(s);
}