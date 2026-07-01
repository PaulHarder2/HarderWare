using MetarParser.Data.Entities;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

/// <summary>
/// WX-238 NarrativeGlossary: builds the approved-vocabulary glossary the reconciler injects so the
/// free-composed narrative (WX-128) anchors on curated <see cref="LanguageTemplate"/> terms instead
/// of free-generating a synonym. In-memory rows; no DB, no Claude.
/// </summary>
public class NarrativeGlossaryTests
{
    private static readonly Language En = new() { Id = 1, IsoCode = "en", DisplayName = "English" };
    private static readonly Language Es = new() { Id = 2, IsoCode = "es", DisplayName = "Spanish" };

    private static LanguageTemplate Row(Language lang, string token, string phrase, bool representable = true) =>
        new() { LanguageId = lang.Id, Language = lang, Token = token, Phrase = phrase, Representable = representable };

    // A freezing-drizzle concept (the one that drifts to "engelante") + a rain concept, en + es.
    private static List<LanguageTemplate> Rows() =>
    [
        Row(En, "drizzle_freezing", "freezing drizzle"),
        Row(En, "rain", "rain"),
        Row(Es, "drizzle_freezing", "llovizna helada"),
        Row(Es, "rain", "lluvia"),
    ];

    private static LanguageTemplateStore Store(IEnumerable<string> glossary) =>
        new(Rows, () => glossary.ToList());

    [Fact]
    public void Build_UsesApprovedTargetTerm_ForAnchoredConcept()
    {
        var g = NarrativeGlossary.Build(Store(["drizzle_freezing"]), ["en", "es"]);
        Assert.Contains("es: freezing drizzle → «llovizna helada»", g);
    }

    [Fact]
    public void Build_IncludesEnglish_AsIdentityAnchor()
    {
        var g = NarrativeGlossary.Build(Store(["drizzle_freezing"]), ["en", "es"]);
        Assert.Contains("en: freezing drizzle → «freezing drizzle»", g);
    }

    [Fact]
    public void Build_ExcludesConcepts_NotInGlossarySet()
    {
        // rain is a real token but deliberately NOT anchored — it must not appear.
        var g = NarrativeGlossary.Build(Store(["drizzle_freezing"]), ["en", "es"]);
        Assert.DoesNotContain("lluvia", g);
        Assert.DoesNotContain("rain", g);
    }

    [Fact]
    public void Build_SkipsConcept_BlockedInALanguage()
    {
        var rows = Rows();
        rows.RemoveAll(r => r.Language == Es && r.Token == "drizzle_freezing");
        rows.Add(Row(Es, "drizzle_freezing", "", representable: false));   // no approved phrase to anchor to
        var store = new LanguageTemplateStore(() => rows, () => new List<string> { "drizzle_freezing" });

        Assert.Equal("", NarrativeGlossary.Build(store, ["es"]));
    }

    [Fact]
    public void Build_ReturnsEmpty_WhenNoGlossaryTokens()
    {
        var store = new LanguageTemplateStore(Rows);   // no glossary loader supplied
        Assert.Equal("", NarrativeGlossary.Build(store, ["en", "es"]));
    }

    [Fact]
    public void Store_DropsUnknownGlossaryToken_NotInTokContract()
    {
        // A stale/renamed token that is not in the renderer's Tok contract is dropped fail-closed.
        var store = Store(["drizzle_freezing", "totally_not_a_real_token"]);
        Assert.Contains("drizzle_freezing", store.GlossaryTokens);
        Assert.DoesNotContain("totally_not_a_real_token", store.GlossaryTokens);
    }
}