namespace WxReport.Svc;

/// <summary>
/// WX-181 day-banded update-debounce schedule: an ordered set of
/// <c>(FromDay, MinHours)</c> steps that set the minimum gap between *unscheduled*
/// updates as a step function of how far out — in recipient-local days — the
/// change reaches. <c>"1:6,3:12"</c> means a change touching day 1 or 2 (today /
/// tomorrow) needs a 6 h gap since the last unscheduled send, while a change that
/// only reaches day 3 or later needs 12 h. A not-severe→severe change overrides the
/// schedule entirely (the punch-through is handled by the caller), and the
/// service-wide 90-minute min-gap remains a hard floor beneath all of it.
/// <para>
/// Service config, so parsing is <b>strict / fail-closed</b> (a malformed schedule
/// throws at load rather than silently degrading to a wrong cadence) — unlike the
/// lenient <see cref="MetarParser.Data.ScheduledSendHoursFormat"/> runtime reader.
/// </para>
/// </summary>
public static class UpdateDebounceScheduleFormat
{
    /// <summary>One band: from <see cref="FromDay"/> (1-based, inclusive) onward — until the next step's day — an unscheduled update needs at least <see cref="MinHours"/> since the last one.</summary>
    public readonly record struct Step(int FromDay, int MinHours);

    /// <summary>
    /// Parses <c>"day:hours,day:hours,…"</c> into steps ordered by <see cref="Step.FromDay"/>.
    /// Throws <see cref="FormatException"/> unless the schedule is non-empty, its first day is
    /// <c>1</c> (so every day &gt;= 1 is covered), days strictly ascend, and every value is a
    /// non-negative integer. Whitespace around tokens is tolerated.
    /// </summary>
    public static IReadOnlyList<Step> Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new FormatException("UpdateDebounceSchedule is empty; expected e.g. \"1:6,3:12\".");

        var steps = new List<Step>();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = token.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var day) || !int.TryParse(parts[1], out var hours))
                throw new FormatException($"UpdateDebounceSchedule token '{token}' is not 'day:hours'.");
            if (day < 1)
                throw new FormatException($"UpdateDebounceSchedule day must be >= 1 (token '{token}').");
            if (hours < 0)
                throw new FormatException($"UpdateDebounceSchedule hours must be >= 0 (token '{token}').");
            steps.Add(new Step(day, hours));
        }

        if (steps[0].FromDay != 1)
            throw new FormatException($"UpdateDebounceSchedule must start at day 1; first step is day {steps[0].FromDay}.");
        for (int i = 1; i < steps.Count; i++)
            if (steps[i].FromDay <= steps[i - 1].FromDay)
                throw new FormatException($"UpdateDebounceSchedule days must strictly ascend; saw {steps[i - 1].FromDay} then {steps[i].FromDay}.");

        return steps;
    }

    /// <summary>
    /// The minimum inter-update hours for a change reaching recipient-local day
    /// <paramref name="day"/> (1-based): the <see cref="Step.MinHours"/> of the latest step
    /// whose <see cref="Step.FromDay"/> &lt;= <paramref name="day"/>. Assumes a
    /// <see cref="Parse"/>d schedule (first day 1, ascending), so any <paramref name="day"/>
    /// &gt;= 1 always matches at least the first step.
    /// </summary>
    public static int MinHoursForDay(IReadOnlyList<Step> schedule, int day)
    {
        var hours = schedule[0].MinHours;
        foreach (var step in schedule)
        {
            if (step.FromDay > day)
                break;
            hours = step.MinHours;
        }
        return hours;
    }
}