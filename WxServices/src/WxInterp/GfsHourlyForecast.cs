namespace WxInterp;

/// <summary>
/// One hour of GFS model-forecast values at a recipient's exact location,
/// produced by bilinear interpolation over the four surrounding 0.25° grid
/// points.  Hourly-resolution counterpart of <see cref="GfsDailyForecast"/>;
/// consumed by the 6-hour snapshot builder under WX-77.
/// </summary>
public sealed class GfsHourlyPoint
{
    /// <summary>UTC valid-time of this forecast hour.</summary>
    public DateTime ValidTimeUtc { get; init; }

    /// <summary>2-metre air temperature in degrees Celsius, or <see langword="null"/> if unavailable.</summary>
    public float? TmpC { get; init; }

    /// <summary>2-metre dew-point temperature in degrees Celsius, or <see langword="null"/> if unavailable.  Used by WX-77 as part of the freezing-precip phenomenon proxy.</summary>
    public float? DwpC { get; init; }

    /// <summary>Sustained 10-metre wind speed in knots, derived from the U/V components, or <see langword="null"/> if unavailable.</summary>
    public float? WindKt { get; init; }

    /// <summary>Meteorological wind direction (from) in degrees true, or <see langword="null"/> if unavailable.</summary>
    public int? WindDirDeg { get; init; }

    /// <summary>Instantaneous precipitation rate in mm/hr (converted from the GFS native kg m⁻² s⁻¹), or <see langword="null"/> if unavailable.</summary>
    public float? PrecipMmHr { get; init; }

    /// <summary>Total cloud cover as a percentage (0–100), or <see langword="null"/> if unavailable.</summary>
    public float? TcdcPct { get; init; }

    /// <summary>Surface-based CAPE in joules per kilogram, or <see langword="null"/> if unavailable.</summary>
    public float? CapeJKg { get; init; }
}

/// <summary>
/// GFS model forecast at hourly resolution at a recipient's exact location,
/// produced by bilinear interpolation.  Hourly-resolution counterpart of
/// <see cref="GfsForecast"/>; introduced under WX-77 to feed the 6-hour
/// snapshot builder.  Daily-summary callers continue to use
/// <see cref="GfsForecast"/>.
/// </summary>
public sealed class GfsHourlyForecast
{
    /// <summary>UTC initialisation time of the GFS model run used to produce this forecast.</summary>
    public DateTime ModelRunUtc { get; init; }

    /// <summary>Per-hour interpolated values in ascending valid-time order.</summary>
    public IReadOnlyList<GfsHourlyPoint> Hours { get; init; } = [];
}