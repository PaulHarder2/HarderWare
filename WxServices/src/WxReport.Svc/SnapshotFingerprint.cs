using WxInterp;

namespace WxReport.Svc;

/// <summary>
/// Severity of a weather change, used to decide whether to send an unscheduled
/// report and what tone and subject line to use.
/// </summary>
public enum ChangeSeverity
{
    /// <summary>No change detected, or not applicable (scheduled / first send).</summary>
    None,

    /// <summary>
    /// Only low-signal forecast fields changed (e.g. a GFS risk flag cleared).
    /// The send is suppressed — the change is not worth an unscheduled email.
    /// </summary>
    Minor,

    /// <summary>
    /// A meaningful but non-dangerous change occurred (conditions cleared, forecast
    /// risk appeared, or a significant temperature shift was detected).
    /// Subject line: "Weather update"; Claude opens with a brief change summary.
    /// </summary>
    Update,

    /// <summary>
    /// A dangerous current-conditions change occurred (thunderstorm, low visibility,
    /// or high winds appeared).
    /// Subject line: "Weather alert"; Claude opens with an urgent notice.
    /// </summary>
    Alert,
}

/// <summary>
/// Produces a compact string that captures only the conditions relevant to
/// significant-change detection.  Two snapshots with the same fingerprint
/// are considered "not significantly different" for alert-sending purposes.
/// </summary>
public static class SnapshotFingerprint
{
    /// <summary>
    /// Computes a fingerprint for <paramref name="snap"/> using the thresholds
    /// in <paramref name="cfg"/>.  The returned string changes when:
    /// <list type="bullet">
    ///   <item>Wind speed crosses the configured knot threshold.</item>
    ///   <item>Visibility crosses the configured statute-mile threshold.</item>
    ///   <item>A thunderstorm phenomenon appears or disappears.</item>
    ///   <item>Any precipitation phenomenon appears or disappears.</item>
    ///   <item>The GFS forecast high for the next calendar day shifts by at least <see cref="SignificantChangeConfig.ForecastHighChangeDegF"/> degrees.</item>
    ///   <item>The GFS forecast low for the next calendar day shifts by at least <see cref="SignificantChangeConfig.ForecastLowChangeDegF"/> degrees.</item>
    ///   <item>Any GFS forecast day crosses the <see cref="SignificantChangeConfig.CapeThresholdJKg"/> thunderstorm-potential threshold.</item>
    ///   <item>Any GFS forecast day crosses the <see cref="SignificantChangeConfig.GfsPrecipThresholdMmHr"/> precipitation threshold.</item>
    /// </list>
    /// </summary>
    /// <param name="snap">The weather snapshot to fingerprint.</param>
    /// <param name="cfg">Thresholds that define what constitutes a "significant" condition for each dimension.</param>
    /// <returns>
    /// A compact pipe-delimited string encoding eight fields, e.g.
    /// <c>"W:False|V:False|TS:False|PR:True|GH:5|GL:3|GC:False|GP:True"</c>.
    /// The <c>GH</c> field is a bucket index derived by dividing the forecast high
    /// by <see cref="SignificantChangeConfig.ForecastHighChangeDegF"/>; it is <c>?</c>
    /// when no GFS data is available.
    /// The <c>GL</c> field is a bucket index derived by dividing the forecast low
    /// by <see cref="SignificantChangeConfig.ForecastLowChangeDegF"/>; it is <c>?</c>
    /// when no GFS data is available.
    /// </returns>
    public static string Compute(WeatherSnapshot snap, SignificantChangeConfig cfg)
    {
        var windKt   = snap.WindSpeedKt ?? 0;
        var windHigh = windKt >= cfg.WindThresholdKt;

        var visSm  = snap.Cavok ? 99.0 : (snap.VisibilityStatuteMiles ?? 99.0);
        var visLow = visSm < cfg.VisibilityThresholdSm;

        // Current (non-recent) thunderstorm
        var hasTs = snap.WeatherPhenomena.Any(w =>
            w.Descriptor == WeatherDescriptor.Thunderstorm && !w.IsRecent);

        // Current (non-recent) precipitation
        var hasPrecip = snap.WeatherPhenomena.Any(w =>
            w.Precipitation.Count > 0 && !w.IsRecent);

        // GFS: bucket the next calendar day's forecast high/low to their respective
        // change-degree resolutions.  A change in bucket means the temp shifted by
        // at least that many degrees.
        var gfsDays   = snap.GfsForecast?.Days;
        var today     = DateOnly.FromDateTime(DateTime.UtcNow);
        var nextDay   = gfsDays?.FirstOrDefault(d => d.Date > today) ?? gfsDays?.FirstOrDefault();
        var highBucket = nextDay?.HighTempF.HasValue == true
            ? (int?)Math.Floor(nextDay.HighTempF!.Value / cfg.ForecastHighChangeDegF)
            : null;
        var lowBucket  = nextDay?.LowTempF.HasValue == true
            ? (int?)Math.Floor(nextDay.LowTempF!.Value / cfg.ForecastLowChangeDegF)
            : null;

        // GFS: CAPE risk — any forecast day exceeds the thunderstorm-potential threshold.
        var gfsCapeRisk = gfsDays?.Any(d => d.MaxCapeJKg >= cfg.CapeThresholdJKg) ?? false;

        // GFS: precip risk — any forecast day exceeds the precipitation-rate threshold.
        var gfsPrecipRisk = gfsDays?.Any(d => d.MaxPrecipRateMmHr >= cfg.GfsPrecipThresholdMmHr) ?? false;

        return $"W:{windHigh}|V:{visLow}|TS:{hasTs}|PR:{hasPrecip}" +
               $"|GH:{highBucket?.ToString() ?? "?"}|GL:{lowBucket?.ToString() ?? "?"}|GC:{gfsCapeRisk}|GP:{gfsPrecipRisk}";
    }

