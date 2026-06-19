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
    public void Reload_PicksUpNewlySupportedLanguage()
    {
        // The per-cycle refresh scenario (WX-171): a language enabled/generated while the
        // service is running (WX-172) appears in the store after the next Reload — without a
        // restart — so the send gate stops failing its recipients closed.
        var rows = Sample();
        var store = new LanguageTemplateStore(() => rows);
        Assert.DoesNotContain("fr", store.LoadedLanguages);
        Assert.Equal(new[] { "rain" }, store.MissingTokens("fr", new[] { "rain" }));   // unloaded → missing

        var fr = new Language { Id = 3, IsoCode = "fr", DisplayName = "French", CultureName = "fr-FR" };
        rows.Add(Row(fr, "rain", "pluie"));
        store.Reload();

        Assert.Contains("fr", store.LoadedLanguages);
        Assert.Equal("pluie", store.ForLanguage("fr").Get("rain"));
        Assert.Empty(store.MissingTokens("fr", new[] { "rain" }));                      // now complete for that contract
    }

    [Fact]
    public void ReadAccessors_DoNotExposeMutableSnapshotState()
    {
        // WX-171 (review): the read accessors must not hand out the shared snapshot's mutable
        // collections, or a caller could downcast + mutate them and corrupt the atomic cache.
        var rows = Sample();
        rows.Add(Row(Es, "wintry_mix", "", representable: false));   // a blocked token to read back
        var store = new LanguageTemplateStore(() => rows);

        // PhrasesFor returns a read-only view, not the backing Dictionary.
        Assert.IsNotType<Dictionary<string, string>>(store.PhrasesFor("en"));

        // BlockedTokens returns a defensive copy: mutating it must not leak into the store.
        var blocked = (HashSet<string>)store.BlockedTokens("es");
        blocked.Add("intruder");
        Assert.DoesNotContain("intruder", store.BlockedTokens("es"));
        Assert.Contains("wintry_mix", store.BlockedTokens("es"));
    }

    [Fact]
    public void Lookups_NormalizeRegionalAndCaseTags_ToBaseLanguage()
    {
        // WX-171 (review): a regional or mixed-case tag ("es-419", "ES") must resolve to its base
        // language rather than miss the cache and fail the recipient closed -- the defense the
        // renderer's former NormalizeLang gave, now centralized in the store. It canonicalizes the
        // lookup KEY only; the es phrases (not en) are returned.
        var store = new LanguageTemplateStore(Sample);

        Assert.True(store.TryGetPhrase("es-419", "rain", out var p) && p == "lluvia");
        Assert.True(store.TryGetPhrase("ES", "rain", out var p2) && p2 == "lluvia");
        Assert.Equal("lluvia", store.ForLanguage("es-419").Get("rain"));
        Assert.Equal("es", store.ForLanguage("es-419").Iso);          // TemplateSet carries the bare iso for narrative selection
        Assert.Empty(store.MissingTokens("es-419", new[] { "rain", "drizzle_light" }));   // complete via the base language
    }

    [Fact]
    public void CultureFor_UsesLanguageCultureName_DefaultsToEnUsThenInvariant()
    {
        var fr = new Language { Id = 3, IsoCode = "fr", DisplayName = "French", CultureName = "fr-FR" };
        var rows = Sample();                       // En/Es carry no CultureName
        rows.Add(Row(fr, "rain", "pluie"));
        var store = new LanguageTemplateStore(() => rows);

        Assert.Equal("fr-FR", store.CultureFor("fr").Name);     // from Language.CultureName
        Assert.Equal("en-US", store.CultureFor("en").Name);     // no CultureName → en-US default
        Assert.Equal("en-US", store.CultureFor("xx").Name);     // unloaded → en-US default
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

    [Fact]
    public void ForLanguage_Get_ResolvesPhrase_AndFailsLoudlyOnMiss()
    {
        var store = new LanguageTemplateStore(Sample);
        var es = store.ForLanguage("es");

        Assert.Equal("es", es.Iso);
        Assert.Equal("lluvia", es.Get("rain"));
        Assert.True(es.Has("rain"));
        Assert.False(es.Has("snow"));

        // Fail-closed: a missing token throws, carrying iso + token (no silent en-substitution).
        var ex = Assert.Throws<MissingTemplateException>(() => es.Get("snow"));
        Assert.Equal("es", ex.IsoCode);
        Assert.Equal("snow", ex.Token);
    }

    [Fact]
    public void ForLanguage_UnloadedLanguage_GetAlwaysThrows()
    {
        var store = new LanguageTemplateStore(Sample);
        var fr = store.ForLanguage("fr");   // not loaded
        Assert.Throws<MissingTemplateException>(() => fr.Get("rain"));
    }

    [Fact]
    public void MissingTokens_ReportsAbsentAndBlocked_EmptyWhenComplete()
    {
        var rows = Sample();
        rows.Add(Row(Es, "wintry_mix", "", representable: false));   // blocked counts as missing
        var store = new LanguageTemplateStore(() => rows);
        var required = new[] { "rain", "drizzle_light", "snow", "wintry_mix" };

        // en: has rain + drizzle_light; missing snow + wintry_mix (never seeded).
        Assert.Equal(new[] { "snow", "wintry_mix" }, store.MissingTokens("en", required));
        // es: has rain + drizzle_light; snow absent; wintry_mix blocked -> both missing.
        Assert.Equal(new[] { "snow", "wintry_mix" }, store.MissingTokens("es", required));
        // A complete contract returns empty.
        Assert.Empty(store.MissingTokens("es", new[] { "rain", "drizzle_light" }));
        // An unloaded language is missing everything required.
        Assert.Equal(new[] { "drizzle_light", "rain" }, store.MissingTokens("fr", new[] { "rain", "drizzle_light" }));
    }
}