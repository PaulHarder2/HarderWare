using WxInterp;

namespace WxReport.Svc;

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
    /// </list>
    /// </summary>
    /// <param name="snap">The weather snapshot to fingerprint.</param>
    /// <param name="cfg">Thresholds that define what constitutes a "significant" condition for each dimension.</param>
    /// <returns>
    /// A compact pipe-delimited string encoding five boolean flags, e.g.
    /// <c>"W:False|V:False|C:True|TS:False|PR:True"</c>.
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

        return $"W:{windHigh}|V:{visLow}|C:{ceilingLow}|TS:{hasTs}|PR:{hasPrecip}";
    }

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
}
