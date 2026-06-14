namespace WxReport.Svc;

/// <summary>
/// A day-banded send-timing schedule: an ordered set of <c>(FromDay, MinMinutes)</c>
/// steps that set a minute count as a step function of how far out — in
/// recipient-local days (1 = today) — a forecast change reaches.
/// <c>"1:90,3:180"</c> means a change touching day 1 or 2 (today / tomorrow) uses
/// 90 minutes, while one that only reaches day 3 or later uses 180.
/// <para>
/// Shared by two WxReport send-timing rate-limits, each binding it to its own config
/// key and reading the minute value as its own quantity:
/// <list type="bullet">
/// <item><see cref="ReportConfig.UpdateDebounceSchedule"/> (WX-181) — the minimum gap
/// since the last unscheduled send (post-send debounce).</item>
/// <item><see cref="ReportConfig.PreScheduledQuietSchedule"/> (WX-157) — how long before
/// the next scheduled slot an unscheduled update is held back (the quiet window).</item>
/// </list>
/// A not-severe→severe onset overrides the schedule entirely (the punch-through is
/// handled by each caller), and the service-wide 90-minute
/// <see cref="ReportConfig.MinGapMinutes"/> remains a hard floor beneath all of it.
/// </para>
/// <para>
/// Service config, so parsing is <b>strict / fail-closed</b> (a malformed schedule
/// throws at load rather than silently degrading to a wrong cadence) — unlike the
/// lenient <see cref="MetarParser.Data.ScheduledSendHoursFormat"/> runtime reader.
/// </para>
/// </summary>
public static class DayBandedSchedule
{
    /// <summary>One band: from <see cref="FromDay"/> (1-based, inclusive) onward — until the next step's day — the schedule yields <see cref="MinMinutes"/> minutes.</summary>
    public readonly record struct Step(int FromDay, int MinMinutes);

    /// <summary>
    /// Parses <c>"day:minutes,day:minutes,…"</c> into steps ordered by <see cref="Step.FromDay"/>.
    /// Throws <see cref="FormatException"/> unless the schedule is non-empty, its first day is
    /// <c>1</c> (so every day &gt;= 1 is covered), days strictly ascend, and every value is a
    /// non-negative integer. Whitespace around tokens is tolerated.
    /// </summary>
    public static IReadOnlyList<Step> Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new FormatException("Day-banded schedule is empty; expected e.g. \"1:90,3:180\".");

        // Do NOT use RemoveEmptyEntries: an empty token (stray/double/trailing comma,
        // or a bare ",") must fail strict/fail-closed as a FormatException, not be
        // silently dropped — and ',' alone would otherwise split to nothing and make
        // steps[0] below throw IndexOutOfRangeException, which the caller doesn't catch.
        var tokens = raw.Split(',', StringSplitOptions.TrimEntries);
        if (tokens.Any(static t => t.Length == 0))
            throw new FormatException("Day-banded schedule contains an empty token (stray/double/trailing comma); expected 'day:minutes,day:minutes,…'.");

        var steps = new List<Step>(tokens.Length);
        foreach (var token in tokens)
        {
            var parts = token.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var day) || !int.TryParse(parts[1], out var minutes))
                throw new FormatException($"Day-banded schedule token '{token}' is not 'day:minutes'.");
            if (day < 1)
                throw new FormatException($"Day-banded schedule day must be >= 1 (token '{token}').");
            if (minutes < 0)
                throw new FormatException($"Day-banded schedule minutes must be >= 0 (token '{token}').");
            steps.Add(new Step(day, minutes));
        }

        if (steps.Count == 0)
            throw new FormatException("Day-banded schedule is empty; expected e.g. \"1:90,3:180\".");

        if (steps[0].FromDay != 1)
            throw new FormatException($"Day-banded schedule must start at day 1; first step is day {steps[0].FromDay}.");
        for (int i = 1; i < steps.Count; i++)
            if (steps[i].FromDay <= steps[i - 1].FromDay)
                throw new FormatException($"Day-banded schedule days must strictly ascend; saw {steps[i - 1].FromDay} then {steps[i].FromDay}.");

        return steps;
    }

    /// <summary>
    /// The minute value for a change reaching recipient-local day <paramref name="day"/>
    /// (1-based): the <see cref="Step.MinMinutes"/> of the latest step whose
    /// <see cref="Step.FromDay"/> &lt;= <paramref name="day"/>. Assumes a <see cref="Parse"/>d
    /// schedule (first day 1, ascending), so any <paramref name="day"/> &gt;= 1 always
    /// matches at least the first step.
    /// </summary>
    public static int MinMinutesForDay(IReadOnlyList<Step> schedule, int day)
    {
        var minutes = schedule[0].MinMinutes;
        foreach (var step in schedule)
        {
            if (step.FromDay > day)
                break;
            minutes = step.MinMinutes;
        }
        return minutes;
    }
}