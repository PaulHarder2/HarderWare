using System.Globalization;

namespace WxServices.Common;

/// <summary>
/// Utilities for mapping human-readable language names (e.g. <c>"Spanish"</c> or
/// <c>"Español"</c>) to IETF language tags and localized strings.
/// </summary>
public static class LanguageHelper
{
    // ── IETF tag lookup ───────────────────────────────────────────────────────

    /// <summary>
    /// Converts a natural-language name to the corresponding BCP 47 IETF language
    /// tag suitable for use in the HTML <c>lang</c> attribute (e.g. <c>"es"</c>).
    /// <para>
    /// Both English names (<c>"Spanish"</c>) and native names (<c>"español"</c>)
    /// are recognised. The lookup searches all neutral cultures known to .NET, so
    /// any language the runtime supports can be resolved without a hard-coded list.
    /// Returns <c>"en"</c> if <paramref name="languageName"/> is blank or unrecognised.
    /// </para>
    /// </summary>
    /// <param name="languageName">
    /// Natural-language name of the desired language, in English or the language's
    /// own script (e.g. <c>"French"</c>, <c>"Français"</c>, <c>"日本語"</c>).
    /// </param>
    /// <returns>
    /// A two-letter ISO 639-1 language code such as <c>"es"</c>, <c>"fr"</c>, or
    /// <c>"ja"</c>, or <c>"en"</c> when no match is found.
    /// </returns>
    public static string ToIetfTag(string? languageName)
    {
        if (string.IsNullOrWhiteSpace(languageName))
            return "en";

        var query = languageName.Trim().ToLowerInvariant();

        var match = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .FirstOrDefault(c =>
                c.EnglishName.ToLowerInvariant() == query ||
                c.NativeName.ToLowerInvariant() == query);

        return match?.TwoLetterISOLanguageName ?? "en";
    }

    // ── Announcement subject lines ────────────────────────────────────────────

    /// <summary>Translated "HarderWare Service Announcement" subject lines, keyed on lower-case language name.</summary>
    private static readonly Dictionary<string, string> _subjects = new(StringComparer.OrdinalIgnoreCase)
    {
        ["spanish"] = "HarderWare Anuncio de servicio",
        ["español"] = "HarderWare Anuncio de servicio",
        ["french"] = "HarderWare Annonce de service",
        ["français"] = "HarderWare Annonce de service",
        ["german"] = "HarderWare Dienstankündigung",
        ["deutsch"] = "HarderWare Dienstankündigung",
        ["portuguese"] = "HarderWare Anúncio de serviço",
        ["português"] = "HarderWare Anúncio de serviço",
    };

    /// <summary>
    /// Returns the localised <c>HarderWare Service Announcement</c> email subject
    /// for <paramref name="languageName"/>, falling back to the English form for
    /// any language not yet in the translation table.
    /// </summary>
    /// <param name="languageName">
    /// Natural-language name of the recipient's language (e.g. <c>"Spanish"</c>).
    /// </param>
    /// <returns>A translated subject string, or the English default.</returns>
    public static string AnnouncementSubject(string? languageName)
        => !string.IsNullOrWhiteSpace(languageName)
            && _subjects.TryGetValue(languageName.Trim(), out var s)
            ? s
            : "HarderWare Service Announcement";
}