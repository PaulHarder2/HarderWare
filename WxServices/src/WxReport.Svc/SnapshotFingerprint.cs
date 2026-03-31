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
    /// low ceiling, or high winds appeared).
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
    ///   <item>The lowest BKN/OVC/VV ceiling crosses the configured feet threshold.</item>
    ///   <item>A thunderstorm phenomenon appears or disappears.</item>
    ///   <item>Any precipitation phenomenon appears or disappears.</item>
    ///   <item>The GFS forecast high for the next calendar day shifts by at least <see cref="SignificantChangeConfig.ForecastHighChangeDegF"/> degrees.</item>
    ///   <item>Any GFS forecast day crosses the <see cref="SignificantChangeConfig.CapeThresholdJKg"/> thunderstorm-potential threshold.</item>
    ///   <item>Any GFS forecast day crosses the <see cref="SignificantChangeConfig.GfsPrecipThresholdMmHr"/> precipitation threshold.</item>
    /// </list>
    /// </summary>
    /// <param name="snap">The weather snapshot to fingerprint.</param>
    /// <param name="cfg">Thresholds that define what constitutes a "significant" condition for each dimension.</param>
    /// <returns>
    /// A compact pipe-delimited string encoding eight fields, e.g.
    /// <c>"W:False|V:False|C:True|TS:False|PR:True|GH:5|GC:False|GP:True"</c>.
    /// The <c>GH</c> field is a bucket index derived by dividing the forecast high
    /// by <see cref="SignificantChangeConfig.ForecastHighChangeDegF"/>; it is <c>?</c>
    /// when no GFS data is available.
    /// </returns>
    public static string Compute(WeatherSnapshot snap, SignificantChangeConfig cfg)
    {
        var windKt   = snap.WindSpeedKt ?? 0;
        var windHigh = windKt >= cfg.WindThresholdKt;

        var visSm  = snap.Cavok ? 99.0 : (snap.VisibilityStatuteMiles ?? 99.0);
        var visLow = visSm < cfg.VisibilityThresholdSm;

        var ceiling    = LowestSignificantCeilingFt(snap);
        var ceilingLow = ceiling.HasValue && ceiling.Value < cfg.CeilingThresholdFt;

        // Current (non-recent) thunderstorm
        var hasTs = snap.WeatherPhenomena.Any(w =>
            w.Descriptor == WeatherDescriptor.Thunderstorm && !w.IsRecent);

        // Current (non-recent) precipitation
        var hasPrecip = snap.WeatherPhenomena.Any(w =>
            w.Precipitation.Count > 0 && !w.IsRecent);

        // GFS: bucket the next calendar day's forecast high to ForecastHighChangeDegF resolution.
        // A change in bucket means the high shifted by at least that many degrees.
        var gfsDays    = snap.GfsForecast?.Days;
        var today      = DateOnly.FromDateTime(DateTime.UtcNow);
        var nextDay    = gfsDays?.FirstOrDefault(d => d.Date > today) ?? gfsDays?.FirstOrDefault();
        var highBucket = nextDay?.HighTempF.HasValue == true
            ? (int?)Math.Floor(nextDay.HighTempF!.Value / cfg.ForecastHighChangeDegF)
            : null;

        // GFS: CAPE risk — any forecast day exceeds the thunderstorm-potential threshold.
        var gfsCapeRisk = gfsDays?.Any(d => d.MaxCapeJKg >= cfg.CapeThresholdJKg) ?? false;

        // GFS: precip risk — any forecast day exceeds the precipitation-rate threshold.
        var gfsPrecipRisk = gfsDays?.Any(d => d.MaxPrecipRateMmHr >= cfg.GfsPrecipThresholdMmHr) ?? false;

        return $"W:{windHigh}|V:{visLow}|C:{ceilingLow}|TS:{hasTs}|PR:{hasPrecip}" +
               $"|GH:{highBucket?.ToString() ?? "?"}|GC:{gfsCapeRisk}|GP:{gfsPrecipRisk}";
    }

    /// <summary>
    /// Classifies the severity of the change between two fingerprints to determine
    /// whether an unscheduled report should be sent and what tone it should use.
    /// </summary>
    /// <remarks>
    /// Classification rules (evaluated in priority order):
    /// <list type="bullet">
    ///   <item><b>Alert</b> — a dangerous current-conditions flag (<c>W</c>, <c>V</c>, <c>C</c>, or <c>TS</c>) changed to <c>True</c>.</item>
    ///   <item><b>Update</b> — any current-conditions flag cleared; <c>PR</c> changed; a GFS risk flag (<c>GC</c> or <c>GP</c>) appeared; or the temperature bucket (<c>GH</c>) shifted.</item>
    ///   <item><b>Minor</b> — only GFS risk flags (<c>GC</c>/<c>GP</c>) cleared (good news; no send needed).</item>
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
        if (BecameTrue(oldP, newP, "C"))  return ChangeSeverity.Alert;
        if (BecameTrue(oldP, newP, "TS")) return ChangeSeverity.Alert;

        // Update: conditions cleared, precip changed, or forecast worsened.
        if (Changed(oldP, newP, "W"))           return ChangeSeverity.Update;
        if (Changed(oldP, newP, "V"))           return ChangeSeverity.Update;
        if (Changed(oldP, newP, "C"))           return ChangeSeverity.Update;
        if (Changed(oldP, newP, "TS"))          return ChangeSeverity.Update;
        if (Changed(oldP, newP, "PR"))          return ChangeSeverity.Update;
        if (BecameTrue(oldP, newP, "GC"))       return ChangeSeverity.Update;
        if (BecameTrue(oldP, newP, "GP"))       return ChangeSeverity.Update;
        if (Changed(oldP, newP, "GH"))          return ChangeSeverity.Update;

        // Everything remaining (GC/GP cleared): suppress.
        return ChangeSeverity.Minor;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the height in feet of the lowest broken, overcast, or vertical-visibility
    /// sky layer in <paramref name="snap"/>, or <see langword="null"/> if no such layer exists.
    /// </summary>
    /// <param name="snap">The snapshot whose sky layers to examine.</param>
    /// <returns>Height in feet of the lowest significant ceiling layer, or <see langword="null"/>.</returns>
    private static int? LowestSignificantCeilingFt(WeatherSnapshot snap) =>
        snap.SkyLayers
            .Where(l => l.Coverage is SkyCoverage.Broken
                                   or SkyCoverage.Overcast
                                   or SkyCoverage.VerticalVisibility)
            .Select(l => l.HeightFeet)
            .Where(h => h.HasValue)
            .Select(h => h!.Value)
            .OrderBy(h => h)
            .Cast<int?>()
            .FirstOrDefault();

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

    /// <summary>Returns <see langword="true"/> when <paramref name="key"/> was <c>False</c> in <paramref name="old"/> and is <c>True</c> in <paramref name="neu"/>.</summary>
    /// <param name="old">Parsed fields from the previous fingerprint.</param>
    /// <param name="neu">Parsed fields from the current fingerprint.</param>
    /// <param name="key">The field name to check.</param>
    /// <returns><see langword="true"/> if the field transitioned from <c>False</c> to <c>True</c>.</returns>
    private static bool BecameTrue(Dictionary<string, string> old, Dictionary<string, string> neu, string key)
        => old.TryGetValue(key, out var o) && neu.TryGetValue(key, out var n)
           && o == "False" && n == "True";

    /// <summary>Returns <see langword="true"/> when the value of <paramref name="key"/> differs between <paramref name="old"/> and <paramref name="neu"/>.</summary>
    /// <param name="old">Parsed fields from the previous fingerprint.</param>
    /// <param name="neu">Parsed fields from the current fingerprint.</param>
    /// <param name="key">The field name to check.</param>
    /// <returns><see langword="true"/> if the field value changed.</returns>
    private static bool Changed(Dictionary<string, string> old, Dictionary<string, string> neu, string key)
        => old.TryGetValue(key, out var o) && neu.TryGetValue(key, out var n) && o != n;
}
