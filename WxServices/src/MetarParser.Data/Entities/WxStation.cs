namespace MetarParser.Data.Entities;

/// <summary>
/// Entity representing one row in the <c>WxStations</c> table.
/// Each row holds the geographic metadata for a METAR reporting station,
/// populated on first encounter during a METAR fetch cycle.
/// </summary>
public sealed class WxStation
{
    /// <summary>
    /// Four-letter ICAO station identifier (primary key), e.g. <c>"KDWH"</c>.
    /// </summary>
    public string IcaoId { get; set; } = "";

    /// <summary>
    /// Full name of the airport or weather station as returned by the
    /// Aviation Weather Center airport API, e.g. <c>"David Wayne Hooks Memorial"</c>.
    /// <see langword="null"/> if the API did not return a name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Station latitude in decimal degrees North.
    /// Positive values are north of the equator; negative values are south.
    /// <see langword="null"/> when the AWC airport API could not resolve this station.
    /// </summary>
    public double? Lat { get; set; }

    /// <summary>
    /// Station longitude in decimal degrees East.
    /// Positive values are east of the prime meridian; negative values (i.e. the
    /// Americas) are west.
    /// <see langword="null"/> when the AWC airport API could not resolve this station.
    /// </summary>
    public double? Lon { get; set; }

    /// <summary>
    /// Station elevation in feet above mean sea level.
    /// <see langword="null"/> if the API did not return an elevation.
    /// </summary>
    public double? ElevationFt { get; set; }
}
