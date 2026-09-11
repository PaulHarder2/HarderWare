using WxReport.Svc.TranslationQa;

using Xunit;

namespace WxReport.Tests;

/// <summary>
/// WX-504 — the harness-only Chicago scenario. It exists to make the reconciler name days and
/// day-parts in LOCAL time, so it must really be authored off UTC; and it must stay out of
/// <see cref="Exemplars.All"/>, which production QA reruns judge with Gemini on every run.
/// </summary>
public class ExemplarsHarnessOnlyTests
{
    [Fact]
    public void All_DoesNotContainTheHarnessOnlyScenario()
    {
        Assert.DoesNotContain(Exemplars.All(), s => s.Name == "chicago-day-parts");
        Assert.All(Exemplars.All(), s => Assert.Equal(TimeZoneInfo.Utc, s.Tz));   // the existing two are unchanged
    }

    [Fact]
    public void Named_FindsHarnessOnlyAndProductionScenarios_AndNothingElse()
    {
        Assert.NotNull(Exemplars.Named("chicago-day-parts"));
        Assert.NotNull(Exemplars.Named(" Warm-Convective "));   // trimmed, case-insensitive, as the tool's --scenario is
        Assert.Null(Exemplars.Named("no-such-scenario"));
    }

    [Fact]
    public void ChicagoDayParts_BlocksSitOnLocalDayPartBoundaries_AwayFromUtc()
    {
        var s = Exemplars.Named("chicago-day-parts")!;
        var blocks = s.Provisional.Blocks.Concat(s.Prior.Blocks).ToList();

        foreach (var b in blocks)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(b.StartUtc, DateTimeKind.Utc), s.Tz);
            Assert.Contains(local.Hour, new[] { 0, 6, 12, 18 });
            Assert.Equal(0, local.Minute);
        }

        // The point of the scenario: no block's UTC hour equals its local hour, so a model that reads the
        // UTC hour as local names the wrong day-part.
        Assert.All(blocks, b => Assert.NotEqual(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(b.StartUtc, DateTimeKind.Utc), s.Tz).Hour,
            b.StartUtc.Hour));
    }
}