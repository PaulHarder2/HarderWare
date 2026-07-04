using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

/// <summary>
/// WX-238 no-op-regression guard: the approved-vocabulary glossary must actually reach the assembled
/// reconciler system prompt. Unit-tests the internal <c>ForecastReconciler.BuildReconcilerSystemPrompt</c>
/// seam directly — if a future edit drops the append (or the DI loader), the glossary silently never
/// reaches Claude and the fix reverts to a no-op with the isolated NarrativeGlossaryTests still green.
/// </summary>
public class ReconcilerSystemPromptTests
{
    [Fact]
    public void SystemPrompt_AppendsGlossary_WhenProvided()
    {
        var prompt = ForecastReconciler.BuildReconcilerSystemPrompt(
            ["en", "es"], ReportKind.Scheduled, allowSkip: false, "MARKER-GLOSSARY-9Z");
        Assert.Contains("MARKER-GLOSSARY-9Z", prompt);
    }

    [Fact]
    public void SystemPrompt_NoGlossarySection_WhenEmpty()
    {
        var prompt = ForecastReconciler.BuildReconcilerSystemPrompt(
            ["en", "es"], ReportKind.Scheduled, allowSkip: false, "");
        // The per-report glossary header lives only in the appended block; empty glossary → absent.
        Assert.DoesNotContain("Approved vocabulary for this report", prompt);
    }

    [Fact]
    public void Guidance_LocksProbabilityTierRule_AgainstSilentDrop()
    {
        // WX-238 (reopened): the probability-tier rule was added after a QA-only regression — the
        // narrative rendered English "likely" as the un-anchored "se espera" (the *expected*
        // register) — slipped past the suite. Lock the distinctive phrasing so a future prompt edit
        // can't silently drop the tier-substitution constraint (CodeRabbit).
        var guidance = ReconcilerPrompts.ReconciliationGuidanceText;
        Assert.Contains("NOT interchangeable", guidance);
        Assert.Contains("se espera", guidance);
    }

    [Fact]
    public void Guidance_LocksIdiomaticVocabularyLicense_AllowsRootTransformation()
    {
        // WX-258: the vocabulary-usage license was widened from "inflect for agreement + recase" to the
        // full same-root idiomatic set (compounding / derivation) so the free narrative can write
        // de "Freitagnachmittag" / eo "posttagmeze" from the approved daypart root — while still forbidding
        // a synonym or the forced dictionary form. Lock the distinctive phrasing against a silent narrowing.
        // Collapse the prompt's line-wrapping so a multi-word phrase matches wherever it wraps.
        var guidance = System.Text.RegularExpressions.Regex.Replace(
            ReconcilerPrompts.ReconciliationGuidanceText, @"\s+", " ");
        Assert.Contains("compounded with an adjacent word", guidance);
        Assert.Contains("derived into another part of speech", guidance);
        Assert.Contains("do not force the dictionary/citation form", guidance);
        Assert.Contains("synonym", guidance);   // synonym substitution stays forbidden
    }

    [Fact]
    public void SystemPrompt_LocksDaypartDayBoundaryRule_AgainstSilentDrop()
    {
        // WX-244: the free narrative used a night-word for the evening ("la noche del martes" =
        // Wednesday's wee hours in our model). Lock the day-boundary + early-morning discipline so a
        // future prompt edit can't silently drop it — it's a QA-only regression, like the probability
        // tier above, so the suite has to hold it.
        var prompt = ForecastReconciler.BuildReconcilerSystemPrompt(
            ["en", "es"], ReportKind.Scheduled, allowSkip: false, "");
        Assert.Contains("Never attach a night word to a day name", prompt);
        Assert.Contains("la madrugada", prompt);   // the 00-06 = early-morning framing
    }
}