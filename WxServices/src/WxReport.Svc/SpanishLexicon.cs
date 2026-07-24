namespace WxReport.Svc;

/// <summary>
/// WX-168: the Spanish <see cref="ILanguageLexicon"/>. es is live in production (recipients today), and
/// <c>madrugada</c> is the word I can author with confidence; other live languages (eo/da/de) get their
/// own validated plugins later, gated on a native source (a wrong entry FALSE-rejects, worse than the safe
/// no-op).
///
/// <para>
/// The residual policy bites harder in es than en, and that is deliberate: <c>mañana</c> = morning OR
/// tomorrow, <c>tarde</c> = afternoon OR evening, <c>noche</c> = evening OR night — all AMBIGUOUS, so all
/// omitted from the day-part set (only the pre-dawn <c>madrugada</c> is unambiguous). So the es
/// <c>{q:time}</c>&#8596;day-part check fires only on <c>madrugada</c>; every other day-part word leans on
/// the prompt. WX-340 dropped the closing/aggregate precip and relative-day word-lists this class also
/// carried when their validators were retired in favour of the reconciler prompt.
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
}