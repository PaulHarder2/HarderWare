using System.Text.Json;

using WxServices.Common.TranslationQa;

using Xunit;

namespace WxReport.Tests;

// WX-219 — the judge-package consumer logic that lives in WxServices.Common: the vocabulary join (request
// pairs ⋈ verdicts on Token, with derived status + actionable-suggestion flag) and folder discovery.
public class TranslationQaJudgePackageTests
{
    private static VocabularyPair Pair(string token, string target, bool representable = true) =>
        new(token, $"en-{token}", "ctx", "Hint", target, "tctx", representable, Note: null, Reviewed: false);

    private static JudgingRequest Request(params VocabularyPair[] vocab) =>
        new("de", "German", Array.Empty<RenderedScenario>(), vocab);

    private static JudgeResponse Verdicts(params VocabularyVerdict[] verdicts) =>
        new("de", SelfReportedConfidence: null, Array.Empty<BackTranslation>(), Array.Empty<ReportFinding>(), verdicts);

    [Fact]
    public void JoinVocabulary_matches_on_token_and_derives_status()
    {
        var req = Request(Pair("A", "alpha"), Pair("B", "beta"));
        var judged = Verdicts(
            new VocabularyVerdict("A", Accurate: true, Natural: true, "ok", Suggestion: null),
            new VocabularyVerdict("B", Accurate: false, Natural: true, "clumsy", Suggestion: "besser"));

        var (rows, orphans) = JudgePackageStore.JoinVocabulary(req, judged);

        Assert.Empty(orphans);
        Assert.Equal(2, rows.Count);

        var a = rows.Single(r => r.Token == "A");
        Assert.Equal(VerdictStatus.Ok, a.Status);
        Assert.False(a.HasActionableSuggestion);

        var b = rows.Single(r => r.Token == "B");
        Assert.Equal(VerdictStatus.Warn, b.Status);
        Assert.True(b.HasActionableSuggestion); // "besser" differs from target "beta"
    }

    [Fact]
    public void JoinVocabulary_unjudged_request_token_is_NotJudged()
    {
        var (rows, _) = JudgePackageStore.JoinVocabulary(Request(Pair("A", "alpha")), Verdicts());
        Assert.Equal(VerdictStatus.NotJudged, rows.Single().Status);
        Assert.False(rows.Single().HasActionableSuggestion);
    }

    [Fact]
    public void JoinVocabulary_wrong_when_neither_accurate_nor_natural()
    {
        var judged = Verdicts(new VocabularyVerdict("A", Accurate: false, Natural: false, "bad", Suggestion: null));
        var (rows, _) = JudgePackageStore.JoinVocabulary(Request(Pair("A", "alpha")), judged);
        Assert.Equal(VerdictStatus.Wrong, rows.Single().Status);
    }

    [Fact]
    public void JoinVocabulary_unrepresentable_overrides_even_with_a_verdict()
    {
        var req = Request(Pair("A", target: "", representable: false));
        var judged = Verdicts(new VocabularyVerdict("A", true, true, "", Suggestion: null));
        var (rows, _) = JudgePackageStore.JoinVocabulary(req, judged);
        Assert.Equal(VerdictStatus.Unrepresentable, rows.Single().Status);
    }

    [Fact]
    public void JoinVocabulary_suggestion_equal_to_target_is_not_actionable()
    {
        var judged = Verdicts(new VocabularyVerdict("A", true, false, "", Suggestion: "alpha")); // == target
        var (rows, _) = JudgePackageStore.JoinVocabulary(Request(Pair("A", "alpha")), judged);
        Assert.False(rows.Single().HasActionableSuggestion);
    }

    [Fact]
    public void JoinVocabulary_verdict_without_a_request_token_is_an_orphan()
    {
        var judged = Verdicts(
            new VocabularyVerdict("A", true, true, "", null),
            new VocabularyVerdict("ZZZ", true, true, "", null));
        var (rows, orphans) = JudgePackageStore.JoinVocabulary(Request(Pair("A", "alpha")), judged);
        Assert.Single(rows);
        Assert.Single(orphans);
        Assert.Equal("ZZZ", orphans[0].Token);
    }

    [Fact]
    public void Discover_pairs_judged_with_request_newest_first_and_skips_unpaired()
    {
        var dir = TempDir();
        try
        {
            WritePair(dir, "de", "20260101-000000");
            WritePair(dir, "es", "20260102-000000"); // newer
            // a subfolder with a verdict but no request → not a usable package
            var frSub = Directory.CreateDirectory(Path.Combine(dir, "fr.20260103-000000")).FullName;
            File.WriteAllText(Path.Combine(frSub, "fr.20260103-000000.judged.json"),
                JsonSerializer.Serialize(Verdicts(), TranslationQaJson.Write));

            var refs = JudgePackageStore.Discover(dir);

            Assert.Equal(2, refs.Count);
            Assert.Equal(("es", "20260102-000000"), (refs[0].Iso, refs[0].Stamp)); // newest first
            Assert.Equal(("de", "20260101-000000"), (refs[1].Iso, refs[1].Stamp));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Discover_missing_folder_returns_empty()
    {
        Assert.Empty(JudgePackageStore.Discover(Path.Combine(Path.GetTempPath(), "wxqa-missing-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void Load_roundtrips_request_and_judged_through_the_shared_json_options()
    {
        var dir = TempDir();
        try
        {
            WritePair(dir, "de", "20260101-000000");
            var pkg = JudgePackageStore.Load(JudgePackageStore.Discover(dir).Single());

            Assert.Equal("de", pkg.Judged.Language);
            Assert.Equal("gemini-test", pkg.Judged.JudgedBy); // source stamp survives the round-trip
            Assert.Equal("German", pkg.Request.TargetDisplayName);
            Assert.Single(pkg.Vocabulary);
            Assert.Equal(VerdictStatus.Ok, pkg.Vocabulary[0].Status);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wxqa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // A package is a per-check subfolder "<iso>.<stamp>" holding "<iso>.<stamp>.request.json" +
    // "<iso>.<stamp>.judged.json" (WX-232).
    private static void WritePair(string dir, string iso, string stamp)
    {
        var name = $"{iso}.{stamp}";
        var sub = Directory.CreateDirectory(Path.Combine(dir, name)).FullName;
        File.WriteAllText(Path.Combine(sub, $"{name}.request.json"),
            JsonSerializer.Serialize(new JudgingRequest(iso, "German", Array.Empty<RenderedScenario>(), [Pair("A", "alpha")]), TranslationQaJson.Write));
        File.WriteAllText(Path.Combine(sub, $"{name}.judged.json"),
            JsonSerializer.Serialize(new JudgeResponse(iso, null, Array.Empty<BackTranslation>(), Array.Empty<ReportFinding>(),
                [new VocabularyVerdict("A", true, true, "", null)], JudgedBy: "gemini-test"), TranslationQaJson.Write));
    }
}