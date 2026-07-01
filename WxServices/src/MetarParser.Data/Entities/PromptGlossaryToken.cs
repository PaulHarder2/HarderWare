namespace MetarParser.Data.Entities;

/// <summary>
/// One language-neutral vocabulary token (a <c>ReportTokens.Tok</c> concept key, e.g.
/// <c>"drizzle_freezing"</c>) whose approved <see cref="LanguageTemplate"/> phrase should be
/// injected into the forecast-reconciler prompt as an anchoring glossary (WX-238). The
/// free-composed per-language narrative (WX-128) otherwise free-generates weather terms and can
/// drift off the curated vocabulary — e.g. the Spanish narrative writing "llovizna engelante"
/// for the approved "llovizna helada"; listing the concept here tells the reconciler to compose
/// around the approved term instead.
///
/// Deliberately language-agnostic: whether a concept belongs in the prompt glossary is a
/// property of the concept, not of any one language, so it lives once here rather than as a flag
/// duplicated across every per-(token, language) <see cref="LanguageTemplate"/> row. The
/// per-language approved phrases are still read from <see cref="LanguageTemplate"/>; this table
/// only says which concepts to anchor.
/// </summary>
public class PromptGlossaryToken
{
    /// <summary>Auto-incremented surrogate key.</summary>
    public long Id { get; set; }

    /// <summary>
    /// The language-neutral token key (e.g. <c>"drizzle_freezing"</c>). Must match a
    /// <c>ReportTokens.Tok</c> constant — validated fail-closed at load, where a row naming an
    /// unknown/renamed token is dropped with a loud ERROR. Unique.
    /// </summary>
    public string Token { get; set; } = "";

    /// <summary>Optional curation note (why this concept is anchored). Null = none.</summary>
    public string? Note { get; set; }
}