    /// <summary>
    /// Classifies the severity of the change between two fingerprints to determine
    /// whether an unscheduled report should be sent and what tone it should use.
    /// </summary>
    /// <remarks>
    /// Classification rules (evaluated in priority order):
    /// <list type="bullet">
    ///   <item><b>Alert</b> — a dangerous current-conditions flag (<c>W</c>, <c>V</c>, or <c>TS</c>) became <c>True</c> (conditions worsened past threshold).</item>
    ///   <item><b>Update</b> — observed precipitation appeared (<c>PR</c> became <c>True</c>); a GFS risk flag (<c>GC</c> or <c>GP</c>) appeared; or a temperature bucket (<c>GH</c> or <c>GL</c>) shifted in either direction.</item>
    ///   <item><b>Minor</b> — only "conditions improved" transitions occurred: observed flags (<c>W</c>, <c>V</c>, <c>TS</c>, <c>PR</c>) cleared, or GFS risk flags (<c>GC</c>/<c>GP</c>) cleared.  Send suppressed.</item>
    /// </list>
    /// If either fingerprint cannot be parsed, <see cref="ChangeSeverity.Update"/> is returned as a safe default.
    /// </remarks>
    /// <param name="oldFp">Fingerprint from the previous send.</param>
    /// <param name="newFp">Fingerprint computed from the current snapshot.</param>
    /// <returns>The <see cref="ChangeSeverity"/> describing the nature of the change.</returns>
    public static ChangeSeverity ClassifyChange(string oldFp, string newFp)
    {
        var oldP = ParseFields(oldFp);
        var newP = ParseFields(newFp);

        if (oldP.Count == 0 || newP.Count == 0)
            return ChangeSeverity.Update;

        // Alert: a dangerous current-condition appeared.
        if (BecameTrue(oldP, newP, "W"))  return ChangeSeverity.Alert;
        if (BecameTrue(oldP, newP, "V"))  return ChangeSeverity.Alert;
        if (BecameTrue(oldP, newP, "TS")) return ChangeSeverity.Alert;

        // Update: precip appeared, forecast risk appeared, or temperature shifted.
        // "Conditions improved" transitions for observed fields (W/V/TS/PR) are
        // suppressed — they fall through to Minor below.
        if (BecameTrue(oldP, newP, "PR"))       return ChangeSeverity.Update;
        if (BecameTrue(oldP, newP, "GC"))       return ChangeSeverity.Update;
        if (BecameTrue(oldP, newP, "GP"))       return ChangeSeverity.Update;
        if (Changed(oldP, newP, "GH"))          return ChangeSeverity.Update;
        if (Changed(oldP, newP, "GL"))          return ChangeSeverity.Update;

        // Everything remaining (observed flags cleared, GC/GP cleared): suppress.
        return ChangeSeverity.Minor;
    }

