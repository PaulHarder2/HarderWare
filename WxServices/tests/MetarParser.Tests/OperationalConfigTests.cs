using System;
using System.Linq;

using MetarParser.Data.Configuration;

using Xunit;

namespace MetarParser.Tests;

/// <summary>
/// WX-315: the Configure tab's operational settings mapping. Lives in the data layer rather than
/// the WPF code-behind precisely so it can be covered here — WxManager is excluded from CI (WX-135).
/// </summary>
public class OperationalConfigTests
{
    [Fact]
    public void BuildEditableRows_EmitsTheThreeKeys_Trimmed()
    {
        var rows = OperationalConfig.BuildEditableRows(
            smtpHost: "  smtp.gmail.com  ", smtpPort: " 587 ", alertEmail: "  ops@example.com ");

        Assert.Equal(
            new[] { "Smtp:Host", "Smtp:Port", "Monitor:AlertEmail" },
            rows.Select(r => r.Key));
        Assert.Equal("smtp.gmail.com", rows[0].Value);
        Assert.Equal("587", rows[1].Value);
        Assert.Equal("ops@example.com", rows[2].Value);
    }

    /// <summary>
    /// A bad port must fail loudly. The pre-WX-315 tab silently substituted 587 for anything
    /// unparseable, so a typo appeared to save and quietly changed nothing.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-port")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("65536")]
    [InlineData("587.5")]
    public void BuildEditableRows_RejectsInvalidPort(string port)
    {
        var ex = Assert.Throws<ConfigWriteException>(() =>
            OperationalConfig.BuildEditableRows("smtp.gmail.com", port, "ops@example.com"));

        Assert.Contains("port", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("25")]
    [InlineData("65535")]
    public void BuildEditableRows_AcceptsPortBoundaries(string port)
    {
        var rows = OperationalConfig.BuildEditableRows("smtp.gmail.com", port, "ops@example.com");

        Assert.Equal(port, rows.Single(r => r.Key == "Smtp:Port").Value);
    }

    /// <summary>
    /// None of the tab's own keys may be bootstrap-critical — otherwise the shared write guard
    /// would reject the tab's normal save, which would be a defect discovered only at runtime.
    /// </summary>
    [Fact]
    public void EditableKeys_AreNotBootstrapCritical()
    {
        var rows = OperationalConfig.BuildEditableRows("h", "25", "a@b.c");

        Assert.All(rows, r => Assert.False(BootstrapKeys.IsBootstrapKey(r.Key)));
    }
}