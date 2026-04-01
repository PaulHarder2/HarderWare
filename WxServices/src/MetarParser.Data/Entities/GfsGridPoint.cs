namespace MetarParser.Data.Entities;

/// <summary>
/// Entity representing one row in the <c>GfsGrid</c> table.
/// Each row holds the GFS model-forecast values for a single grid point
/// (identified by latitude and longitude) at a specific model-run time
/// and forecast hour.
/// <para>
/// The bounding box and resolution are controlled by configuration.  Up to two
/// model runs are retained simultaneously during the transition period between
/// an incoming run and the previous one.
/// </para>
/// </summary>
public sealed class GfsGridPoint
{
    /// <summary>Primary key, auto-incremented by the database.</summary>
    public int Id { get; set; }

    /// <summary>
    /// UTC date and time at which the GFS model run was initialised,
    /// e.g. <c>2026-03-29 12:00 UTC</c>.
    /// GFS runs at 00Z, 06Z, 12Z, and 18Z each day.
    /// </summary>
    public DateTime ModelRunUtc { get; set; }

    /// <summary>
    /// Forecast hour offset from <see cref="ModelRunUtc"/>.
    /// Ranges from 0 (analysis) to 120 (five days ahead) in one-hour steps.
    /// </summary>
    public int ForecastHour { get; set; }

    /// <summary>
    /// Grid point latitude in decimal degrees North.
    /// Positive values are north of the equator; negative values are south.
    /// </summary>
    public float Lat { get; set; }

    /// <summary>
    /// Grid point longitude in decimal degrees East.
    /// Positive values are east of the prime meridian; negative values (i.e. the
    /// Americas) are west.
    /// </summary>
    public float Lon { get; set; }

    // ── meteorological parameters ────────────────────────────────────────────

    /// <summary>
    /// 2-metre air temperature in degrees Celsius.
    /// Converted from the native GFS unit of Kelvin during ingestion.
    /// <see langword="null"/> if not available for this point.
    /// </summary>
    public float? TmpC { get; set; }

    /// <summary>
    /// 2-metre dew-point temperature in degrees Celsius.
    /// Derived from the GFS 2-metre specific-humidity field during ingestion.
    /// <see langword="null"/> if not available for this point.
    /// </summary>
    public float? DwpC { get; set; }

    /// <summary>
    /// 10-metre U (eastward) wind component in metres per second.
    /// Combine with <see cref="VGrdMs"/> to derive speed and direction.
    /// <see langword="null"/> if not available.
    /// </summary>
    public float? UGrdMs { get; set; }

    /// <summary>
    /// 10-metre V (northward) wind component in metres per second.
    /// Combine with <see cref="UGrdMs"/> to derive speed and direction.
    /// <see langword="null"/> if not available.
    /// </summary>
    public float? VGrdMs { get; set; }

    /// <summary>
    /// Instantaneous precipitation rate in kg m⁻² s⁻¹ (numerically equivalent
    /// to mm s⁻¹).
    /// <see langword="null"/> if not available.
    /// </summary>
    public float? PRateKgM2s { get; set; }

    /// <summary>
    /// Total cloud cover as a percentage (0–100).
    /// <see langword="null"/> if not available.
    /// </summary>
    public float? TcdcPct { get; set; }

    /// <summary>
    /// Surface-based Convective Available Potential Energy (CAPE) in joules per
    /// kilogram.  Higher values indicate greater convective instability; values
    /// above ~1,000 J/kg suggest significant thunderstorm potential.
    /// <see langword="null"/> if not available.
    /// </summary>
    public float? CapeJKg { get; set; }

    /// <summary>
    /// Mean sea-level pressure in Pascals.
    /// Derived from the GFS <c>PRMSL</c> field at the mean sea level surface.
    /// Divide by 100 to convert to hectopascals (hPa / millibars).
    /// <see langword="null"/> if not available.
    /// </summary>
    public float? PrMslPa { get; set; }
}
