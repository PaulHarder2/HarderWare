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
        // variety on "rain"). Lock the absolute storm-word gate — liquid storm words require severeFlag,
        // the gate keys on severeFlag (not the snapshot's precipPhenomenon), and a non-severe window is
        // plain "rain" even when its precipPhenomenon is thunderstorm — so a future prompt edit can't
        // silently weaken it back into the general vocabulary paragraph. The gate is scoped to LIQUID
        // storm words: frozen precip keeps its own words (that clause is locked too, so the gate can't
        // drift into forbidding a legitimate "winter storm" the deterministic validator allows).
        // Whitespace-collapse so a phrase matches wherever it line-wraps.
        var guidance = System.Text.RegularExpressions.Regex.Replace(
            ReconcilerPrompts.ReconciliationGuidanceText, @"\s+", " ");
        Assert.Contains("ABSOLUTE GATE", guidance);
        Assert.Contains("keys on severeFlag, NOT on the snapshot's precipPhenomenon", guidance);
        Assert.Contains("EVEN WHEN its precipPhenomenon is thunderstorm", guidance);
        Assert.Contains("Frozen precipitation is unaffected", guidance);   // gate stays liquid-scoped
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