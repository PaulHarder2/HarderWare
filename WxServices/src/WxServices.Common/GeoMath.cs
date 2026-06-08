namespace WxServices.Common;

/// <summary>
/// Shared spherical-geometry helpers.  Promoted from a private copy in
/// <c>WxInterpreter</c> (WX-140) so the fetch planner and the station-fallback
/// search measure distance identically — a station the fallback would accept
/// must be a station the fetch layer tried to cover.
/// </summary>
public static class GeoMath
{
    /// <summary>
    /// Great-circle distance in kilometres between two points on Earth, using
    /// the haversine formula with a mean radius of 6371.0 km.
    /// </summary>
    public static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0;
        var dLat = (lat2 - lat1) * Math.PI / 180.0;
        var dLon = (lon2 - lon1) * Math.PI / 180.0;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0)
              * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}