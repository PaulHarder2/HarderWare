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
}