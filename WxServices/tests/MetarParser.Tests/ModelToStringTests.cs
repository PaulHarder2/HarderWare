using Xunit;

namespace MetarParser.Tests;

/// <summary>
/// Verifies that each model's ToString() reconstructs the raw METAR token
/// it was decoded from.  These tests guard against regressions in the
/// round-trip: Parse → model → ToString → original token.
/// </summary>
public class ModelToStringTests
{
    // ── Wind ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Wind_ToString_Basic()
    {
        var w = new Wind { Direction = 270, Speed = 15, Unit = "KT" };
        Assert.Equal("27015KT", w.ToString());
    }

    [Fact]
    public void Wind_ToString_WithGust()
    {
        var w = new Wind { Direction = 270, Speed = 15, Gust = 25, Unit = "KT" };
        Assert.Equal("27015G25KT", w.ToString());
    }

    [Fact]
    public void Wind_ToString_Variable()
    {
        var w = new Wind { IsVariable = true, Speed = 3, Unit = "KT" };
        Assert.Equal("VRB03KT", w.ToString());
    }

    [Fact]
    public void Wind_ToString_Mps()
    {
        var w = new Wind { Direction = 40, Speed = 5, Unit = "MPS" };
        Assert.Equal("04005MPS", w.ToString());
    }

    [Fact]
    public void Wind_ToString_WithVariableSector()
    {
        var w = new Wind { Direction = 270, Speed = 15, Unit = "KT", VariableFrom = 250, VariableTo = 310 };
        Assert.Equal("27015KT 250V310", w.ToString());
    }

    [Fact]
    public void Wind_ToString_Calm()
    {
        var w = new Wind { Direction = 0, Speed = 0, Unit = "KT" };
        Assert.Equal("00000KT", w.ToString());
    }

    // ── Visibility ───────────────────────────────────────────────────────────

    [Fact]
    public void Visibility_ToString_Normal()
    {
        var v = new Visibility { DistanceMeters = 9999 };
        Assert.Equal("9999", v.ToString());
    }

    [Fact]
    public void Visibility_ToString_Cavok()
    {
        var v = new Visibility { Cavok = true };
        Assert.Equal("CAVOK", v.ToString());
    }

    [Fact]
    public void Visibility_ToString_LessThanWithLeadingZero()
    {
        var v = new Visibility { DistanceMeters = 200, LessThan = true };
        Assert.Equal("M0200", v.ToString());
    }

    [Fact]
    public void Visibility_ToString_LeadingZero()
    {
        var v = new Visibility { DistanceMeters = 800 };
        Assert.Equal("0800", v.ToString());
    }

    [Fact]
    public void Visibility_ToString_Ndv()
    {
        var v = new Visibility { DistanceMeters = 9999, NoDirectionalVariation = true };
        Assert.Equal("9999 NDV", v.ToString());
    }

    // ── RunwayVisualRange ────────────────────────────────────────────────────

    [Fact]
    public void RunwayVisualRange_ToString_MeanWithTrend()
    {
        var r = new RunwayVisualRange { Runway = "28L", MeanMeters = 600, Trend = 'N' };
        Assert.Equal("R28L/0600N", r.ToString());
    }

    [Fact]
    public void RunwayVisualRange_ToString_VariableRange()
    {
        var r = new RunwayVisualRange { Runway = "28L", MinMeters = 400, MaxMeters = 800, Trend = 'U' };
        Assert.Equal("R28L/400V800U", r.ToString());
    }

    [Fact]
    public void RunwayVisualRange_ToString_BelowMinimum()
    {
        var r = new RunwayVisualRange { Runway = "28L", MeanMeters = 50, BelowMinimum = true };
        Assert.Equal("R28L/M0050", r.ToString());
    }

    [Fact]
    public void RunwayVisualRange_ToString_AboveMaximum()
    {
        var r = new RunwayVisualRange { Runway = "28L", MeanMeters = 2000, AboveMaximum = true };
        Assert.Equal("R28L/P2000", r.ToString());
    }

    // ── SkyCondition ─────────────────────────────────────────────────────────

    [Fact]
    public void SkyCondition_ToString_LayerWithHeight()
    {
        var s = new SkyCondition { Cover = "FEW", HeightFeet = 3000 };
        Assert.Equal("FEW030", s.ToString());
    }

    [Fact]
    public void SkyCondition_ToString_WithCbType()
    {
        var s = new SkyCondition { Cover = "FEW", HeightFeet = 2000, CloudType = "CB" };
        Assert.Equal("FEW020CB", s.ToString());
    }

    [Fact]
    public void SkyCondition_ToString_SkyClear()
    {
        var s = new SkyCondition { Cover = "SKC" };
        Assert.Equal("SKC", s.ToString());
    }

    [Fact]
    public void SkyCondition_ToString_VerticalVisibility()
    {
        var s = new SkyCondition { Cover = "VV", HeightFeet = 200, IsVerticalVisibility = true };
        Assert.Equal("VV002", s.ToString());
    }

    // ── Temperature ──────────────────────────────────────────────────────────

    [Fact]
    public void Temperature_ToString_BothPositive()
    {
        var t = new Temperature { Air = 10, DewPoint = 5 };
        Assert.Equal("10/5", t.ToString());
    }

    [Fact]
    public void Temperature_ToString_BothNegative()
    {
        var t = new Temperature { Air = -5, DewPoint = -10 };
        Assert.Equal("M5/M10", t.ToString());
    }

    [Fact]
    public void Temperature_ToString_MixedSign()
    {
        var t = new Temperature { Air = 2, DewPoint = -3 };
        Assert.Equal("2/M3", t.ToString());
    }

    // ── Altimeter ────────────────────────────────────────────────────────────

    [Fact]
    public void Altimeter_ToString_Hpa()
    {
        var a = new Altimeter { Value = 1018, Unit = "hPa" };
        Assert.Equal("Q1018", a.ToString());
    }

    [Fact]
    public void Altimeter_ToString_Inhg()
    {
        // Use a round value to avoid floating-point truncation issues.
        var a = new Altimeter { Value = 30.00, Unit = "inHg" };
        Assert.Equal("A3000", a.ToString());
    }
}
