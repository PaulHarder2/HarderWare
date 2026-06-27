using MetarParser.Data.Entities;

using WxReport.Svc;
using WxReport.Tools.TranslationQa;

using Xunit;

namespace WxReport.Tests;

/// <summary>
/// WX-215 — validates the translation-QA exemplar fixtures (<see cref="Exemplars"/>): every scenario's
/// provisional forecast renders cleanly against its primary and every alternate observation, in each
/// seeded language, via the narrative-free degraded path (no Claude/DB needed — the renderer's structured
/// grid + Reported-Conditions block is exactly what the QA harness audits). This is the "consume without
/// error" acceptance gate; the coverage matrix (COVERAGE.md) is regenerated from the dumped HTML by
/// setting WXQA_DUMP_DIR (a no-op in CI).
/// </summary>
public class ExemplarFixtureTests
{
    private static readonly LanguageTemplateStore Store = SeedTemplateStore.Build();
    private static readonly string[] Languages = ["en", "es"];

    private static Recipient Rec() => new()
    {
        RecipientId = "qa",
        Name = "QA",
        Email = "qa@example.com",
        TempUnit = "F",
        PressureUnit = "inHg",
        WindSpeedUnit = "mph",
        PrecipUnit = "in",
    };

    public static IEnumerable<object[]> ScenarioLanguages()
    {
        foreach (var s in Exemplars.All())
            foreach (var lang in Languages)
                yield return [s.Name, lang];
    }

    [Theory]
    [MemberData(nameof(ScenarioLanguages))]
    public void Scenario_RendersEveryObservation_WithoutError(string scenarioName, string lang)
    {
        var s = Exemplars.All().Single(x => x.Name == scenarioName);
        var templates = Store.ForLanguage(lang);
        var culture = Store.CultureFor(lang);
        var rec = Rec();

        IEnumerable<WxInterp.WeatherSnapshot> observations =
            [s.PrimaryObservation, .. s.AltObservations.Select(a => a.Observation)];

        foreach (var obs in observations)
        {
            var html = StructuredReportRenderer.RenderDegraded(
                s.Provisional, obs, rec, templates, culture, Exemplars.LocalityTz, s.AnchorDay);

            Assert.False(string.IsNullOrWhiteSpace(html));
            Assert.Contains("Spring", html); // locality is present → the report body rendered
        }
    }

    [Fact]
    public void Provisional_And_Prior_Honor_The_Block_Invariant()
    {
        // ForecastSnapshotBlock invariant: PrecipPhenomenon is non-null exactly when PrecipExpectation != None.
        foreach (var s in Exemplars.All())
            foreach (var b in s.Provisional.Blocks.Concat(s.Prior.Blocks))
                Assert.Equal(b.PrecipExpectation == PrecipExpectation.None, b.PrecipPhenomenon is null);
    }

    [Fact]
    public void DumpRenderedExemplars_WhenEnvVarSet()
    {
        var dir = Environment.GetEnvironmentVariable("WXQA_DUMP_DIR");
        if (string.IsNullOrWhiteSpace(dir))
            return; // no-op in CI / normal runs; set WXQA_DUMP_DIR to regenerate COVERAGE.md inputs

        Directory.CreateDirectory(dir);
        var rec = Rec();
        foreach (var s in Exemplars.All())
            foreach (var lang in Languages)
            {
                var templates = Store.ForLanguage(lang);
                var culture = Store.CultureFor(lang);

                void Dump(string label, WxInterp.WeatherSnapshot obs) =>
                    File.WriteAllText(
                        Path.Combine(dir, $"{s.Name}.{lang}.{Slug(label)}.html"),
                        StructuredReportRenderer.RenderDegraded(
                            s.Provisional, obs, rec, templates, culture, Exemplars.LocalityTz, s.AnchorDay));

                Dump("primary", s.PrimaryObservation);
                foreach (var a in s.AltObservations)
                    Dump(a.Label, a.Observation);
            }
    }

    private static string Slug(string s) =>
        string.Concat(s.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-'));
}