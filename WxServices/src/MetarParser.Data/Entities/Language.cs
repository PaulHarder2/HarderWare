namespace MetarParser.Data.Entities;

/// <summary>
/// A language the product knows about for recipient reports. The full table is
/// seeded from ISO 639-1 (the "AllLanguages" set); the subset with
/// <see cref="IsEnabled"/> set are the "SupportedLanguages" a recipient may actually
/// be assigned. Enabling a language asserts that localized report templates exist
/// for it — so a recipient is never assigned a language the renderer cannot produce
/// (WX-137 / WX-166). Managed via WxManager's Languages tab.
/// </summary>
public class Language
{
    /// <summary>Auto-incremented surrogate key.</summary>
    public long Id { get; set; }

    /// <summary>
    /// ISO 639-1 two-letter code, lower-case (e.g. <c>"en"</c>, <c>"es"</c>). Unique
    /// across the table; this is the stable identity matched against the renderer's
    /// supported-template set.
    /// </summary>
    public string IsoCode { get; set; } = "";

    /// <summary>English display name of the language (e.g. <c>"English"</c>, <c>"Spanish"</c>).</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// Whether this language is a SupportedLanguage — selectable for recipients.
    /// Enabling requires that localized report templates exist for the language;
    /// disabling is refused while any recipient is still assigned to it.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// IETF culture tag used for date/time names and number formatting of this
    /// language's reports (e.g. <c>"en-US"</c>, <c>"es-US"</c>). Per-language, not
    /// per-token, so it lives here rather than on <see cref="LanguageTemplate"/>.
    /// Null until the language is enabled/populated (WX-167).
    /// </summary>
    public string? CultureName { get; set; }

    /// <summary>
    /// When this language's templates were last (re)generated, UTC. Null = never
    /// generated (the PENDING state once enabled). Set by the WX-172 generator.
    /// </summary>
    public DateTime? GeneratedAtUtc { get; set; }

    /// <summary>
    /// Why the language is not ready, if generation could not produce a usable set
    /// — e.g. a token Claude flagged as not representable by simple substitution
    /// (BLOCKED-needs-code). Null = no blocking problem. Set by the WX-172 generator;
    /// a BLOCKED language is not auto-retried (the renderer assembly is the problem).
    /// </summary>
    public string? GenerationError { get; set; }

    /// <summary>The localized templates that render this language's reports (WX-167).</summary>
    public ICollection<LanguageTemplate> Templates { get; set; } = new List<LanguageTemplate>();
}