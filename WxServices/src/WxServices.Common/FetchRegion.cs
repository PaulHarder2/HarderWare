using System.Globalization;

namespace WxServices.Common;

/// <summary>
/// Represents the geographic bounding box for weather data fetching.
/// </summary>
/// <param name="South">Southern latitude bound (degrees).</param>
/// <param name="North">Northern latitude bound (degrees).</param>
/// <param name="West">Western longitude bound (degrees, negative = west).</param>
/// <param name="East">Eastern longitude bound (degrees, negative = west).</param>
public sealed record FetchRegion(double South, double North, double West, double East)
{
    /// <summary>
    /// Formats the region as a comma-separated bbox string suitable for the
    /// Aviation Weather Center API: <c>south,west,north,east</c>.
    /// </summary>
    public string ToAwcBbox() => $"{South},{West},{North},{East}";

    /// <summary>
    /// Returns the centre latitude of the region.
    /// </summary>
    public double CentreLat => (South + North) / 2;

    /// <summary>
    /// Returns the centre longitude of the region.
    /// </summary>
    public double CentreLon => (West + East) / 2;

    /// <summary>
    /// Returns <see langword="true"/> if the given point falls within the region.
    /// </summary>
    public bool Contains(double lat, double lon)
        => lat >= South && lat <= North && lon >= West && lon <= East;

    /// <summary>
    /// Resolves the fetch region from configuration values.
    /// <para>
    /// If explicit region bounds (<c>RegionSouth</c>, <c>RegionNorth</c>,
    /// <c>RegionWest</c>, <c>RegionEast</c>) are all set, uses those directly.
    /// Otherwise, falls back to <c>HomeLatitude ± BoundingBoxDegrees</c> /
    /// <c>HomeLongitude ± BoundingBoxDegrees</c>.
    /// </para>
    /// </summary>
    /// <param name="regionSouth">Explicit southern bound, or <see langword="null"/>.</param>
    /// <param name="regionNorth">Explicit northern bound, or <see langword="null"/>.</param>
    /// <param name="regionWest">Explicit western bound, or <see langword="null"/>.</param>
    /// <param name="regionEast">Explicit eastern bound, or <see langword="null"/>.</param>
    /// <param name="homeLat">Home latitude (used when explicit bounds are absent).</param>
    /// <param name="homeLon">Home longitude (used when explicit bounds are absent).</param>
    /// <param name="boxDegrees">Bounding box half-width in degrees (used when explicit bounds are absent). Defaults to 9.</param>
    /// <returns>The resolved fetch region, or <see langword="null"/> if neither explicit bounds nor home coordinates are available.</returns>
    public static FetchRegion? Resolve(
        double? regionSouth, double? regionNorth,
        double? regionWest, double? regionEast,
        double? homeLat, double? homeLon,
        double boxDegrees = 9)
    {
        if (regionSouth.HasValue && regionNorth.HasValue &&
            regionWest.HasValue && regionEast.HasValue)
        {
            return new FetchRegion(regionSouth.Value, regionNorth.Value,
                                   regionWest.Value, regionEast.Value);
        }

        if (homeLat.HasValue && homeLon.HasValue)
        {
            return new FetchRegion(
                Math.Max(-90, homeLat.Value - boxDegrees),
                Math.Min(90, homeLat.Value + boxDegrees),
                homeLon.Value - boxDegrees,
                homeLon.Value + boxDegrees);
        }

        return null;
    }

    /// <summary>
    /// Convenience overload that reads directly from an <c>IConfiguration</c>-style
    /// key-value lookup.
    /// </summary>
    public static FetchRegion? FromConfig(Func<string, string?> getValue)
    {
        return Resolve(
            regionSouth: ParseDouble(getValue("Fetch:RegionSouth")),
            regionNorth: ParseDouble(getValue("Fetch:RegionNorth")),
            regionWest: ParseDouble(getValue("Fetch:RegionWest")),
            regionEast: ParseDouble(getValue("Fetch:RegionEast")),
            homeLat: ParseDouble(getValue("Fetch:HomeLatitude")),
            homeLon: ParseDouble(getValue("Fetch:HomeLongitude")),
            boxDegrees: ParseDouble(getValue("Fetch:BoundingBoxDegrees")) ?? 9);
    }

    private static double? ParseDouble(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}