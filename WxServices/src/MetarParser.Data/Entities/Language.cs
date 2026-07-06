using System.ComponentModel.DataAnnotations.Schema;

namespace MetarParser.Data.Entities;

/// <summary>
/// The generation state of a <see cref="Language"/> (WX-172), derived from its
/// <see cref="Language.IsEnabled"/>, <see cref="Language.GeneratedAtUtc"/>, and
/// <see cref="Language.GenerationError"/> columns. Only <see cref="Ready"/> languages
/// are assignable to recipients and rendered.
/// </summary>
public enum LanguageGenerationState
{
    /// <summary>Not enabled — not a SupportedLanguage.</summary>
    Disabled,

    /// <summary>Enabled but never generated yet — the service will generate it on its next cycle.</summary>
    Pending,

    /// <summary>Enabled, generated, and not blocked — assignable and renderable.</summary>
    Ready,

    /// <summary>Generated, but a token is not representable in this language (needs a renderer/code change); not auto-retried.</summary>
    Blocked,

    /// <summary>A transient generation error (transport/parse) — auto-retried on the next cycle.</summary>
    Failed,
}

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

    /// <summary>
    /// The generation state derived from <see cref="IsEnabled"/>, <see cref="GeneratedAtUtc"/>,
    /// and <see cref="GenerationError"/> (WX-172). Computed, never persisted. The encoding:
    /// PENDING = (enabled, GeneratedAtUtc null, no error); READY = (enabled, GeneratedAtUtc set,
    /// no error); BLOCKED = (enabled, GeneratedAtUtc set, error set); FAILED = (enabled,
    /// GeneratedAtUtc null, error set). WX-253: the generation path no longer parks in BLOCKED —
    /// a non-representable token auto-disables the language (error set alongside IsEnabled=false),
    /// which DISABLED (checked first) reports; BLOCKED now denotes only a hand-edited enabled+error
    /// state (which a re-enable recovers).
    /// </summary>
    [NotMapped]
    public LanguageGenerationState GenerationState =>
        !IsEnabled ? LanguageGenerationState.Disabled
        : GenerationError is not null
            ? (GeneratedAtUtc is not null ? LanguageGenerationState.Blocked : LanguageGenerationState.Failed)
        : GeneratedAtUtc is not null ? LanguageGenerationState.Ready
        : LanguageGenerationState.Pending;

    /// <summary>
    /// True when this language is READY (WX-172): enabled, generated, and not blocked — the
    /// only state in which a recipient may be assigned it and its reports render. Computed,
    /// never persisted.
    /// </summary>
    [NotMapped]
    public bool IsReady => GenerationState == LanguageGenerationState.Ready;
}