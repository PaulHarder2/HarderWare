namespace WxReport.Svc;

/// <summary>
/// WX-168: the Spanish <see cref="ILanguageLexicon"/>. es is live in production (recipients today), and
/// these are the words I can author with confidence; other live languages (eo/da/de) get their own
/// validated plugins later, gated on a native source (a wrong entry FALSE-rejects, worse than the safe
/// no-op).
///
/// <para>
/// The residual policy bites harder in es than en, and that is deliberate: <c>mañana</c> = morning OR
/// tomorrow, <c>tarde</c> = afternoon OR evening, <c>noche</c> = evening OR night — all AMBIGUOUS, so all
/// omitted from the day-part set (only the pre-dawn <c>madrugada</c> is unambiguous). And because the one
/// es word for "tomorrow" (<c>mañana</c>) is the same ambiguous word, es has <b>no deterministic tomorrow
/// trigger</b> — <see cref="TomorrowWords"/> is empty. So the es time resolver fires only on the
/// unambiguous <c>hoy</c> / <c>esta noche</c> / <c>madrugada</c>; the rest leans on the prompt. The real es
/// coverage this adds is the closing / aggregate precip-at-a-dry-time lexicons below.
/// </para>
/// </summary>
internal sealed class SpanishLexicon : ILanguageLexicon
{
    public string IsoCode => "es";

    // Only the pre-dawn word is unambiguous. mañana/tarde/noche are omitted (ambiguous → would false-reject).
    public IReadOnlyList<(string Word, int Part)> DayPartWords { get; } = new[]
    {
        ("madrugada", 0),
    };

    // Weekday names (es) + the clear relative-day words. Their presence just before a day-part pins it to
    // another day → skip (conservative; an over-inclusive qualifier only skips, never false-rejects).
    public IReadOnlyList<string> DayQualifiers { get; } = new[]
    {
        "lunes", "martes", "miércoles", "jueves", "viernes", "sábado", "domingo",
        "hoy", "ayer", "mañana",
    };

    public IReadOnlyList<string> RelativeDayWords { get; } = new[]
    {
        "hoy", "ayer", "mañana", "esta noche", "próximo", "siguiente", "luego", "después",
    };

    public IReadOnlyList<string> ClosingPrecipWords { get; } = new[]
    {
        "lluvia", "lluvias", "lloviendo", "lluvioso", "tormenta", "tormentas", "tormentoso",
        "chubasco", "chubascos", "aguacero", "aguaceros", "llovizna", "lloviznas", "nieve",
        "nevando", "nevada", "nevadas", "aguanieve", "granizo", "truenos",
    };

    public IReadOnlyList<string> ClosingNegationCues { get; } = new[]
    {
        "no", "sin", "nada", "ninguno", "ninguna", "ningún", "seco", "seca", "despejado", "despejada",
    };

    public IReadOnlyList<string> ClosingCessationCues { get; } = new[]
    {
        "terminando", "termina", "cesando", "cesa", "disminuyendo", "disminuye",
        "despejando", "despeja", "amainando", "amaina", "aclarando", "aclara",
    };

    public IReadOnlyList<string> AggregateDryWords { get; } = new[] { "seco", "seca" };

    public IReadOnlyList<string> AggregateNegationCues { get; } = new[] { "no", "improbable" };

    public IReadOnlyList<string> TodayWords { get; } = new[] { "hoy" };

    public IReadOnlyList<string> TonightWords { get; } = new[] { "esta noche" };

    // Empty by design: es "mañana" (= tomorrow OR morning) is ambiguous, so there is no deterministic
    // es tomorrow trigger — that resolution leans on the prompt rule.
    public IReadOnlyList<string> TomorrowWords { get; } = System.Array.Empty<string>();
}