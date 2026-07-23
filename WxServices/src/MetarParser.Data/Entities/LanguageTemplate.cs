namespace MetarParser.Data.Entities;

/// <summary>
/// Whether a template's <see cref="LanguageTemplate.ContextInfo"/> is a real
/// sentence to translate, or an English usage note to read but not translate (WX-167).
/// </summary>
public enum TemplateContextKind
{
    /// <summary>A sentence using the token; the generator translates it and returns the translation (an in-context agreement/order check).</summary>
    Example,

    /// <summary>A language-neutral English usage gloss; the generator reads it to translate the token correctly but does not translate it.</summary>
    Hint,
}

/// <summary>
/// Whether the deterministic prose validator keys on a template's <see cref="LanguageTemplate.Phrase"/>
/// as one of its word-lists (WX-335). Orthogonal to rendering: the renderer always uses the phrase;
/// this says whether the validator does too. Sub 3 (WX-336) builds the per-language validator lexicon
/// (just <c>DayPartWords</c>, after the WX-331 pivot) from the <c>Yes</c> rows — the <c>DayPart1–4</c>
/// day-part tokens a language marks validator-safe.
/// </summary>
public enum ValidatorUse
{
    /// <summary>Render-only: the validator ignores this phrase. The default for every render token.</summary>
    No,

    /// <summary>Dual-use: rendered AND used by the validator — a <c>DayPart1–4</c> day-part word the
    /// language marks unambiguous. The only validator-relevant value after the WX-331 pivot, which moved
    /// every free-prose word-bag check to the reconciler prompt, so no validator-ONLY words remain.</summary>
    Yes,
}

/// <summary>
/// One localized string the deterministic renderer substitutes into a report, keyed
/// by a language-neutral <see cref="Token"/> (WX-167). Supersedes the hard-coded
/// interim <c>ReportVocabulary</c>: the token set is the code-side contract, the
/// per-language phrases are data. Grammar-sensitive combinations are single atomic
/// tokens (agreement/order baked in), so the renderer never glues two phrases.
/// </summary>
public class LanguageTemplate
{
    /// <summary>Auto-incremented surrogate key.</summary>
    public long Id { get; set; }

    /// <summary>The owning <see cref="Language"/>.</summary>
    public long LanguageId { get; set; }

    /// <summary>Navigation to the owning language.</summary>
    public Language? Language { get; set; }

    /// <summary>
    /// The language-neutral token key (e.g. <c>"rain_light"</c>, <c>"sev_storms_likely"</c>).
    /// Unique per language; the stable identity the renderer binds against.
    /// </summary>
    public string Token { get; set; } = "";

    /// <summary>The localized phrase or format string (e.g. <c>"light rain"</c> / <c>"lluvia ligera"</c>).</summary>
    public string Phrase { get; set; } = "";

    /// <summary>
    /// The disambiguating context the generator uses when translating this token.
    /// Its role depends on <see cref="ContextKind"/>: an <see cref="TemplateContextKind.Example"/>
    /// sentence is translated and returned; a <see cref="TemplateContextKind.Hint"/> is an
    /// English usage gloss read but never translated (so a hint row keeps the English gloss
    /// in every language).
    /// </summary>
    public string ContextInfo { get; set; } = "";

    /// <summary>Whether <see cref="ContextInfo"/> is a translatable example or a read-only usage hint.</summary>
    public TemplateContextKind ContextKind { get; set; }

    /// <summary>
    /// Whether the deterministic <c>{q:time}</c>&#8596;day-part validator keys on this phrase (WX-335):
    /// <c>No</c> = render-only (default); <c>Yes</c> = rendered AND validated — a <c>DayPart1–4</c>
    /// day-part word the language marks unambiguous. Sub 3 (WX-336) builds <c>DayPartWords</c> from the
    /// <c>Yes</c> rows. (No <c>Only</c> value: the WX-331 pivot moved every validator-only word-bag to
    /// the reconciler prompt.)
    /// </summary>
    public ValidatorUse ValidatorUse { get; set; }

    /// <summary>
    /// Generator's caveat or, when not representable, the explanation of why a simple
    /// word/phrase cannot fill this slot in the language (word order / agreement /
    /// restructuring). Null = no note.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>
    /// Whether a simple word/phrase can fill this token's slot in the language. False =
    /// the language is BLOCKED-needs-code on this token (the renderer assembly needs
    /// generalizing); set by the WX-172 generator. Defaults true.
    /// </summary>
    public bool Representable { get; set; } = true;

    /// <summary>Who reviewed this phrase (WX-173 review round-trip). Null = generated-but-unreviewed.</summary>
    public string? ReviewedBy { get; set; }

    /// <summary>When this phrase was reviewed, UTC (WX-173). Null = generated-but-unreviewed.</summary>
    public DateTime? ReviewedAtUtc { get; set; }
}