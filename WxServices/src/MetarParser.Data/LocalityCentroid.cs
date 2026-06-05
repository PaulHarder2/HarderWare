using MetarParser.Data.Entities;

namespace MetarParser.Data;

/// <summary>
/// Computes a locality's centroid — the mean of its member recipients' coordinates —
/// which serves as the single GFS forecast point for the locality (WX-123). Recomputed
/// whenever a locality's membership changes (WxManager's Localities tab calls
/// <see cref="Recompute"/>, WX-127); the per-locality generation loop (WX-130) consumes
/// the stored centroid.
/// </summary>
public static class LocalityCentroid
{
    /// <summary>
    /// Recomputes <paramref name="locality"/>'s <see cref="Locality.CentroidLat"/> /
    /// <see cref="Locality.CentroidLon"/> as the planar mean of the coordinates of the
    /// <paramref name="members"/> that have both a latitude and longitude. Members
    /// without coordinates (e.g. not yet geocoded) are excluded; if none have
    /// coordinates, both centroid fields are set to <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// A planar (arithmetic) mean is exact enough at metropolitan scale; the
    /// antimeridian / great-circle wrap-around is a non-issue for the CONUS localities
    /// this serves.
    /// </remarks>
    public static void Recompute(Locality locality, IEnumerable<Recipient> members)
    {
        ArgumentNullException.ThrowIfNull(locality);
        ArgumentNullException.ThrowIfNull(members);

        double latSum = 0, lonSum = 0;
        int count = 0;
        foreach (var member in members)
        {
            ArgumentNullException.ThrowIfNull(member);
            if (member.Latitude is double lat && member.Longitude is double lon)
            {
                latSum += lat;
                lonSum += lon;
                count++;
            }
        }

        if (count == 0)
        {
            locality.CentroidLat = null;
            locality.CentroidLon = null;
            return;
        }

        locality.CentroidLat = latSum / count;
        locality.CentroidLon = lonSum / count;
    }
}