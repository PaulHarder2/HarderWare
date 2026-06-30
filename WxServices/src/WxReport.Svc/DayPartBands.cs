namespace WxReport.Svc;

/// <summary>
/// Shared day-part band gating for a local calendar day's temperature extremes (WX-230, WX-234).
/// A daily high and a daily low each live in a specific 6-hour band, so a <em>partial</em> day
/// (one whose forecast horizon starts or ends mid-day) can hold one extreme but not the other.
/// These predicates are the single source of truth for "does this day have a genuine high / low",
/// shared by the temperature summary (<see cref="TemperatureRangeSummarizer.DailyHighsLows"/>) and
/// the Extended Forecast grid (<see cref="StructuredReportRenderer"/>) so the two cannot drift —
/// the same role <see cref="SevereBlocks.NotFullyElapsed"/> plays for the elapsed-block trim.
/// <para>
/// Blocks are local-aligned to 00/06/12/18 (WX-155). The gating keys off a block's local START hour:
/// <list type="bullet">
/// <item>the daily <b>maximum</b> sits in the 12:00–18:00 (peak-heating) band, so a day has a genuine
///   high only if it has THAT band (<see cref="HasAfternoon"/>). A trailing day whose horizon ends
///   before its afternoon has only a pre-dawn/morning block, whose max is not a daytime high.</item>
/// <item>the daily <b>minimum</b> sits at ~dawn (06:00), in the morning half of the day, so a day has
///   a genuine overnight low only if it has a band starting before the afternoon
///   (<see cref="HasDawnWindow"/> — the pre-dawn 00:00 or morning 06:00 band). A leading day (today,
///   sent past the morning) whose earliest block is the afternoon has only daytime minima, not an
///   overnight low — the mirror of the same defect. Using "before the afternoon" rather than exactly
///   {0,6} is also robust to a DST-stepped pre-dawn boundary that lands at, e.g., local 01:00.</item>
/// </list>
/// At high latitudes the minimum can drift off dawn — a small, accepted error.
/// </para>
/// </summary>
internal static class DayPartBands
{
    /// <summary>Local start hour of the afternoon (peak-heating) 6-hour band that holds the daily maximum.</summary>
    internal const int AfternoonBandLocalHour = 12;

    /// <summary>True when a block whose local start hour is <paramref name="localHour"/> is the afternoon (peak-heating) band — a day needs one to have a genuine daily high.</summary>
    internal static bool HasAfternoon(int localHour) => localHour == AfternoonBandLocalHour;

    /// <summary>True when a block whose local start hour is <paramref name="localHour"/> is in the morning half that brackets the dawn minimum (pre-dawn 00:00 or morning 06:00) — a day needs one to have a genuine overnight low.</summary>
    internal static bool HasDawnWindow(int localHour) => localHour < AfternoonBandLocalHour;
}