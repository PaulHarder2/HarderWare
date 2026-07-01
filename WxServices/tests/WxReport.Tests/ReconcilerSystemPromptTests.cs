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
}