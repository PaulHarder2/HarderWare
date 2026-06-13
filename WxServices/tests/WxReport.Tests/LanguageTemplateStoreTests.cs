using MetarParser.Data.Entities;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

/// <summary>
/// WX-171 LanguageTemplateStore: the load/cache layer that maps isoCode → token → phrase
/// from <see cref="LanguageTemplate"/> rows, with the Reload()/Invalidate() seam. Tests
/// use an in-memory row provider — no database — and never touch the renderer.
/// </summary>
public class LanguageTemplateStoreTests
{
    private static readonly Language En = new() { Id = 1, IsoCode = "en", DisplayName = "English" };
    private static readonly Language Es = new() { Id = 2, IsoCode = "es", DisplayName = "Spanish" };

    private static LanguageTemplate Row(Language lang, string token, string phrase, bool representable = true) =>
        new() { LanguageId = lang.Id, Language = lang, Token = token, Phrase = phrase, Representable = representable };

    private static List<LanguageTemplate> Sample() =>
    [
        Row(En, "rain", "rain"),
        Row(En, "drizzle_light", "light drizzle"),
        Row(Es, "rain", "lluvia"),
        Row(Es, "drizzle_light", "llovizna ligera"),
    ];

    [Fact]
    public void Load_GroupsByLanguage_AndResolvesPhrases()
    {
        var store = new LanguageTemplateStore(Sample);

        Assert.True(store.TryGetPhrase("en", "rain", out var en));
        Assert.Equal("rain", en);
        Assert.True(store.TryGetPhrase("es", "drizzle_light", out var es));
        Assert.Equal("llovizna ligera", es);
        Assert.Equal(new HashSet<string> { "en", "es" }, store.LoadedLanguages);
    }

    [Theory]
    [InlineData("fr", "rain")]   // language not loaded
    [InlineData("en", "snow")]   // token not present
    [InlineData("", "rain")]     // empty iso
    [InlineData("en", "")]       // empty token
    public void TryGetPhrase_Miss_ReturnsFalseAndEmpty(string iso, string token)
    {
        var store = new LanguageTemplateStore(Sample);
        Assert.False(store.TryGetPhrase(iso, token, out var phrase));
        Assert.Equal("", phrase);
    }

    [Fact]
    public void BlockedToken_IsExcludedFromPhrases_ButReported()
    {
        var rows = Sample();
        rows.Add(Row(Es, "wintry_mix", "", representable: false));   // BLOCKED-needs-code in Spanish
        var store = new LanguageTemplateStore(() => rows);

        Assert.False(store.TryGetPhrase("es", "wintry_mix", out _));
        Assert.Contains("wintry_mix", store.BlockedTokens("es"));
        Assert.DoesNotContain("wintry_mix", store.PhrasesFor("es").Keys);
    }

    [Fact]
    public void UnkeyableRows_AreSkipped_NotFatal()
    {
        var rows = Sample();
        rows.Add(new LanguageTemplate { Token = "orphan", Phrase = "x", Language = null });   // no language
        rows.Add(Row(En, "", "empty-token"));                                                  // no token
        var store = new LanguageTemplateStore(() => rows);

        Assert.True(store.TryGetPhrase("en", "rain", out _));   // good rows still load
        Assert.Equal(new HashSet<string> { "en", "es" }, store.LoadedLanguages);
    }

    [Fact]
    public void Reload_PicksUpSourceChanges()
    {
        var rows = Sample();
        var store = new LanguageTemplateStore(() => rows);
        Assert.False(store.TryGetPhrase("en", "snow", out _));

        rows.Add(Row(En, "snow", "snow"));
        Assert.False(store.TryGetPhrase("en", "snow", out _));   // cached snapshot unchanged until reload

        store.Reload();
        Assert.True(store.TryGetPhrase("en", "snow", out var phrase));
        Assert.Equal("snow", phrase);
    }

    [Fact]
    public void Invalidate_RebuildsLazilyOnNextRead()
    {
        var rows = Sample();
        int loads = 0;
        var store = new LanguageTemplateStore(() => { loads++; return rows; });
        Assert.Equal(1, loads);                  // initial load

        rows.Add(Row(En, "snow", "snow"));
        store.Invalidate();                      // no rebuild yet
        Assert.Equal(1, loads);

        Assert.True(store.TryGetPhrase("en", "snow", out _));   // next read rebuilds
        Assert.Equal(2, loads);

        Assert.True(store.TryGetPhrase("en", "rain", out _));   // subsequent reads do not
        Assert.Equal(2, loads);
    }
}