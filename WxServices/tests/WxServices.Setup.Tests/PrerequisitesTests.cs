using WxServices.Setup;

using Xunit;

namespace WxServices.Setup.Tests;

/// <summary>
/// WX-314 AC-1: the gate DECISION and the report FORMATTING are pure, so they get unit tests here.
/// The actual probes (SQL / TCP / PATH) in <c>Prerequisites.CheckAsync</c> are I/O and are verified
/// by the functional isolated-run (docs/test-procedures/WX-314.md §1).
/// </summary>
public class PrerequisitesTests
{
    private static PrereqCheck Check(PrereqStatus status) => new("x", status, "");

    [Fact]
    public void MayProceed_TrueWhenNoFail()
    {
        Assert.True(Prerequisites.MayProceed(new[] { Check(PrereqStatus.Pass), Check(PrereqStatus.Warn) }));
    }

    [Fact]
    public void MayProceed_FalseWhenAnyFail()
    {
        Assert.False(Prerequisites.MayProceed(
            new[] { Check(PrereqStatus.Pass), Check(PrereqStatus.Fail), Check(PrereqStatus.Warn) }));
    }

    [Fact]
    public void Format_MarksEachStatus_AndShowsDetail()
    {
        var text = Prerequisites.Format(new[]
        {
            new PrereqCheck("Reachable", PrereqStatus.Pass, ""),
            new PrereqCheck("Docker", PrereqStatus.Warn, "not on PATH"),
            new PrereqCheck("Mixed Mode", PrereqStatus.Fail, "enable it"),
        });

        Assert.Contains("[ OK ] Reachable", text);
        Assert.Contains("[WARN] Docker — not on PATH", text);
        Assert.Contains("[FAIL] Mixed Mode — enable it", text);
    }

    /// <summary>
    /// The TCP probe must follow --server rather than assuming a default-port local instance,
    /// otherwise a perfectly good remote or non-default-port server is reported as a hard failure.
    /// </summary>
    [Theory]
    [InlineData(@".\SQLEXPRESS", "127.0.0.1", 1433)]
    [InlineData("(local)", "127.0.0.1", 1433)]
    [InlineData("localhost", "127.0.0.1", 1433)]
    [InlineData("LOCALHOST", "127.0.0.1", 1433)]
    [InlineData("", "127.0.0.1", 1433)]
    [InlineData("SQLBOX01", "SQLBOX01", 1433)]
    [InlineData(@"SQLBOX01\PROD", "SQLBOX01", 1433)]
    [InlineData("SQLBOX01,14330", "SQLBOX01", 14330)]
    [InlineData(@".\SQLEXPRESS,14330", "127.0.0.1", 14330)]
    [InlineData("10.0.0.5,1433", "10.0.0.5", 1433)]
    public void TcpProbeTarget_DerivesHostAndPortFromServer(string server, string host, int port)
    {
        var (actualHost, actualPort) = Prerequisites.TcpProbeTarget(server);

        Assert.Equal(host, actualHost);
        Assert.Equal(port, actualPort);
    }
}