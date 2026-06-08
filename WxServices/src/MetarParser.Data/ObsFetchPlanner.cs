using WxServices.Common;

namespace MetarParser.Data;

/// <summary>
/// Builds the set of bounding boxes the METAR/TAF fetch covers each cycle
/// (WX-140).  One continent-sized box is exactly what the AWC data API
/// silently truncates (observed: ~560 reports returned for a CONUS bbox of
/// 2,000+ stations), so observation fetching uses a <em>plan</em> of small
/// boxes instead: the home box plus one box around every locality centroid
/// and every locality-less recipient.  Small boxes stay far under the cap,
/// give the 30-mile station-fallback search its neighborhood data, and scale
/// with the locality architecture by construction — adding a locality adds
/// its box with no manual flagging.
///
/// <para>
/// Pure geometry, no I/O: callers supply coordinates, this orders and
/// deduplicates.  The GFS fetch is deliberately NOT planned here — it keeps
/// the explicit <c>Fetch:Region*</c> CONUS region the maps require.
/// </para>
/// </summary>
public static class ObsFetchPlanner
{
    /// <summary>
    /// Builds the ordered, deduplicated box list: the home box first (largest,
    /// so containment pruning bites), then one box per point.  A box entirely
    /// contained in an earlier accepted box is dropped; duplicate points
    /// (e.g. two localities sharing a centroid) collapse naturally the same way.
    /// </summary>
    /// <param name="homeLat">Home latitude, or <see langword="null"/> when unconfigured.</param>
    /// <param name="homeLon">Home longitude, or <see langword="null"/> when unconfigured.</param>
    /// <param name="homeBoxDegrees">Half-width of the home box in degrees (the long-standing <c>Fetch:BoundingBoxDegrees</c>).</param>
    /// <param name="points">Locality centroids and locality-less recipients' coordinates.</param>
    /// <param name="pointBoxDegrees">Half-width of each point box in degrees (<c>Fetch:LocalityBoxDegrees</c>).</param>
    /// <returns>The boxes to fetch, in fetch order.  Empty when nothing is configured.</returns>
    public static IReadOnlyList<FetchRegion> Plan(
        double? homeLat, double? homeLon, double homeBoxDegrees,
        IEnumerable<(double Lat, double Lon)> points, double pointBoxDegrees)
    {
        var plan = new List<FetchRegion>();

        if (homeLat.HasValue && homeLon.HasValue)
            plan.Add(BoxAround(homeLat.Value, homeLon.Value, homeBoxDegrees));

        foreach (var (lat, lon) in points)
        {
            var box = BoxAround(lat, lon, pointBoxDegrees);
            if (!plan.Any(existing => existing.Contains(box)))
                plan.Add(box);
        }

        return plan;
    }

    private static FetchRegion BoxAround(double lat, double lon, double halfWidthDegrees) => new(
        Math.Max(-90, lat - halfWidthDegrees),
        Math.Min(90, lat + halfWidthDegrees),
        lon - halfWidthDegrees,
        lon + halfWidthDegrees);

    /// <summary>
    /// Stations the bbox fetches should have covered but didn't: the AWC API
    /// omits individual stations from bbox results even when the box is small
    /// (the long-standing reason <c>AlwaysFetchDirect</c> exists), so after the
    /// boxes run, every <em>defined</em> station a report could draw on gets a
    /// freshness check and the gaps get a direct by-ID fetch (WX-140).
    /// </summary>
    /// <param name="points">Locality centroids and locality-less recipients' coordinates.</param>
    /// <param name="radiusKm">
    /// Neighborhood radius — should match the WxReport station-fallback radius
    /// (<c>WxInterpreter.MaxFallbackDistanceKm</c>, 50 km ≈ 30 mi), so every
    /// station the fallback is allowed to choose is a station this layer tried
    /// to fetch.  Distances use the same <see cref="GeoMath.HaversineKm"/>.
    /// </param>
    /// <param name="stations">Known stations with coordinates (from <c>WxStations</c>).</param>
    /// <param name="namedIcaos">
    /// Stations explicitly named by localities/recipients (<c>MetarIcao</c>
    /// lists).  Always candidates regardless of distance — a locality's chosen
    /// station can sit outside the fallback radius of its own centroid
    /// (Watonga's KEND is ~68 km out).
    /// </param>
    /// <param name="freshIcaos">Stations that already have a recent observation — covered, not a gap.</param>
    /// <returns>Ordinal-ordered ICAOs needing a direct fetch.</returns>
    public static IReadOnlyList<string> MissingNeighborStations(
        IEnumerable<(double Lat, double Lon)> points,
        double radiusKm,
        IEnumerable<(string Icao, double Lat, double Lon)> stations,
        IEnumerable<string> namedIcaos,
        IEnumerable<string> freshIcaos)
    {
        var pointList = points.ToList();
        var fresh = new HashSet<string>(freshIcaos, StringComparer.OrdinalIgnoreCase);
        var candidates = new HashSet<string>(namedIcaos, StringComparer.OrdinalIgnoreCase);

        foreach (var (icao, lat, lon) in stations)
            if (pointList.Any(p => GeoMath.HaversineKm(p.Lat, p.Lon, lat, lon) <= radiusKm))
                candidates.Add(icao);

        return candidates
            .Where(icao => !fresh.Contains(icao))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}