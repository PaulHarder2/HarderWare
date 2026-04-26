namespace MetarParser.Data.Entities;

/// <summary>
/// Entity representing one row in the <c>WxStations</c> table.
/// Each row holds the geographic metadata for a METAR reporting station,
/// populated on first encounter during a METAR fetch cycle.
/// Rows may be updated by the OurAirports importer to enrich name and municipality data.
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
    /// City or municipality in which the station is located, as provided by
    /// OurAirports (e.g. "College Station").
    /// <see langword="null"/> until the OurAirports import has run or if the
    /// station is not present in the OurAirports dataset.
    /// </summary>
    public string? Municipality { get; set; }

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

    /// <summary>
    /// When <see langword="true"/>, the METAR fetch cycle always requests this station via a
    /// single-station query in addition to the bounding-box query, because the station is known
    /// to be omitted from bbox results unreliably.
    /// <see langword="null"/> means the station has not yet been evaluated.
    /// </summary>
    public bool? AlwaysFetchDirect { get; set; }

    /// <summary>
    /// Full name of the state, province, or region (ISO 3166-2 subdivision name),
    /// e.g. <c>"Texas"</c> or <c>"England"</c>.
    /// <see langword="null"/> when the OurAirports dataset does not supply a region for this station.
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// Full ISO 3166-2 subdivision code (country-prefixed), e.g. <c>"US-TX"</c> or <c>"GB-ENG"</c>.
    /// </summary>
    public string? RegionCode { get; set; }

    /// <summary>
    /// Short subdivision abbreviation — the portion of <see cref="RegionCode"/> after the hyphen,
    /// e.g. <c>"TX"</c> or <c>"ENG"</c>.  Preferred for compact report display.
    /// </summary>
    public string? RegionAbbr { get; set; }

    /// <summary>
    /// ISO 3166-1 country short name, e.g. <c>"United States"</c> or <c>"United Kingdom"</c>.
    /// </summary>
    public string? Country { get; set; }

    /// <summary>
    /// ISO 3166-1 alpha-2 country code, e.g. <c>"US"</c> or <c>"GB"</c>.
    /// </summary>
    public string? CountryCode { get; set; }

    /// <summary>
    /// Display-friendly country abbreviation distinct from <see cref="CountryCode"/> where
    /// convention differs from ISO, e.g. <c>"UK"</c> for GB, <c>"USA"</c> for US.
    /// Defaults to <see cref="CountryCode"/> when no override applies.
    /// </summary>
    public string? CountryAbbr { get; set; }
}