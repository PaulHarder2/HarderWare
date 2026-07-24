namespace WxReport.Svc;

/// <summary>
/// WX-168: a per-language plugin supplying the word-list the reconciler's one surviving deterministic
/// prose validator keys on — the <c>{q:time}</c>&#8596;day-part agreement check (WX-149). The validator's
/// <em>logic</em> is language-agnostic (sentence splitting, proximity, the residual policy); only this
/// lexicon is per-language, so adding a language is one new <see cref="ILanguageLexicon"/> registered in
/// <see cref="LanguageLexicons"/> — no validator changes.
///
/// <para>
/// WX-340 reduced the contract to <see cref="IsoCode"/> + <see cref="DayPartWords"/>: the free-prose
/// word-bag validators (closing/aggregate precip-at-a-dry-time WX-152/177, the severe-storm vocabulary
/// gate WX-284/293) and the English-only cross-midnight both-days check (WX-264) were dropped and their
/// coverage moved into the reconciler generation prompt uniformly for every language (WX-331: nothing
/// privileged about English). The ten word-lists those checks consumed — day qualifiers, relative-day
/// cues, closing/aggregate precip/negation/cessation words, today/tonight/tomorrow triggers — went with
/// them.
/// </para>
///
/// <para>
/// The residual policy is part of the contract: expose <b>only UNAMBIGUOUS words</b>. A word whose local
/// meaning is genuinely ambiguous (en "tonight" = evening|overnight; es "mañana" = morning|tomorrow;
/// es "tarde" = afternoon|evening) is <b>omitted</b>, so the validator skips rather than false-rejects —
/// a wrong entry would reject legitimate prose, which is strictly worse than the safe no-op a missing
/// language already gives. A language with no plugin resolves to <c>null</c> (<see cref="LanguageLexicons.For"/>)
/// and the validator no-ops for it, exactly as before this ticket.
/// </para>
/// </summary>
internal interface ILanguageLexicon
{
    /// <summary>The ISO code this lexicon serves (e.g. "en", "es"), matched case-insensitively.</summary>
    string IsoCode { get; }

    /// <summary>UNAMBIGUOUS day-part words → part (0 pre-dawn, 1 morning, 2 afternoon, 3 evening). Consumed
    /// by the <c>{q:time}</c>&#8596;day-part agreement check (WX-149).</summary>
    IReadOnlyList<(string Word, int Part)> DayPartWords { get; }
}

/// <summary>
/// WX-168: the plugin registry. Every enabled language that can be authored/validated safely registers a
/// single <see cref="ILanguageLexicon"/> here; <see cref="For"/> resolves it by ISO code, returning
/// <c>null</c> for any language without a plugin — the <c>{q:time}</c>&#8596;day-part check then no-ops
/// for that language (its prose leans on the language-agnostic prompt rules and the QA-judge path).
/// </summary>
internal static class LanguageLexicons
{
    private static readonly IReadOnlyDictionary<string, ILanguageLexicon> ByIso =
        new ILanguageLexicon[] { new EnglishLexicon(), new SpanishLexicon() }
            .ToDictionary(l => l.IsoCode, StringComparer.OrdinalIgnoreCase);

    /// <summary>The lexicon for <paramref name="iso"/>, or <c>null</c> when no plugin exists (the validator
    /// then no-ops for that language — the safe residual, never a false reject).</summary>
    internal static ILanguageLexicon? For(string iso) =>
        ByIso.TryGetValue(iso, out var lexicon) ? lexicon : null;
}