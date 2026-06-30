using MetarParser.Data.Entities;

using WxReport.Svc.TranslationQa;

using Xunit;

namespace WxReport.Tests;

// WX-235: a rerun is Succeeded ONLY when it produced a complete judged package — every scenario reconciled.
// Anything less (no package, or a partial one missing scenarios) is Failed, never a ✓ over a missing/partial
// package that would silently supersede the prior complete one — the exact problem WX-235 exists to fix. (A
// shutdown-cancelled run never reaches OutcomeFor: it throws OperationCanceledException and its claim is
// released for re-run on restart — covered by ClaudeClientRetryTests.HostShutdownCancellation_Propagates_NotRetried.)
public class QaRerunWorkerOutcomeTests
{
    private static TranslationQaRunner.Result Result(bool judged, bool anyScenarioFailed, int scenariosRendered) =>
        new(
            TargetIso: "da",
            Stamp: "20260630-205208422",
            PackageDir: @"C:\x\da.20260630-205208422",
            ScenariosRendered: scenariosRendered,
            AnyScenarioFailed: anyScenarioFailed,
            RequestMarkdown: null,
            RequestPath: null,
            Judged: judged);

    [Fact]
    public void CompleteRun_AllScenariosReconciled_IsSucceeded()
    {
        var (status, stamp, error) = QaRerunWorker.OutcomeFor(Result(judged: true, anyScenarioFailed: false, scenariosRendered: 2));

        Assert.Equal(QaRerunStatus.Succeeded, status);
        Assert.Equal("20260630-205208422", stamp);
        Assert.Null(error);
    }

    [Fact]
    public void EmptyRun_NoScenarioReconciled_IsFailed()
    {
        // The 'da' run interrupted by service shutdown in WX-235's first §13 attempt: a stamp/folder were
        // created but no judged.json — Judged is false, and it must NOT be Succeeded.
        var (status, stamp, error) = QaRerunWorker.OutcomeFor(Result(judged: false, anyScenarioFailed: true, scenariosRendered: 0));

        Assert.Equal(QaRerunStatus.Failed, status);
        Assert.Null(stamp);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void PartialRun_SomeScenariosFailed_IsFailed_NotASilentCheck()
    {
        // A judged package was written, but only for the scenarios that reconciled — a partial package is still
        // an honest ⚠, not a ✓ that would supersede the prior complete package with an incomplete one.
        var (status, stamp, error) = QaRerunWorker.OutcomeFor(Result(judged: true, anyScenarioFailed: true, scenariosRendered: 1));

        Assert.Equal(QaRerunStatus.Failed, status);
        Assert.Null(stamp);
        Assert.Contains("incomplete", error);
    }
}