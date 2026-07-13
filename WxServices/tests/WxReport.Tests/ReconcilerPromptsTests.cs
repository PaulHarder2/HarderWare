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

    [Fact]
    public void ReconciliationGuidance_LocksStormWordSevereGate_AgainstSilentDrop()
    {
        // WX-293: the recurring Closing degrade came from the model rendering "storms" for a non-severe
        // window (the snapshot's non-severe precipPhenomenon=thunderstorm leaking into prose as stylistic
        // variety on "rain"). Lock the absolute, phase-aware storm-word gate — storm words require
        // severeFlag, a non-severe window takes the plain PHASE word (rain for liquid, snow for frozen),
        // and this holds even when precipPhenomenon is thunderstorm — so a future prompt edit can't
        // silently weaken it back into the general vocabulary paragraph. Whitespace-collapse so a phrase
        // matches wherever it line-wraps.
        var guidance = System.Text.RegularExpressions.Regex.Replace(
            ReconcilerPrompts.ReconciliationGuidanceText, @"\s+", " ");
        Assert.Contains("ABSOLUTE, PHASE-AWARE GATE", guidance);
        Assert.Contains("The gate keys on severeFlag, NOT on precipitation phase", guidance);
        Assert.Contains("a non-severe thunderstorm is \"rain\" to", guidance);
    }

    [Fact]
    public void ReconciliationGuidance_LocksMultiWindowClosingStormRule_AgainstSilentDrop()
    {
        // WX-293: the leak was specifically the multi-window Closing ("rain ... then storms ...") that
        // varied "rain" to "storms" for stylistic contrast across day-parts. Lock the closing-scoped
        // reminder that the storm-word gate applies to EVERY clause of the closing, not just the first.
        var guidance = System.Text.RegularExpressions.Regex.Replace(
            ReconcilerPrompts.ReconciliationGuidanceText, @"\s+", " ");
        Assert.Contains("Closing, multi-window phrasing", guidance);
        Assert.Contains("apply the storm-word gate", guidance);
        Assert.Contains("do not vary \"rain\" to \"storms\"", guidance);
    }
}