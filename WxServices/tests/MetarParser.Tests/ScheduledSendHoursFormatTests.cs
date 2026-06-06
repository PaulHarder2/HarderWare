using MetarParser.Data;

using Xunit;

namespace MetarParser.Tests;

public class ScheduledSendHoursFormatTests
{
    // ── Parse (lenient runtime reader) ───────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NullOrBlank_YieldsEmpty(string? raw)
        => Assert.Empty(ScheduledSendHoursFormat.Parse(raw));

    [Fact]
    public void Parse_SortsValidHours()
        => Assert.Equal([6, 12, 18], ScheduledSendHoursFormat.Parse("18, 6, 12"));

    [Fact]
    public void Parse_SilentlyDropsInvalidAndOutOfRangeTokens()
    {
        // The lenient contract: bad tokens vanish, good ones survive — this is
        // the long-standing WxReport.Svc runtime behavior, preserved verbatim.
        Assert.Equal([7], ScheduledSendHoursFormat.Parse("7, 24, -1, 7am"));
    }

    [Fact]
    public void Parse_AllInvalid_YieldsEmpty()
        => Assert.Empty(ScheduledSendHoursFormat.Parse("24, noon"));

    [Fact]
    public void Parse_AcceptsBoundaryHours()
        => Assert.Equal([0, 23], ScheduledSendHoursFormat.Parse("23, 0"));

    // ── TryValidate (strict UI gate) ─────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryValidate_NullOrBlank_IsValid(string? raw)
    {
        Assert.True(ScheduledSendHoursFormat.TryValidate(raw, out var bad));
        Assert.Null(bad);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("6, 12")]
    [InlineData("0,23")]
    public void TryValidate_AcceptsWellFormedLists(string raw)
        => Assert.True(ScheduledSendHoursFormat.TryValidate(raw, out _));

    [Theory]
    [InlineData("24", "24")]
    [InlineData("-1", "-1")]
    [InlineData("6, 12, 7am", "7am")]
    [InlineData("6,, 12", "")]
    public void TryValidate_RejectsAndNamesFirstBadToken(string raw, string expectedBad)
    {
        Assert.False(ScheduledSendHoursFormat.TryValidate(raw, out var bad));
        Assert.Equal(expectedBad, bad);
    }
}