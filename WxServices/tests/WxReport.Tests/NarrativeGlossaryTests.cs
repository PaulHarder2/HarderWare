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
    public void Build_AnchorsDaypartWord_ForEvening()
    {
        // WX-244: the named dayparts are anchored so the prose uses the approved evening word
        // (es "Tarde-Noche") instead of drifting to "noche". Same concept→term mechanism as weather.
        var rows = new List<LanguageTemplate>
        {
            Row(En, Tok.DayPart4, "Evening"),
            Row(Es, Tok.DayPart4, "Tarde-Noche"),
        };
        var g = NarrativeGlossary.Build(new LanguageTemplateStore(() => rows, () => [Tok.DayPart4]), ["en", "es"]);
        Assert.Contains("es: Evening → «Tarde-Noche»", g);
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

    [Fact]
    public void Build_AnchorsSpanTokens_SoTheTargetUsesTheInclusiveForm()
    {
        // WX-239: span_through / span_until are SOFT Hint glossary tokens. Once a target language has
        // its top-up-generated phrase, the glossary anchors the inclusive/boundary rendering — de
        // "through" → «bis einschließlich» (not the day-truncating bare «bis»), "until" → «bis».
        var de = new Language { Id = 3, IsoCode = "de", DisplayName = "German" };
        var rows = new List<LanguageTemplate>
        {
            Row(En, Tok.SpanThrough, "through"),
            Row(En, Tok.SpanUntil, "until"),
            Row(de, Tok.SpanThrough, "bis einschließlich"),
            Row(de, Tok.SpanUntil, "bis"),
        };
        var store = new LanguageTemplateStore(() => rows, () => [Tok.SpanThrough, Tok.SpanUntil]);
        var g = NarrativeGlossary.Build(store, ["en", "de"]);
        // Assert each FULL per-language line, so every pair — including the mid-line `until` one that
        // joins after "; " with no repeated language prefix — is qualified by its language. This guards
        // the en identity anchors (through AND until) and the de target mappings against regression.
        Assert.Contains("de: through → «bis einschließlich»; until → «bis»", g);
        Assert.Contains("en: through → «through»; until → «until»", g);
    }

    [Fact]
    public void Build_OmitsSpanToken_WhenTargetLacksThePhrase_ButKeepsEnglishAnchor()
    {
        // Deploy-day SOFT path: en is seeded but a target has not been top-up-generated yet. The
        // target contributes no span pair (its line is simply omitted — no throw), and the en
        // identity anchor still appears. This is the behavior live on the day the migration ships.
        var de = new Language { Id = 3, IsoCode = "de", DisplayName = "German" };
        var rows = new List<LanguageTemplate>
        {
            Row(En, Tok.SpanThrough, "through"),
            Row(de, "rain", "Regen"),   // de is a LOADED language, just missing the span token (pre-top-up)
        };
        var store = new LanguageTemplateStore(() => rows, () => [Tok.SpanThrough]);
        var g = NarrativeGlossary.Build(store, ["en", "de"]);
        Assert.Contains("en: through → «through»", g);   // en anchor present
        Assert.DoesNotContain("de:", g);                 // de lacks the span phrase → omitted from the glossary, no throw
    }
}