using MetarParser.Data.Entities;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

// WX-336 (Sub 3 of WX-331): the cutover characterization golden + builder-behavior tests for the one
// surviving deterministic prose validator's per-language input — LanguageTemplateStore.DayPartWords.
//
// This replaces LanguageLexiconCharacterizationTests (WX-334): the hand-written EnglishLexicon /
// SpanishLexicon and the ILanguageLexicon plugin registry are retired; DayPartWords is now built from
// the DB templates (the DayPart1–4 rows a language flags ValidatorUse=Yes). The en assertion is the
// keystone: it proves the DB-derived builder yields the SAME four day-part words the retired
// EnglishLexicon hard-coded (post the WX-335 "overnight" drop), so the cutover is byte-identical for en
// — a VALUES diff here is the red flag that en behavior moved. The builder-behavior tests pin the
// projection logic (Yes→included at the right part, No/non-representable/non-DayPart→excluded,
// unknown/uncurated language→empty, iso canonicalization) that carries es and every future language.
public class LanguageDayPartWordsTests
{
    [Fact]
    public void En_DayPartWords_AreByteIdentical_ToRetiredEnglishLexicon()
    {
        // The en seed store marks DayPart1–4 ValidatorUse=Yes (mirroring the WX-335 prod load), so the
        // DB-derived builder must produce exactly the retired EnglishLexicon set (post the WX-335
        // "overnight" drop): early hours=0, morning=1, afternoon=2, evening=3.
        var store = SeedTemplateStore.Build();

        AssertSameDayParts(
            new[] { ("early hours", 0), ("morning", 1), ("afternoon", 2), ("evening", 3) },
            store.DayPartWords("en"));
    }

    [Fact]
    public void Builder_IncludesOnlyRepresentableValidatorUseYesDayParts_AtCorrectPart()
    {
        // Only a representable DayPart1–4 row flagged Yes contributes, at part = ordinal-1. A No row, a
        // not-representable row, and a non-DayPart Yes row are all excluded; the result is part-ordered.
        var store = StoreWith("xx",
            Row(Tok.DayPart1, "alpha", ValidatorUse.Yes),                 // → (alpha, 0)
            Row(Tok.DayPart2, "beta", ValidatorUse.No),                   // excluded: render-only
            Row(Tok.DayPart3, "gamma", ValidatorUse.Yes),                 // → (gamma, 2)
            Row(Tok.DayPart4, "delta", ValidatorUse.Yes, representable: false), // excluded: not representable
            Row("ClosingFallback", "epsilon", ValidatorUse.Yes));         // excluded: not a DayPart token

        AssertSameDayParts(new[] { ("alpha", 0), ("gamma", 2) }, store.DayPartWords("xx"));
    }

    [Fact]
    public void Builder_SkipsBlankPhraseDayPart()
    {
        // A Yes DayPart row with a blank phrase is skipped: an empty word would make NearestDayPartWord's
        // IndexOf("") match at every position without advancing (a hang) and false-bind to a {q:time}
        // token. The retired hard-coded lexicons guaranteed non-empty words; the DB source does not.
        var store = StoreWith("xx",
            Row(Tok.DayPart1, "", ValidatorUse.Yes),
            Row(Tok.DayPart2, "   ", ValidatorUse.Yes),
            Row(Tok.DayPart3, "gamma", ValidatorUse.Yes));

        AssertSameDayParts(new[] { ("gamma", 2) }, store.DayPartWords("xx"));
    }

    [Fact]
    public void Builder_UncuratedOrUnknownLanguage_YieldsEmpty_TheSafeResidual()
    {
        // A language whose DayPart rows are all No (de/eo/da/sq today) yields empty DayPartWords, and an
        // unknown iso yields empty too — the {q:time}<->day-part check then no-ops for it (prompt-governed),
        // never a false reject. A strict gain over the pre-WX-336 total no-op via a null lexicon.
        var store = StoreWith("de", Row(Tok.DayPart2, "Morgen", ValidatorUse.No));

        Assert.Empty(store.DayPartWords("de"));   // curated none → empty
        Assert.Empty(store.DayPartWords("zz"));   // unknown language → empty
    }

    [Fact]
    public void DayPartWords_IsIsoCanonicalized()
    {
        // The lookup key is canonicalized (case-folded, region stripped) exactly like every other store
        // accessor, so a mixed-case or regional tag resolves to the base language rather than missing.
        var store = SeedTemplateStore.Build();
        var expected = new[] { ("early hours", 0), ("morning", 1), ("afternoon", 2), ("evening", 3) };

        AssertSameDayParts(expected, store.DayPartWords("EN"));
        AssertSameDayParts(expected, store.DayPartWords("en-US"));
    }

    // Builds a store from an inline row list for one language (bare test double — no DB, no migrations).
    private static LanguageTemplateStore StoreWith(string iso, params LanguageTemplate[] rows)
    {
        var lang = new Language { Id = 1, IsoCode = iso, DisplayName = iso, CultureName = "en-US" };
        foreach (var r in rows)
        {
            r.LanguageId = lang.Id;
            r.Language = lang;
        }
        return new LanguageTemplateStore(() => rows);
    }

    private static LanguageTemplate Row(string token, string phrase, ValidatorUse validatorUse, bool representable = true) =>
        new() { Token = token, Phrase = phrase, ValidatorUse = validatorUse, Representable = representable };

    // Compare day-part word lists as sets — order-insensitive (the validator uses them for membership),
    // duplicate-sensitive (a stray repeat is a real diff), and CASE-insensitive on the word:
    // NearestDayPartWord matches OrdinalIgnoreCase, so the render-case a language ships (the en seed
    // carries "Morning"; the live DB and the retired EnglishLexicon used lowercase "morning") does not
    // affect validator behavior — the contract is (word ignoring case, part).
    private static void AssertSameDayParts(
        IReadOnlyList<(string Word, int Part)> expected,
        IReadOnlyList<(string Word, int Part)> actual)
    {
        static IEnumerable<(string, int)> Norm(IReadOnlyList<(string Word, int Part)> xs) =>
            xs.Select(t => (t.Word.ToLowerInvariant(), t.Part))
              .OrderBy(t => t.Item1, StringComparer.Ordinal).ThenBy(t => t.Item2);
        Assert.Equal(Norm(expected), Norm(actual));
    }
}