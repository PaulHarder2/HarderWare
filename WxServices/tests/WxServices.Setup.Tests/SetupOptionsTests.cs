using System;

using WxServices.Setup;

using Xunit;

namespace WxServices.Setup.Tests;

/// <summary>
/// WX-314 AC-3, test-first: argument parsing over the environment-resolved defaults, and the
/// error cases (unknown flag, missing value, invalid mode). Pure — no environment touched.
/// </summary>
public class SetupOptionsTests
{
    private static SetupOptions Parse(params string[] args) =>
        SetupOptions.Parse(args, defaultInstallRoot: @"C:\HarderWare", defaultServicesDir: @"C:\repo\services");

    [Fact]
    public void Parse_NoArgs_UsesDefaults()
    {
        var o = Parse();

        Assert.Equal("full", o.Mode);
        Assert.Equal("WeatherData", o.Database);
        Assert.Equal("wxservices", o.SqlLogin);
        Assert.Equal(@".\SQLEXPRESS", o.Server);
        Assert.Equal(@"C:\HarderWare", o.InstallRoot);
        Assert.Equal(@"C:\repo\services", o.ServicesDir);
    }

    [Fact]
    public void Parse_OverridesEveryTarget()
    {
        var o = Parse(
            "--mode", "opregion",
            "--install-root", @"C:\HarderWareTest",
            "--services-dir", @"C:\tmp\svc",
            "--database", "WeatherDataTest",
            "--sql-login", "wxservicestest",
            "--server", "OTHERSQL");

        Assert.Equal("opregion", o.Mode);
        Assert.Equal(@"C:\HarderWareTest", o.InstallRoot);
        Assert.Equal(@"C:\tmp\svc", o.ServicesDir);
        Assert.Equal("WeatherDataTest", o.Database);
        Assert.Equal("wxservicestest", o.SqlLogin);
        Assert.Equal("OTHERSQL", o.Server);
    }

    [Theory]
    [InlineData("--mode", "sideways")]   // invalid mode
    [InlineData("--bogus", "x")]         // unknown flag
    public void Parse_InvalidArg_Throws(string flag, string value) =>
        Assert.Throws<ArgumentException>(() => Parse(flag, value));

    [Fact]
    public void Parse_FlagMissingValue_Throws() =>
        Assert.Throws<ArgumentException>(() => Parse("--database"));
}