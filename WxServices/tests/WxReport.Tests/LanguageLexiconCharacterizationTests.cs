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
// The word-lists are compared as MULTISETS — order-insensitive but duplicate-sensitive (see AssertSameWords).
// The validators consume these lists only for membership (Contains) checks, so element ORDER carries no
// behavior; pinning it would make the golden false-fail when Sub 3 builds the lists from DB rows in a
// different order than the hand-written arrays despite identical membership (WX-334 /code-review finding).
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
        AssertSameDayParts(
            new[] { ("early hours", 0), ("morning", 1), ("afternoon", 2), ("evening", 3) },
            lex.DayPartWords);

        AssertSameWords(
            new[]
            {
                "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday",
                "today", "tonight", "tomorrow", "yesterday",
            },
            lex.DayQualifiers);

        AssertSameWords(
            new[] { "today", "tonight", "tomorrow", "yesterday", "next", "following", "later" },
            lex.RelativeDayWords);

        AssertSameWords(
            new[]
            {
                "rain", "rains", "raining", "rainy", "shower", "showers", "thundershower", "thundershowers",
                "storm", "storms", "stormy", "thunderstorm", "thunderstorms", "thunder", "snow", "snows",
                "snowing", "snowy", "snowfall", "snowstorm", "snowstorms", "flurries", "flurry", "wintry",
                "sleet", "drizzle", "hail", "downpour", "downpours",
            },
            lex.ClosingPrecipWords);

        AssertSameWords(
            new[] { "no", "not", "without", "dry", "nothing", "none", "absent", "lacking" },
            lex.ClosingNegationCues);

        AssertSameWords(
            new[]
            {
                "ending", "ends", "ended", "taper", "tapers", "tapering", "tapered", "clearing", "clears",
                "cleared", "diminishing", "diminishes", "subsiding", "subsides", "departing", "exiting", "fading",
            },
            lex.ClosingCessationCues);

        AssertSameWords(new[] { "dry", "rain-free", "storm-free", "precipitation-free" }, lex.AggregateDryWords);
        AssertSameWords(new[] { "not", "unlikely", "won't", "wont" }, lex.AggregateNegationCues);
        AssertSameWords(new[] { "today" }, lex.TodayWords);
        AssertSameWords(new[] { "tonight" }, lex.TonightWords);
        AssertSameWords(new[] { "tomorrow" }, lex.TomorrowWords);
    }

    [Fact]
    public void Spanish_Lexicon_Contents_AreUnchanged()
    {
        var lex = LanguageLexicons.For("es");
        Assert.NotNull(lex);
        Assert.Equal("es", lex!.IsoCode);

        // Only the pre-dawn word is unambiguous; mañana/tarde/noche omitted (ambiguous).
        AssertSameDayParts(new[] { ("madrugada", 0) }, lex.DayPartWords);

        AssertSameWords(
            new[]
            {
                "lunes", "martes", "miércoles", "jueves", "viernes", "sábado", "domingo",
                "hoy", "ayer", "mañana",
            },
            lex.DayQualifiers);

        AssertSameWords(
            new[] { "hoy", "ayer", "mañana", "esta noche", "próximo", "siguiente", "luego", "después" },
            lex.RelativeDayWords);

        AssertSameWords(
            new[]
            {
                "lluvia", "lluvias", "lloviendo", "lluvioso", "tormenta", "tormentas", "tormentoso",
                "chubasco", "chubascos", "aguacero", "aguaceros", "llovizna", "lloviznas", "nieve",
                "nevando", "nevada", "nevadas", "aguanieve", "granizo", "truenos",
            },
            lex.ClosingPrecipWords);

        AssertSameWords(
            new[] { "no", "sin", "nada", "ninguno", "ninguna", "ningún", "seco", "seca", "despejado", "despejada" },
            lex.ClosingNegationCues);

        AssertSameWords(
            new[]
            {
                "terminando", "termina", "cesando", "cesa", "disminuyendo", "disminuye",
                "despejando", "despeja", "amainando", "amaina", "aclarando", "aclara",
            },
            lex.ClosingCessationCues);

        AssertSameWords(new[] { "seco", "seca" }, lex.AggregateDryWords);
        AssertSameWords(new[] { "no", "improbable" }, lex.AggregateNegationCues);
        AssertSameWords(new[] { "hoy" }, lex.TodayWords);
        AssertSameWords(new[] { "esta noche" }, lex.TonightWords);

        // Empty by design: es "mañana" (= tomorrow OR morning) is ambiguous, so there is no deterministic
        // es tomorrow trigger (WX-168 residual policy). The refactor must preserve this exactly.
        Assert.Empty(lex.TomorrowWords);
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
        // The two content goldens assert each member by name; this guards the SET so a member added to
        // ILanguageLexicon during the Sub 2/3 refactor can't slip past the byte-identical guard unpinned.
        // If this fails: pin the new member in BOTH English_ and Spanish_Lexicon_Contents, then bump this count.
        Assert.Equal(12, typeof(ILanguageLexicon).GetProperties().Length);
    }

    // Compare word-lists as MULTISETS — order-insensitive (the validators use them for membership only) but
    // duplicate-sensitive (a stray repeat is still a real diff). See the class remark for why order isn't pinned.
    private static void AssertSameWords(IReadOnlyList<string> expected, IReadOnlyList<string> actual) =>
        Assert.Equal(
            expected.OrderBy(w => w, StringComparer.Ordinal),
            actual.OrderBy(w => w, StringComparer.Ordinal));

    private static void AssertSameDayParts(
        IReadOnlyList<(string Word, int Part)> expected,
        IReadOnlyList<(string Word, int Part)> actual) =>
        Assert.Equal(
            expected.OrderBy(t => t.Word, StringComparer.Ordinal).ThenBy(t => t.Part),
            actual.OrderBy(t => t.Word, StringComparer.Ordinal).ThenBy(t => t.Part));
}