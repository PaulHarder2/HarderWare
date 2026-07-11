namespace WxReport.Svc;

/// <summary>
/// WX-168: a per-language plugin supplying the word-lists the reconciler's deterministic TIMING/CLAIM
/// prose validators key on — the <c>{q:time}</c>&#8596;day-part check (WX-149), the <c>{chN}</c>-anchored
/// timing check (WX-151), and the closing/aggregate precip-at-a-dry-time checks (WX-152 / WX-177). The
/// validators' <em>logic</em> is language-agnostic (sentence splitting, time resolution, proximity, the
/// residual policy); only these lexicons are per-language, so adding a language is one new
/// <see cref="ILanguageLexicon"/> registered in <see cref="LanguageLexicons"/> — no validator changes.
///
/// <para>
/// The residual policy is part of the contract: expose <b>only UNAMBIGUOUS words</b>. A word whose local
/// meaning is genuinely ambiguous (en "tonight" = evening|overnight; es "mañana" = morning|tomorrow;
/// es "tarde" = afternoon|evening) is <b>omitted</b>, so the validator skips rather than false-rejects —
/// a wrong entry would reject legitimate prose, which is strictly worse than the safe no-op a missing
/// language already gives. A language with no plugin resolves to <c>null</c> (<see cref="LanguageLexicons.For"/>)
/// and every validator no-ops for it, exactly as before this ticket.
/// </para>
/// </summary>
internal interface ILanguageLexicon
{
    /// <summary>The ISO code this lexicon serves (e.g. "en", "es"), matched case-insensitively.</summary>
    string IsoCode { get; }

    /// <summary>UNAMBIGUOUS day-part words → part (0 pre-dawn, 1 morning, 2 afternoon, 3 evening). Consumed
    /// by the <c>{q:time}</c>&#8596;day-part agreement check (WX-149); the parts 1–3 words also drive the
    /// time resolver's day-part buckets (the hour ranges are universal, only the words are per-language).</summary>
    IReadOnlyList<(string Word, int Part)> DayPartWords { get; }

    /// <summary>Day-qualifier words — WEEKDAY names plus the core relative-day words (today / tonight /
    /// tomorrow / yesterday) — whose presence just before a day-part word pins it to a specific day, so
    /// the resolver skips it (can't localize "Friday afternoon" against the window). Weekday names are
    /// per-language (WX-151 / the time resolver's QualifiedByOtherDay).</summary>
    IReadOnlyList<string> DayQualifiers { get; }

    /// <summary>Relative-day cues (today / tonight / tomorrow / yesterday / next / following / later) that
    /// may stand in for a calendar day the validator can't pin — their presence makes the cross-midnight
    /// both-days check skip the sentence (WX-264, conservative). No weekday names here.</summary>
    IReadOnlyList<string> RelativeDayWords { get; }

    /// <summary>Precipitation/storm phenomenon words a closing sentence could assert (WX-152).</summary>
    IReadOnlyList<string> ClosingPrecipWords { get; }

    /// <summary>Cues that make a sentence a non-assertion (negation / "dry" statement) → skip (WX-152).</summary>
    IReadOnlyList<string> ClosingNegationCues { get; }

    /// <summary>Cessation cues (precip ENDING) → the named time is a deadline, not where precip lives; skip (WX-152).</summary>
    IReadOnlyList<string> ClosingCessationCues { get; }

    /// <summary>Aggregate-period dry-claim words ("dry" / "rain-free") (WX-177 CheckAggregateDryClaim).</summary>
    IReadOnlyList<string> AggregateDryWords { get; }

    /// <summary>Cues that NEGATE a dry claim ("won't" / "unlikely") → the sentence asserts wet; skip (WX-177).</summary>
    IReadOnlyList<string> AggregateNegationCues { get; }

    /// <summary>Words resolving to "today" (the reference local day) in the time resolver.</summary>
    IReadOnlyList<string> TodayWords { get; }

    /// <summary>Words resolving to "tonight" (reference-day evening OR next-day pre-dawn) in the time resolver.</summary>
    IReadOnlyList<string> TonightWords { get; }

    /// <summary>Words resolving to "tomorrow" (the next local day) in the time resolver.</summary>
    IReadOnlyList<string> TomorrowWords { get; }
}

/// <summary>
/// WX-168: the plugin registry. Every enabled language that can be authored/validated safely registers a
/// single <see cref="ILanguageLexicon"/> here; <see cref="For"/> resolves it by ISO code, returning
/// <c>null</c> for any language without a plugin — the deterministic timing/claim validators then no-op
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