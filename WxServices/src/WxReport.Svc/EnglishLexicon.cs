namespace WxReport.Svc;

/// <summary>
/// WX-168: the English <see cref="ILanguageLexicon"/> — the <see cref="DayPartWords"/> the surviving
/// <c>{q:time}</c>&#8596;day-part check (WX-149) keys on, extracted verbatim from <c>ForecastReconciler</c>
/// (behavior-preserving for en). WX-340 dropped the ten free-prose word-lists this class also carried when
/// their validators were retired in favour of the reconciler prompt.
/// </summary>
internal sealed class EnglishLexicon : ILanguageLexicon
{
    public string IsoCode => "en";

    // UNAMBIGUOUS day-part words only (parts: 0 pre-dawn, 1 morning, 2 afternoon, 3 evening). English
    // "tonight"/"night" (evening OR overnight) is deliberately absent — ambiguous, so it would false-reject.
    // "overnight" is likewise absent (WX-335): "Monday overnight" means TUESDAY's 00-06 (the night after
    // Monday), not Monday's own block, so it doesn't name a part of the stated day — a day-shift ambiguity.
    public IReadOnlyList<(string Word, int Part)> DayPartWords { get; } = new[]
    {
        ("early hours", 0),   // WX-264: DayPart1's approved English word for the 00-06 block
        ("morning", 1),
        ("afternoon", 2),
        ("evening", 3),
    };
}