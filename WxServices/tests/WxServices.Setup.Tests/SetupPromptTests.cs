using System.Collections.Generic;

using WxServices.Setup;

using Xunit;

namespace WxServices.Setup.Tests;

/// <summary>
/// WX-314 AC-2, test-first: the pure input validators, and the re-prompt-until-valid loop driven
/// through injected IO (so the loop's behaviour is provable without a human at a keyboard).
/// </summary>
public class SetupPromptTests
{
    // ---- validators -------------------------------------------------------

    [Theory]
    [InlineData("KDFW", "KDFW")]
    [InlineData(" kdfw ", "KDFW")]   // trimmed + upper-cased
    public void TryParseIcao_Accepts(string raw, string expected)
    {
        Assert.True(SetupValidators.TryParseIcao(raw, out var icao, out _));
        Assert.Equal(expected, icao);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("KDF")]      // too short
    [InlineData("KDFWX")]    // too long
    [InlineData("KD3W")]     // digit
    public void TryParseIcao_Rejects(string raw)
    {
        Assert.False(SetupValidators.TryParseIcao(raw, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("32.9", 32.9)]
    [InlineData("-90", -90)]
    [InlineData("90", 90)]
    public void TryParseLatitude_Accepts(string raw, double expected)
    {
        Assert.True(SetupValidators.TryParseLatitude(raw, out var lat, out _));
        Assert.Equal(expected, lat);
    }

    [Theory]
    [InlineData("90.1")]
    [InlineData("-90.1")]
    [InlineData("north")]
    [InlineData("")]
    public void TryParseLatitude_Rejects(string raw) =>
        Assert.False(SetupValidators.TryParseLatitude(raw, out _, out _));

    [Theory]
    [InlineData("-97.0", -97.0)]
    [InlineData("180", 180)]
    [InlineData("-180", -180)]
    public void TryParseLongitude_Accepts(string raw, double expected)
    {
        Assert.True(SetupValidators.TryParseLongitude(raw, out var lon, out _));
        Assert.Equal(expected, lon);
    }

    [Theory]
    [InlineData("180.1")]
    [InlineData("-180.1")]
    [InlineData("west")]
    public void TryParseLongitude_Rejects(string raw) =>
        Assert.False(SetupValidators.TryParseLongitude(raw, out _, out _));

    [Theory]
    [InlineData("2.5", 2.5)]
    [InlineData("0.25", 0.25)]
    public void TryParsePositiveDegrees_Accepts(string raw, double expected)
    {
        Assert.True(SetupValidators.TryParsePositiveDegrees(raw, out var deg, out _));
        Assert.Equal(expected, deg);
    }

    [Theory]
    [InlineData("0")]        // must be > 0
    [InlineData("-1")]
    [InlineData("wide")]
    public void TryParsePositiveDegrees_Rejects(string raw) =>
        Assert.False(SetupValidators.TryParsePositiveDegrees(raw, out _, out _));

    /// <summary>MapExtent is handed verbatim to the plot script's --extent argument, and empty
    /// is legal there (the WxVis workers omit the argument entirely) — so it is trimmed, not shaped.</summary>
    [Theory]
    [InlineData("conus", "conus")]
    [InlineData("  conus  ", "conus")]
    [InlineData("", "")]
    public void TryParseMapExtent_AcceptsAnythingIncludingEmpty(string raw, string expected)
    {
        Assert.True(SetupValidators.TryParseMapExtent(raw, out var extent, out _));
        Assert.Equal(expected, extent);
    }

    [Theory]
    [InlineData("hunter2", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void TryParsePassword_RejectsOnlyBlank(string raw, bool expected) =>
        Assert.Equal(expected, SetupValidators.TryParsePassword(raw, out _, out _));

    [Fact]
    public void ValidateRegion_AcceptsOrderedBounds() =>
        Assert.Null(SetupValidators.ValidateRegion(south: 25, north: 40, west: -105, east: -90));

    [Theory]
    [InlineData(40, 25, -105, -90)]   // south >= north
    [InlineData(25, 40, -90, -105)]   // west >= east
    [InlineData(25, 25, -105, -90)]   // degenerate latitude span
    public void ValidateRegion_RejectsUnordered(double s, double n, double w, double e) =>
        Assert.NotNull(SetupValidators.ValidateRegion(s, n, w, e));

    // ---- the prompt loop --------------------------------------------------

    /// <summary>Feeds a scripted answer sequence; captures what was written.</summary>
    private static ConsolePrompter Prompter(IEnumerable<string> answers, List<string>? output = null)
    {
        var queue = new Queue<string>(answers);
        return new ConsolePrompter(
            readLine: () => queue.Count > 0 ? queue.Dequeue() : null,
            write: line => output?.Add(line));
    }

    [Fact]
    public void PromptFoundational_CollectsEveryFieldInOrder()
    {
        var prompter = Prompter(new[]
        {
            "KDFW", "32.9", "-97.0", "2.5", "25", "40", "-105", "-90", "conus",
        });

        var inputs = prompter.PromptFoundational();

        Assert.Equal("KDFW", inputs.HomeIcao);
        Assert.Equal(32.9, inputs.HomeLatitude);
        Assert.Equal(-97.0, inputs.HomeLongitude);
        Assert.Equal(2.5, inputs.BoundingBoxDegrees);
        Assert.Equal(25, inputs.RegionSouth);
        Assert.Equal(40, inputs.RegionNorth);
        Assert.Equal(-105, inputs.RegionWest);
        Assert.Equal(-90, inputs.RegionEast);
        Assert.Equal("conus", inputs.MapExtent);
    }

    [Fact]
    public void PromptFoundational_RepromptsUntilValid_AndReportsWhy()
    {
        var output = new List<string>();
        var prompter = Prompter(
            new[]
            {
                "NO", "KDFW",            // malformed ICAO (too short), then good
                "99", "32.9",            // latitude out of range, then good
                "-97.0", "2.5", "25", "40", "-105", "-90", "conus",
            },
            output);

        var inputs = prompter.PromptFoundational();

        Assert.Equal("KDFW", inputs.HomeIcao);
        Assert.Equal(32.9, inputs.HomeLatitude);
        Assert.Contains(output, line => line.Contains("ICAO", System.StringComparison.OrdinalIgnoreCase));
        Assert.Contains(output, line => line.Contains("-90", System.StringComparison.Ordinal));
    }

    /// <summary>The region bounds are cross-field, so an unordered set re-asks for all four.</summary>
    [Fact]
    public void PromptFoundational_RepromptsRegionWhenBoundsUnordered()
    {
        var prompter = Prompter(new[]
        {
            "KDFW", "32.9", "-97.0", "2.5",
            "40", "25", "-105", "-90",   // south > north — rejected as a set
            "25", "40", "-105", "-90",   // corrected
            "conus",
        });

        var inputs = prompter.PromptFoundational();

        Assert.Equal(25, inputs.RegionSouth);
        Assert.Equal(40, inputs.RegionNorth);
    }

    /// <summary>End of input (Ctrl-Z / redirected empty stdin) must fail loudly, not spin forever.</summary>
    [Fact]
    public void PromptFoundational_ThrowsOnEndOfInput() =>
        Assert.Throws<System.IO.EndOfStreamException>(() => Prompter(new[] { "KDFW" }).PromptFoundational());
}