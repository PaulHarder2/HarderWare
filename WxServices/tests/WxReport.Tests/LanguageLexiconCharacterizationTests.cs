using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

// WX-334 (Sub 1 of WX-331): characterization golden pinning the CURRENT en/es lexicon contents, member by
// member, against the hand-written EnglishLexicon / SpanishLexicon. Written BEFORE the WX-331 refactor and
// must remain UNCHANGED through Sub 3's cutover: once LanguageLexicons.For builds the lexicon from the DB +
// CultureInfo, these same assertions prove en/es come out byte-identical. A diff to this file during the
// refactor is a red flag that en/es behavior moved. Reaches the internal lexicon types via
// InternalsVisibleTo("WxReport.Tests") (declared in WxReport.Svc.csproj).
public class LanguageLexiconCharacterizationTests
{
    [Fact]
    public void English_Lexicon_Contents_AreUnchanged()
    {
        var lex = LanguageLexicons.For("en");
        Assert.NotNull(lex);
        Assert.Equal("en", lex!.IsoCode);

        // UNAMBIGUOUS day-part words only; "tonight"/"night" deliberately absent (ambiguous).
        Assert.Equal(
            new[] { ("overnight", 0), ("early hours", 0), ("morning", 1), ("afternoon", 2), ("evening", 3) },
            lex.DayPartWords);

        Assert.Equal(
            new[]
            {
                "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday",
                "today", "tonight", "tomorrow", "yesterday",
            },
            lex.DayQualifiers);

        Assert.Equal(
            new[] { "today", "tonight", "tomorrow", "yesterday", "next", "following", "later" },
            lex.RelativeDayWords);

        Assert.Equal(
            new[]
            {
                "rain", "rains", "raining", "rainy", "shower", "showers", "thundershower", "thundershowers",
                "storm", "storms", "stormy", "thunderstorm", "thunderstorms", "thunder", "snow", "snows",
                "snowing", "snowy", "snowfall", "snowstorm", "snowstorms", "flurries", "flurry", "wintry",
                "sleet", "drizzle", "hail", "downpour", "downpours",
            },
            lex.ClosingPrecipWords);

        Assert.Equal(
            new[] { "no", "not", "without", "dry", "nothing", "none", "absent", "lacking" },
            lex.ClosingNegationCues);

        Assert.Equal(
            new[]
            {
                "ending", "ends", "ended", "taper", "tapers", "tapering", "tapered", "clearing", "clears",
                "cleared", "diminishing", "diminishes", "subsiding", "subsides", "departing", "exiting", "fading",
            },
            lex.ClosingCessationCues);

        Assert.Equal(new[] { "dry", "rain-free", "storm-free", "precipitation-free" }, lex.AggregateDryWords);
        Assert.Equal(new[] { "not", "unlikely", "won't", "wont" }, lex.AggregateNegationCues);
        Assert.Equal(new[] { "today" }, lex.TodayWords);
        Assert.Equal(new[] { "tonight" }, lex.TonightWords);
        Assert.Equal(new[] { "tomorrow" }, lex.TomorrowWords);
    }

    [Fact]
    public void Spanish_Lexicon_Contents_AreUnchanged()
    {
        var lex = LanguageLexicons.For("es");
        Assert.NotNull(lex);
        Assert.Equal("es", lex!.IsoCode);

        // Only the pre-dawn word is unambiguous; mañana/tarde/noche omitted (ambiguous).
        Assert.Equal(new[] { ("madrugada", 0) }, lex.DayPartWords);

        Assert.Equal(
            new[]
            {
                "lunes", "martes", "miércoles", "jueves", "viernes", "sábado", "domingo",
                "hoy", "ayer", "mañana",
            },
            lex.DayQualifiers);

        Assert.Equal(
            new[] { "hoy", "ayer", "mañana", "esta noche", "próximo", "siguiente", "luego", "después" },
            lex.RelativeDayWords);

        Assert.Equal(
            new[]
            {
                "lluvia", "lluvias", "lloviendo", "lluvioso", "tormenta", "tormentas", "tormentoso",
                "chubasco", "chubascos", "aguacero", "aguaceros", "llovizna", "lloviznas", "nieve",
                "nevando", "nevada", "nevadas", "aguanieve", "granizo", "truenos",
            },
            lex.ClosingPrecipWords);

        Assert.Equal(
            new[] { "no", "sin", "nada", "ninguno", "ninguna", "ningún", "seco", "seca", "despejado", "despejada" },
            lex.ClosingNegationCues);

        Assert.Equal(
            new[]
            {
                "terminando", "termina", "cesando", "cesa", "disminuyendo", "disminuye",
                "despejando", "despeja", "amainando", "amaina", "aclarando", "aclara",
            },
            lex.ClosingCessationCues);

        Assert.Equal(new[] { "seco", "seca" }, lex.AggregateDryWords);
        Assert.Equal(new[] { "no", "improbable" }, lex.AggregateNegationCues);
        Assert.Equal(new[] { "hoy" }, lex.TodayWords);
        Assert.Equal(new[] { "esta noche" }, lex.TonightWords);

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
        // The factory keys iso case-insensitively (StringComparer.OrdinalIgnoreCase). Pin it so the refactor
        // keeps the same resolution semantics.
        Assert.NotNull(LanguageLexicons.For("EN"));
        Assert.NotNull(LanguageLexicons.For("Es"));
    }
}