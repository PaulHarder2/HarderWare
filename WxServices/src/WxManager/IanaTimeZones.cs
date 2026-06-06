namespace WxManager;

/// <summary>
/// Shared IANA-timezone list for WxManager's editor ComboBoxes and timezone
/// validation (Recipients and Localities tabs). Extracted in WX-127 when the
/// Localities tab became the second consumer. The system timezone set is fixed
/// for the process lifetime, so both views are built once and cached.
/// </summary>
internal static class IanaTimeZones
{
    private static readonly Lazy<IReadOnlyList<string>> _all = new(() => Build().AsReadOnly());
    private static readonly Lazy<HashSet<string>> _set =
        new(() => new HashSet<string>(_all.Value, StringComparer.Ordinal));

    /// <summary>
    /// Sorted list of canonical IANA timezone IDs (each Windows timezone from
    /// <see cref="TimeZoneInfo.GetSystemTimeZones"/> converted to its IANA
    /// equivalent; "UTC" always included). Cached and read-only-wrapped —
    /// callers may bind it directly but cannot mutate the shared instance.
    /// </summary>
    public static IReadOnlyList<string> All() => _all.Value;

    /// <summary>
    /// Whether <paramref name="tz"/> is a recognized IANA timezone ID
    /// (ordinal comparison, matching the list <see cref="All"/> returns).
    /// </summary>
    public static bool IsValid(string tz) => _set.Value.Contains(tz);

    private static List<string> Build()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { "UTC" };
        foreach (var tz in TimeZoneInfo.GetSystemTimeZones())
        {
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(tz.Id, out var ianaId) && ianaId is not null)
                ids.Add(ianaId);
        }
        return ids.OrderBy(id => id, StringComparer.Ordinal).ToList();
    }
}