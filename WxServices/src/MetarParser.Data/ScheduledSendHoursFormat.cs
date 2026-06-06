namespace MetarParser.Data;

/// <summary>
/// The single home of the comma-separated scheduled-send-hours format shared by
/// <c>Recipient.ScheduledSendHours</c> and <c>Locality.ScheduledSendHours</c>
/// (e.g. <c>"7"</c> or <c>"6, 12"</c>, hours 0–23). Extracted in WX-127 — the
/// contract previously lived independently in WxReport.Svc's runtime parser and
/// WxManager's Recipients-tab validation loop, and the Localities tab would have
/// been a third copy.
/// </summary>
/// <remarks>
/// Two entry points with deliberately different strictness:
/// <see cref="Parse"/> is the lenient runtime reader (invalid tokens are silently
/// dropped, preserving the long-standing WxReport.Svc behavior), while
/// <see cref="TryValidate"/> is the strict UI gate that rejects any malformed
/// token so bad values never reach the database in the first place.
/// </remarks>
public static class ScheduledSendHoursFormat
{
    /// <summary>
    /// Parses a comma-separated hours string into a sorted list of valid hour
    /// values (0–23). Entries that cannot be parsed or are out of range are
    /// silently ignored; a null/blank input yields an empty list.
    /// </summary>
    /// <param name="raw">The raw comma-separated string, or <see langword="null"/>.</param>
    /// <returns>Sorted valid hours; empty when none parse.</returns>
    public static IReadOnlyList<int> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Select(s => int.TryParse(s, out var h) ? (int?)h : null)
                  .Where(h => h is >= 0 and <= 23)
                  .Select(h => h!.Value)
                  .OrderBy(h => h)
                  .ToList();
    }

    /// <summary>
    /// Strictly validates a comma-separated hours string: every token must be an
    /// integer 0–23. A null/blank input is valid (it means "fall back to the
    /// service default"). On failure, <paramref name="invalidToken"/> carries the
    /// first offending token for the UI's error message.
    /// </summary>
    /// <param name="raw">The raw comma-separated string, or <see langword="null"/>.</param>
    /// <param name="invalidToken">The first token that failed validation, or <see langword="null"/> when valid.</param>
    /// <returns><see langword="true"/> when every token is a valid hour (or the input is blank).</returns>
    public static bool TryValidate(string? raw, out string? invalidToken)
    {
        invalidToken = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;

        foreach (var token in raw.Split(','))
        {
            var t = token.Trim();
            if (!int.TryParse(t, out var h) || h is < 0 or > 23)
            {
                invalidToken = t;
                return false;
            }
        }
        return true;
    }
}