namespace WxReport.Svc;

/// <summary>
/// WX-168: the English <see cref="ILanguageLexicon"/> — the word-lists the timing/claim validators used
/// (hard-coded in <c>ForecastReconciler</c>) before they were made language-pluggable. Extracting them
/// here verbatim is behavior-preserving for en. The one Spanish word the old shared list carried
/// (<c>madrugada</c>) moves to <see cref="SpanishLexicon"/>: en prose never contains it, so removing it
/// from the en set changes nothing for en, and it belongs with the language whose prose can use it.
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

    public IReadOnlyList<string> DayQualifiers { get; } = new[]
    {
        "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday",
        "today", "tonight", "tomorrow", "yesterday",
    };

    public IReadOnlyList<string> RelativeDayWords { get; } = new[]
    {
        "today", "tonight", "tomorrow", "yesterday", "next", "following", "later",
    };

    public IReadOnlyList<string> ClosingPrecipWords { get; } = new[]
    {
        "rain", "rains", "raining", "rainy", "shower", "showers", "thundershower", "thundershowers",
        "storm", "storms", "stormy", "thunderstorm", "thunderstorms", "thunder", "snow", "snows",
        "snowing", "snowy", "snowfall", "snowstorm", "snowstorms", "flurries", "flurry", "wintry",
        "sleet", "drizzle", "hail", "downpour", "downpours",
    };

    public IReadOnlyList<string> ClosingNegationCues { get; } = new[]
    {
        "no", "not", "without", "dry", "nothing", "none", "absent", "lacking",
    };

    public IReadOnlyList<string> ClosingCessationCues { get; } = new[]
    {
        "ending", "ends", "ended", "taper", "tapers", "tapering", "tapered", "clearing", "clears",
        "cleared", "diminishing", "diminishes", "subsiding", "subsides", "departing", "exiting", "fading",
    };

    public IReadOnlyList<string> AggregateDryWords { get; } = new[] { "dry", "rain-free", "storm-free", "precipitation-free" };

    public IReadOnlyList<string> AggregateNegationCues { get; } = new[] { "not", "unlikely", "won't", "wont" };

    public IReadOnlyList<string> TodayWords { get; } = new[] { "today" };

    public IReadOnlyList<string> TonightWords { get; } = new[] { "tonight" };

    public IReadOnlyList<string> TomorrowWords { get; } = new[] { "tomorrow" };
}