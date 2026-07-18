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
}