    /// <summary>
    /// Produces a human-readable description of which fingerprint fields changed
    /// and why an unscheduled report is being issued.  Intended for diagnostic
    /// logging when <see cref="ClassifyChange"/> returns a send-triggering severity.
    /// </summary>
    /// <param name="oldFp">Fingerprint from the previous send.</param>
    /// <param name="newFp">Fingerprint computed from the current snapshot.</param>
    /// <param name="snap">Current snapshot (provides actual measured values).</param>
    /// <param name="cfg">Thresholds used during fingerprint computation.</param>
    /// <returns>
    /// A semicolon-delimited string describing each changed field, e.g.
    /// <c>"Wind speed: 35 kt exceeded threshold of 25 kt; Thunderstorm: appeared"</c>,
    /// or <c>"(unknown change)"</c> if no specific field can be identified.
    /// </returns>
    public static string DescribeChanges(string oldFp, string newFp, WeatherSnapshot snap, SignificantChangeConfig cfg)
    {
        var oldP  = ParseFields(oldFp);
        var newP  = ParseFields(newFp);
        var parts = new List<string>();

        // Wind speed
        if (Changed(oldP, newP, "W"))
        {
            var windKt = snap.WindSpeedKt ?? 0;
            parts.Add(BecameTrue(oldP, newP, "W")
                ? $"Wind speed: {windKt:F0} kt exceeded threshold of {cfg.WindThresholdKt} kt"
                : $"Wind speed: {windKt:F0} kt dropped below threshold of {cfg.WindThresholdKt} kt");
        }

        // Visibility (V:True = below threshold = bad)
        if (Changed(oldP, newP, "V"))
        {
            var visSm = snap.Cavok ? 99.0 : (snap.VisibilityStatuteMiles ?? 99.0);
            parts.Add(BecameTrue(oldP, newP, "V")
                ? $"Visibility: {visSm:F1} sm dropped below threshold of {cfg.VisibilityThresholdSm} sm"
                : $"Visibility: {visSm:F1} sm rose above threshold of {cfg.VisibilityThresholdSm} sm");
        }

        // Thunderstorm
        if (Changed(oldP, newP, "TS"))
            parts.Add(BecameTrue(oldP, newP, "TS") ? "Thunderstorm: appeared" : "Thunderstorm: cleared");

        // Precipitation
        if (Changed(oldP, newP, "PR"))
            parts.Add(BecameTrue(oldP, newP, "PR") ? "Precipitation: appeared" : "Precipitation: cleared");

        // GFS forecast high/low buckets — share the nextDay lookup.
        if (Changed(oldP, newP, "GH") || Changed(oldP, newP, "GL"))
        {
            var gfsDays = snap.GfsForecast?.Days;
            var today   = DateOnly.FromDateTime(DateTime.UtcNow);
            var nextDay = gfsDays?.FirstOrDefault(d => d.Date > today) ?? gfsDays?.FirstOrDefault();

            if (Changed(oldP, newP, "GH"))
            {
                var highF = nextDay?.HighTempF;
                parts.Add(highF.HasValue
                    ? $"Forecast high: {highF.Value:F0}°F (shifted by at least {cfg.ForecastHighChangeDegF}°F)"
                    : $"Forecast high: shifted by at least {cfg.ForecastHighChangeDegF}°F");
            }

            if (Changed(oldP, newP, "GL"))
            {
                var lowF = nextDay?.LowTempF;
                parts.Add(lowF.HasValue
                    ? $"Forecast low: {lowF.Value:F0}°F (shifted by at least {cfg.ForecastLowChangeDegF}°F)"
                    : $"Forecast low: shifted by at least {cfg.ForecastLowChangeDegF}°F");
            }
        }

        // GFS CAPE risk
        if (Changed(oldP, newP, "GC"))
            parts.Add(BecameTrue(oldP, newP, "GC")
                ? $"CAPE risk: appeared (threshold {cfg.CapeThresholdJKg} J/kg)"
                : $"CAPE risk: cleared (threshold {cfg.CapeThresholdJKg} J/kg)");

        // GFS precip risk
        if (Changed(oldP, newP, "GP"))
            parts.Add(BecameTrue(oldP, newP, "GP")
                ? $"GFS precip risk: appeared (threshold {cfg.GfsPrecipThresholdMmHr} mm/hr)"
                : $"GFS precip risk: cleared (threshold {cfg.GfsPrecipThresholdMmHr} mm/hr)");

        return parts.Count > 0 ? string.Join("; ", parts) : "(unknown change)";
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a pipe-delimited fingerprint string into a key/value dictionary.
    /// Returns an empty dictionary if the string is null or malformed.
    /// </summary>
    /// <param name="fp">Fingerprint string, e.g. <c>"W:False|V:True|..."</c>.</param>
    /// <returns>Dictionary mapping field names to their string values.</returns>
    private static Dictionary<string, string> ParseFields(string? fp)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(fp)) return d;
        foreach (var part in fp.Split('|'))
        {
            var colon = part.IndexOf(':');
            if (colon > 0)
                d[part[..colon]] = part[(colon + 1)..];
        }
        return d;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="key"/> was <c>False</c> in <paramref name="old"/>
    /// and is <c>True</c> in <paramref name="neu"/> — i.e. a condition rose above its threshold.
    /// </summary>
    /// <param name="old">Parsed fields from the previous fingerprint.</param>
    /// <param name="neu">Parsed fields from the current fingerprint.</param>
    /// <param name="key">The field name to check.</param>
    /// <returns><see langword="true"/> if the field transitioned from <c>False</c> to <c>True</c>.</returns>
    private static bool BecameTrue(Dictionary<string, string> old, Dictionary<string, string> neu, string key)
        => old.TryGetValue(key, out var o) && neu.TryGetValue(key, out var n)
           && o == "False" && n == "True";

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="key"/> was <c>True</c> in <paramref name="old"/>
    /// and is <c>False</c> in <paramref name="neu"/> — i.e. a condition fell back below its threshold
    /// (conditions improved).
    /// </summary>
    /// <param name="old">Parsed fields from the previous fingerprint.</param>
    /// <param name="neu">Parsed fields from the current fingerprint.</param>
    /// <param name="key">The field name to check.</param>
    /// <returns><see langword="true"/> if the field transitioned from <c>True</c> to <c>False</c>.</returns>
    private static bool BecameFalse(Dictionary<string, string> old, Dictionary<string, string> neu, string key)
        => old.TryGetValue(key, out var o) && neu.TryGetValue(key, out var n)
           && o == "True" && n == "False";

    /// <summary>Returns <see langword="true"/> when the value of <paramref name="key"/> differs between <paramref name="old"/> and <paramref name="neu"/>.</summary>
    /// <param name="old">Parsed fields from the previous fingerprint.</param>
    /// <param name="neu">Parsed fields from the current fingerprint.</param>
    /// <param name="key">The field name to check.</param>
    /// <returns><see langword="true"/> if the field value changed.</returns>
    private static bool Changed(Dictionary<string, string> old, Dictionary<string, string> neu, string key)
        => old.TryGetValue(key, out var o) && neu.TryGetValue(key, out var n) && o != n;
}
