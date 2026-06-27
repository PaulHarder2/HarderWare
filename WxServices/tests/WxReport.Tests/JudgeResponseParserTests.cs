using WxReport.Tools.TranslationQa;

using Xunit;

namespace WxReport.Tests;

/// <summary>WX-218 — the response parser is the shared core (manual paste today, API later); these lock
/// its tolerance of how models actually return JSON (bare, fenced, prose-wrapped) and its clear failure.</summary>
public class JudgeResponseParserTests
{
    private const string FullJson = """
    {
      "language": "de",
      "selfReportedConfidence": { "level": "high", "note": "Native-level German." },
      "backTranslations": [ { "scenario": "warm-convective", "english": "Reported conditions at Spring." } ],
      "reportFindings": [ { "scenario": "warm-convective", "location": "summary", "problem": "stiff phrasing", "suggestedFix": "loosen it" } ],
      "vocabularyVerdicts": [ { "token": "rain_light", "accurate": true, "natural": false, "comment": "clumsy", "suggestion": "leichter Niederschlag" } ]
    }
    """;

    [Fact]
    public void TryParse_BareJson_Succeeds_AndPopulatesFields()
    {
        Assert.True(JudgeResponseParser.TryParse(FullJson, out var r, out var err));
        Assert.Null(err);
        Assert.NotNull(r);
        Assert.Equal("de", r!.Language);
        Assert.Equal("high", r.SelfReportedConfidence!.Level);
        Assert.Single(r.BackTranslations);
        Assert.Single(r.ReportFindings);
        Assert.Single(r.VocabularyVerdicts);
        Assert.Equal("leichter Niederschlag", r.VocabularyVerdicts[0].Suggestion); // native script round-trips
        Assert.False(r.VocabularyVerdicts[0].Natural);
    }

    [Fact]
    public void TryParse_FencedJson_WithSurroundingProse_Succeeds()
    {
        var raw = "Sure — here is my review:\n\n```json\n" + FullJson + "\n```\n\nLet me know if you need more.";
        Assert.True(JudgeResponseParser.TryParse(raw, out var r, out _));
        Assert.Equal("de", r!.Language);
    }

    [Fact]
    public void TryParse_ProseWrapped_NoFence_Succeeds()
    {
        var raw = "Here you go: " + FullJson + " — hope that helps!";
        Assert.True(JudgeResponseParser.TryParse(raw, out var r, out _));
        Assert.Equal("de", r!.Language);
    }

    [Fact]
    public void TryParse_MissingArrays_NormalizesToEmpty_NotNull()
    {
        Assert.True(JudgeResponseParser.TryParse("""{ "language": "de" }""", out var r, out _));
        Assert.NotNull(r);
        Assert.Empty(r!.BackTranslations);
        Assert.Empty(r.ReportFindings);
        Assert.Empty(r.VocabularyVerdicts);
    }

    [Fact]
    public void TryParse_Empty_FailsWithError()
    {
        Assert.False(JudgeResponseParser.TryParse("   ", out var r, out var err));
        Assert.Null(r);
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void TryParse_NoJsonObject_FailsWithError()
    {
        Assert.False(JudgeResponseParser.TryParse("I couldn't do that.", out var r, out var err));
        Assert.Null(r);
        Assert.Contains("no JSON object", err);
    }

    [Fact]
    public void TryParse_MalformedJson_FailsWithError()
    {
        Assert.False(JudgeResponseParser.TryParse("""{ "language": "de", "backTranslations": [ }""", out var r, out var err));
        Assert.Null(r);
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void TryParse_BracesInsideStringValues_DoNotConfuseExtraction()
    {
        // A comment containing { } must not break the balanced-brace scan.
        var raw = """
        Here: { "language": "de", "vocabularyVerdicts": [ { "token": "x", "accurate": true, "natural": true, "comment": "use {placeholder} like {0}", "suggestion": null } ] } — done.
        """;
        Assert.True(JudgeResponseParser.TryParse(raw, out var r, out _));
        Assert.Equal("de", r!.Language);
        Assert.Equal("use {placeholder} like {0}", r.VocabularyVerdicts[0].Comment);
    }

    [Fact]
    public void TryParse_TrailingProseWithBraces_StopsAtObjectEnd()
    {
        var raw = FullJson + "\n\nThanks! {ping me if needed}";
        Assert.True(JudgeResponseParser.TryParse(raw, out var r, out _));
        Assert.Equal("de", r!.Language);
        Assert.Single(r.VocabularyVerdicts);
    }
}