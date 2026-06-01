using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

// Structural guard for the WX-81 significance-tier guidance. The reconciliation
// prompt is prose, but its tier scaffolding is safety-relevant: a future prompt
// edit must not silently drop a tier, the directional-asymmetry rule, the 34 kt
// wind line, or either decision tool. These assertions lock that structure into
// the suite (WX-81 AC #1-3). They check presence, not wording.

public class ReconcilerPromptsTests
{
    [Theory]
    [InlineData("Significance hierarchy")]
    [InlineData("Safety-critical")]
    [InlineData("Plans-affecting")]
    [InlineData("Ambient-interest")]
    [InlineData("Directional asymmetry")]
    [InlineData("34 kt")]
    [InlineData("submit_reconciled_report")]
    [InlineData("skip_send")]
    public void ReconciliationGuidance_ContainsTierScaffolding(string marker)
    {
        Assert.Contains(marker, ReconcilerPrompts.ReconciliationGuidanceText);
    }

    [Fact]
    public void ReconciliationGuidance_DistinguishesSevereFromOrdinaryStorms()
    {
        // The crux of the safety tier: severity (severeFlag / convective signals),
        // not the bare word "thunderstorm", is what escalates. Assert both halves
        // of the distinction survive in the prompt.
        var text = ReconcilerPrompts.ReconciliationGuidanceText;
        Assert.Contains("severe thunderstorms", text);
        Assert.Contains("non-severe thunderstorm", text);
    }
}