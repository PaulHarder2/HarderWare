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
        // register) — slipped past the suite. WX-284 step 2 (Niki) then collapsed the possible/likely
        // pair into one "likely" register while keeping "expected" for certain; lock the collapse
        // premise AND the surviving "se espera" verb-construction ban so a future prompt edit can't
        // silently drop either constraint. Whitespace-collapse so a multi-word phrase matches wherever
        // it line-wraps.
        var guidance = System.Text.RegularExpressions.Regex.Replace(
            ReconcilerPrompts.ReconciliationGuidanceText, @"\s+", " ");
        Assert.Contains("read as the same register", guidance);   // possible == likely (the collapse)
        Assert.Contains("move as no change", guidance);           // ... so it is not an upgrade to narrate
        Assert.Contains("se espera", guidance);                   // the verb-construction ban survives
        Assert.Contains("we never promise it", guidance);         // severe is ALWAYS "possible", never likely/expected (Paul)
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
    public void Guidance_LocksInclusiveSpanRule_AgainstSilentDrop()
    {
        // WX-239 (reinforce): the span_through/span_until glossary tokens were correct, but the free
        // narrative kept emitting the bare ambiguous preposition (de "bis Samstag", es "hasta el
        // viernes") — the Gemini QA judge caught it against v1.60.0. A direct imperative rule was added
        // so the inclusive form is MANDATED for a "through [day]" span, while a plain "until [day]"
        // boundary keeps the ordinary word (the deliberate through/until asymmetry). Lock both halves
        // against a silent prompt edit. Whitespace-collapse so a phrase matches wherever it line-wraps.
        var guidance = System.Text.RegularExpressions.Regex.Replace(
            ReconcilerPrompts.ReconciliationGuidanceText, @"\s+", " ");
        Assert.Contains("MUST take the target language's inclusive form", guidance);
        Assert.Contains("bis einschließlich Samstag", guidance);              // de required inclusive form...
        Assert.Contains("never \"bis Samstag\"", guidance);                   // ...and the banned bare form
        Assert.Contains("hasta el final del sábado", guidance);               // es required inclusive form...
        Assert.Contains("never \"hasta el sábado\"", guidance);               // ...and its banned bare form
        Assert.Contains("inkluzive de", guidance);                            // eo inclusive form
        Assert.Contains("span_through", guidance);
        Assert.Contains("do not force an inclusive reading there", guidance); // "until" stays loose (asymmetry)
    }

    [Fact]
    public void SystemPrompt_CapeGuidance_GatesStormWordingOnSevereFlag_NotCapeMagnitude()
    {
        // WX-293: the CAPE guidance used to license non-severe storm wording ("low CAPE warrants at most
        // a mention of an isolated storm"), contradicting the WX-284/WX-293 storm-word gate — and it
        // misfires in winter, where convective snow (thundersnow) runs at LOW CAPE, so the "low CAPE →
        // isolated storm" branch would push storm wording onto a frozen, non-severe window. It now feeds
        // severeFlag only; recipient storm wording is gated on severeFlag. Lock that so a future edit
        // can't reintroduce CAPE-magnitude storm licensing.
        var prompt = ForecastReconciler.BuildReconcilerSystemPrompt(
            ["en", "es"], ReportKind.Scheduled, allowSkip: false, "");
        Assert.DoesNotContain("isolated storm", prompt);           // the retired non-severe storm license
        Assert.Contains("judge a window's thunderstorm severity", prompt);   // CAPE → severeFlag, not prose
        Assert.Contains("severeFlag", prompt);
        // WX-293 (CR round 2): severeFlag ALONE must not license "severe storms" — that is reserved for a
        // severe CONVECTIVE window; a severe non-convective (wind) event is "severe weather" (WX-284).
        Assert.Contains("severe CONVECTIVE window", prompt);
        Assert.Contains("severeFlag alone does not authorize storm wording", prompt);
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