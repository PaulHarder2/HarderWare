namespace MetarParser.Data.Entities;

/// <summary>
/// A named locality that groups co-located recipients so the expensive per-cycle
/// Claude reconciliation runs once per locality and is then rendered per recipient
/// (WX-123). The locality owns the shared METAR/TAF stations and the centroid
/// lat/lon used as the GFS forecast point for its members.
/// </summary>
/// <remarks>
/// Distinct from <see cref="Recipient.LocalityName"/>, which is only a display label.
/// This is a first-class table; the membership foreign key
/// <c>Recipient.LocalityId</c> is added in WX-125.
/// </remarks>
public class Locality
{
    /// <summary>Auto-incremented surrogate key (bigint).</summary>
    public long Id { get; set; }

    /// <summary>
    /// Human-curated locality name, unique across localities (e.g. <c>"Spring"</c>).
    /// Used in reports and managed via WxManager's Localities tab (WX-127).
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Shared METAR station ICAO(s) in priority order, comma-separated
    /// (e.g. <c>"KDWH, KHOU"</c>), copied verbatim into each member's
    /// <see cref="Recipient.MetarIcao"/> (WX-125). Same first-available fallback
    /// semantics as <see cref="Recipient.MetarIcao"/>.
    /// </summary>
    public string? MetarIcao { get; set; }

    /// <summary>Shared TAF station ICAO for the locality.</summary>
    public string? TafIcao { get; set; }

    /// <summary>
    /// Centroid latitude — the mean of member recipients' latitudes — used as the
    /// locality's GFS forecast point. Recomputed on membership change (WX-126);
    /// <see langword="null"/> until the locality has members.
    /// </summary>
    public double? CentroidLat { get; set; }

    /// <summary>Centroid longitude; see <see cref="CentroidLat"/>.</summary>
    public double? CentroidLon { get; set; }
}