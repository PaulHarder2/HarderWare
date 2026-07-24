using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

// WX-334 (Sub 1 of WX-331): characterization golden pinning the CURRENT en/es lexicon contents, member by
// member, against the hand-written EnglishLexicon / SpanishLexicon. Written BEFORE the WX-331 refactor and
// must stay green through Sub 3's cutover: once LanguageLexicons.For builds the lexicon from the DB +
// CultureInfo, these same assertions prove en/es come out equivalent. A VALUES diff here during the refactor
// is the red flag that en/es behavior moved. Reaches the internal lexicon types via
// InternalsVisibleTo("WxReport.Tests") (declared in WxReport.Svc.csproj).
//
// DayPartWords is compared as an order-insensitive set (see AssertSameDayParts) — the validator consumes it
// only for membership, so element ORDER carries no behavior; pinning it would false-fail when a later DB-built
// lexicon returns the pairs in a different order despite identical membership (WX-334 /code-review finding).
public class LanguageLexiconCharacterizationTests
{
    [Fact]
    public void English_Lexicon_Contents_AreUnchanged()
    {
        var lex = LanguageLexicons.For("en");
        Assert.NotNull(lex);
        Assert.Equal("en", lex!.IsoCode);

        // UNAMBIGUOUS day-part words only; "tonight"/"night" and "overnight" deliberately absent (WX-335:
        // "Monday overnight" = Tuesday's 00-06, a day-shift ambiguity that doesn't name the stated day's part).
        // WX-340 reduced the contract to IsoCode + DayPartWords — the ten free-prose word-lists went with the
        // validators they fed, so only the surviving {q:time}<->day-part check's lexicon is pinned here.
        AssertSameDayParts(
            new[] { ("early hours", 0), ("morning", 1), ("afternoon", 2), ("evening", 3) },
            lex.DayPartWords);
    }

    [Fact]
    public void Spanish_Lexicon_Contents_AreUnchanged()
    {
        var lex = LanguageLexicons.For("es");
        Assert.NotNull(lex);
        Assert.Equal("es", lex!.IsoCode);

        // Only the pre-dawn word is unambiguous; mañana/tarde/noche omitted (ambiguous). WX-340 reduced the
        // contract to IsoCode + DayPartWords (see the English golden's note).
        AssertSameDayParts(new[] { ("madrugada", 0) }, lex.DayPartWords);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("eo")]
    [InlineData("da")]
    public void UnpluggedLanguage_ResolvesToNull_TheSafeResidual(string iso)
    {
        // The refactor must preserve this: a language with no plugin → null → every deterministic validator
        // no-ops for it (never a false reject). de/eo/da are live in production today with exactly this state.
        Assert.Null(LanguageLexicons.For(iso));
    }

    [Fact]
    public void For_IsCaseInsensitive()
    {
        // The factory keys iso case-insensitively (StringComparer.OrdinalIgnoreCase). Pin BOTH that it
        // resolves AND that it resolves to the CORRECT lexicon, so a case-fold bug returning the wrong
        // language is caught, not just a null.
        Assert.Equal("en", LanguageLexicons.For("EN")?.IsoCode);
        Assert.Equal("es", LanguageLexicons.For("Es")?.IsoCode);
    }

    [Fact]
    public void Interface_MemberCount_IsPinned_SoNewMembersCantSlipUnpinned()
    {
        // The two content goldens assert each member by name; this guards the SET so a new member added to
        // ILanguageLexicon can't slip past the byte-identical guard unpinned. WX-340 reduced the contract to
        // IsoCode + DayPartWords (the ten free-prose word-lists retired with their validators).
        // If this fails: pin the new member in BOTH English_ and Spanish_Lexicon_Contents, then bump this count.
        Assert.Equal(2, typeof(ILanguageLexicon).GetProperties().Length);
    }

    private static void AssertSameDayParts(
        IReadOnlyList<(string Word, int Part)> expected,
        IReadOnlyList<(string Word, int Part)> actual) =>
        Assert.Equal(
            expected.OrderBy(t => t.Word, StringComparer.Ordinal).ThenBy(t => t.Part),
            actual.OrderBy(t => t.Word, StringComparer.Ordinal).ThenBy(t => t.Part));
}