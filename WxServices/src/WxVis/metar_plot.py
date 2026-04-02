"""
metar_plot.py — Utility functions for rendering standard WMO METAR station plots.

Provides helper functions to prepare METAR DataFrame columns into the forms
expected by MetPy's StationPlot, and a top-level function that draws and
saves a station-plot figure.

Typical usage:
    from db import get_engine, load_latest_metars
    from metar_plot import render_station_plots

    engine = get_engine()
    df = load_latest_metars(engine)
    render_station_plots(df, output_path="plots/metars.png")
"""

import re
import numpy as np
import pandas as pd
import matplotlib.pyplot as plt
import cartopy.crs as ccrs
import cartopy.feature as cfeature

from metpy.plots import StationPlot
from metpy.plots.wx_symbols import sky_cover, current_weather

from logger import logger
import metpy.calc as mpcalc
from metpy.units import units


# ── Sky cover ─────────────────────────────────────────────────────────────────

# Maps METAR sky-condition coverage tokens to WMO okta values (0–8).
# The station model shows the most significant (highest coverage) layer.
_COVERAGE_OKTAS: dict[str, int] = {
    "SKC": 0,
    "CLR": 0,
    "NCD": 0,
    "NSC": 0,
    "FEW": 1,   # 1–2/8
    "SCT": 3,   # 3–4/8
    "BKN": 6,   # 5–7/8
    "OVC": 8,
    "VV":  8,   # vertical visibility — sky obscured
}

_COVERAGE_ORDER = ["OVC", "VV", "BKN", "SCT", "FEW", "SKC", "CLR", "NCD", "NSC"]


def parse_sky_oktas(raw_sky: str | None) -> int:
    """
    Return the WMO okta value (0–8) for the most significant sky layer in the
    raw sky-condition string (e.g. ``"FEW030 SCT060 BKN120"``).

    Returns 0 (clear) when *raw_sky* is None or empty.
    """
    if not isinstance(raw_sky, str) or not raw_sky:
        return 0
    tokens = raw_sky.upper().split()
    for cover_token in _COVERAGE_ORDER:
        for token in tokens:
            if token.startswith(cover_token):
                return _COVERAGE_OKTAS[cover_token]
    return 0


# ── Present weather ───────────────────────────────────────────────────────────

# A minimal mapping from METAR present-weather descriptors to WMO present-
# weather codes (ww, 00–99) used in the station model symbol font.
# Only the most common phenomena are mapped; unmapped weather returns None.
_WX_CODE_MAP: list[tuple[re.Pattern, int]] = [
    # Thunderstorms
    (re.compile(r"\bTS\b"),       95),   # thunderstorm
    (re.compile(r"\bTS.*GR\b"),   99),   # thunderstorm with hail
    # Freezing precipitation
    (re.compile(r"\bFZRA\b"),     66),   # freezing rain
    (re.compile(r"\bFZDZ\b"),     56),   # freezing drizzle
    # Snow
    (re.compile(r"\+SN\b"),       75),   # heavy snow
    (re.compile(r"\bSN\b"),       71),   # snow (moderate/light)
    (re.compile(r"\bSG\b"),       77),   # snow grains
    # Rain
    (re.compile(r"\+RA\b"),       65),   # heavy rain
    (re.compile(r"-RA\b"),        61),   # light rain
    (re.compile(r"\bRA\b"),       63),   # moderate rain
    # Drizzle
    (re.compile(r"\bDZ\b"),       51),   # drizzle
    # Obscurations
    (re.compile(r"\bFG\b"),        45),  # fog
    (re.compile(r"\bBR\b"),        10),  # mist
    (re.compile(r"\bHZ\b"),         5),  # haze
    (re.compile(r"\bFU\b"),         4),  # smoke
    (re.compile(r"\bDU\b"),         6),  # widespread dust
    (re.compile(r"\bSA\b"),         7),  # dust/sand whirls
    (re.compile(r"\bSS\b"),        34),  # sandstorm
    (re.compile(r"\bDS\b"),        34),  # dust storm
]


def parse_present_weather_code(raw_wx: str | None) -> int | None:
    """
    Return a WMO present-weather code (0–99) for the most significant
    phenomenon in *raw_wx* (e.g. ``"-RA BR"``), or ``None`` if no
    recognisable phenomenon is found.
    """
    if not isinstance(raw_wx, str) or not raw_wx:
        return None
    wx = raw_wx.upper()
    for pattern, code in _WX_CODE_MAP:
        if pattern.search(wx):
            return code
    return None


# ── Altimeter / SLP conversion ────────────────────────────────────────────────

def altimeter_to_slp_hpa(altimeter_value: float | None,
                          altimeter_unit: str | None,
                          elevation_ft: float | None) -> float | None:
    """
    Convert a METAR altimeter setting to approximate mean sea-level pressure
    in hPa using the standard altimetry reduction.

    For stations near sea level (elevation < 50 ft) the altimeter setting in
    hPa is effectively equal to SLP; for elevated stations the hypsometric
    correction is applied.

    Returns ``None`` if *altimeter_value* is None.
    """
    if altimeter_value is None:
        return None

    # Convert to hPa
    if isinstance(altimeter_unit, str) and altimeter_unit.upper() == "INHG":
        qnh_hpa = altimeter_value * 33.8639
    else:
        qnh_hpa = float(altimeter_value)

    if elevation_ft is None or elevation_ft < 1.0:
        return qnh_hpa

    # Simple hypsometric correction: ΔP ≈ elevation_m / 8.5
    elevation_m = elevation_ft * 0.3048
    slp_hpa = qnh_hpa + (elevation_m / 8.5)
    return slp_hpa


def encode_slp(slp_hpa: float | None) -> float | None:
    """
    Encode SLP for display in the station model.

    The WMO convention shows only the last three digits of (SLP × 10) mod 1000,
    e.g. 1013.2 hPa → 132.  MetPy's ``plot_parameter`` accepts the raw float
    (e.g. 132.0); the caller is responsible for formatting.

    Returns ``None`` if *slp_hpa* is None or not finite.
    """
    if slp_hpa is None or not np.isfinite(slp_hpa):
        return None
    return round((slp_hpa * 10) % 1000, 1)


# ── Wind components ───────────────────────────────────────────────────────────

def wind_components_kt(
    direction: float | None,
    speed: float | None,
    unit: str | None,
    is_variable: bool,
) -> tuple[float | None, float | None]:
    """
    Return (u, v) wind components in knots.

    Variable winds (no direction) are returned as (0, 0) so the barb renders
    as a calm symbol rather than being omitted.  Returns ``(None, None)`` when
    speed is not reported.
    """
    if speed is None:
        return None, None

    speed_kt = speed if not isinstance(unit, str) or unit.upper() != "MPS" else speed * 1.944

    if is_variable or direction is None:
        return 0.0, 0.0

    rad = np.radians(direction)
    u = -speed_kt * np.sin(rad)
    v = -speed_kt * np.cos(rad)
    return float(u), float(v)


# ── DataFrame preparation ─────────────────────────────────────────────────────

def prepare_plot_data(df: pd.DataFrame) -> pd.DataFrame:
    """
    Augment a METAR DataFrame (as returned by :func:`db.load_latest_metars`)
    with derived columns needed by :func:`render_station_plots`:

    Added columns:
        sky_oktas        int        WMO okta sky cover (0–8)
        wx_code          int/NaN    WMO present-weather code or NaN
        slp_hpa          float/NaN  Mean sea-level pressure in hPa
        slp_encoded      float/NaN  3-digit station-model SLP encoding
        wind_u_kt        float/NaN  Eastward wind component (kt)
        wind_v_kt        float/NaN  Northward wind component (kt)
        visibility_sm    float/NaN  Visibility in statute miles (CAVOK → 10.0)
    """
    out = df.copy()

    out["sky_oktas"] = out["RawSkyConditions"].apply(parse_sky_oktas)

    out["wx_code"] = out["RawWeatherPhenomena"].apply(parse_present_weather_code)

    out["slp_hpa"] = out.apply(
        lambda r: altimeter_to_slp_hpa(
            r["AltimeterValue"], r["AltimeterUnit"], r["ElevationFt"]
        ),
        axis=1,
    )
    out["slp_encoded"] = out["slp_hpa"].apply(encode_slp)

    components = out.apply(
        lambda r: wind_components_kt(
            r["WindDirection"], r["WindSpeed"], r["WindUnit"], bool(r["WindIsVariable"])
        ),
        axis=1,
        result_type="expand",
    )
    out["wind_u_kt"] = components[0]
    out["wind_v_kt"] = components[1]

    def _vis_sm(row) -> float | None:
        if row["VisibilityCavok"]:
            return 10.0
        if row["VisibilityStatuteMiles"] is not None:
            return row["VisibilityStatuteMiles"]
        if row["VisibilityM"] is not None:
            return row["VisibilityM"] / 1609.344
        return None

    out["visibility_sm"] = out.apply(_vis_sm, axis=1)

    return out


# ── Projection helpers ────────────────────────────────────────────────────────

def _inner_proj_limits(
    proj, extent: tuple[float, float, float, float], n: int = 200
) -> tuple[float, float, float, float]:
    """
    Return the largest axis-aligned rectangle (in projection metres) that fits
    entirely within the projected shape of a lat/lon bounding box.

    Sampling each edge densely handles the curvature of parallels and meridians
    in Lambert Conformal: parallels bow away from the equator so the top edge
    peaks at the centre; meridians converge at the pole so the left/right edges
    lean inward at the top.  Taking the tightest constraint from each edge gives
    an inner rectangle fully covered by data.

    Parameters
    ----------
    proj:
        Cartopy CRS instance used for the map axes.
    extent:
        ``(lon_min, lon_max, lat_min, lat_max)`` in degrees.
    n:
        Number of sample points per edge.  Default 200.

    Returns
    -------
    x_min, x_max, y_min, y_max in the projection's native units (metres).
    """
    lon_min, lon_max, lat_min, lat_max = extent
    lons_h = np.linspace(lon_min, lon_max, n)
    lats_v = np.linspace(lat_min, lat_max, n)

    top   = proj.transform_points(ccrs.PlateCarree(), lons_h,          np.full(n, lat_max))
    bot   = proj.transform_points(ccrs.PlateCarree(), lons_h,          np.full(n, lat_min))
    left  = proj.transform_points(ccrs.PlateCarree(), np.full(n, lon_min), lats_v)
    right = proj.transform_points(ccrs.PlateCarree(), np.full(n, lon_max), lats_v)

    return (
        left[:, 0].max(),   # left meridian leans right at top → tightest x_min
        right[:, 0].min(),  # right meridian leans left at top → tightest x_max
        bot[:, 1].max(),    # bottom parallel dips at centre → tightest y_min
        top[:, 1].min(),    # top parallel bows up at centre → tightest y_max
    )


# ── Rendering ─────────────────────────────────────────────────────────────────

def render_station_plots(
    df: pd.DataFrame,
    output_path: str,
    extent: tuple[float, float, float, float] | None = None,
    margin_deg: float = 0.5,
    dpi: int = 150,
) -> None:
    """
    Render a WMO standard station-plot map from METAR observations and save it.

    Parameters
    ----------
    df:
        DataFrame as returned by :func:`db.load_latest_metars`.  Must contain
        at minimum the columns produced by :func:`prepare_plot_data`.
    output_path:
        File path for the saved figure (PNG recommended).
    extent:
        Map extent as ``(lon_min, lon_max, lat_min, lat_max)``.  Defaults to
        the bounding box of the station data plus *margin_deg* on each side.
    margin_deg:
        Degrees of padding added around the station bounding box when *extent*
        is not provided.  Default 0.5.
    dpi:
        Output resolution.  Default 150.
    """
    plot_df = prepare_plot_data(df)

    # Drop rows with no position data
    plot_df = plot_df.dropna(subset=["Lat", "Lon"])
    if plot_df.empty:
        logger.warning("No plottable METAR data — nothing to render.")
        return

    if extent is None:
        extent = (
            plot_df["Lon"].min() - margin_deg,
            plot_df["Lon"].max() + margin_deg,
            plot_df["Lat"].min() - margin_deg,
            plot_df["Lat"].max() + margin_deg,
        )

    proj = ccrs.LambertConformal(
        central_longitude=(extent[0] + extent[1]) / 2,
        central_latitude=(extent[2] + extent[3]) / 2,
    )

    fig = plt.figure(figsize=(14, 10))
    ax = fig.add_subplot(1, 1, 1, projection=proj)
    x_min, x_max, y_min, y_max = _inner_proj_limits(proj, extent)
    ax.set_xlim(x_min, x_max)
    ax.set_ylim(y_min, y_max)

    # Map features
    ax.add_feature(cfeature.STATES.with_scale("50m"), linewidth=0.5, edgecolor="gray")
    ax.add_feature(cfeature.COASTLINE.with_scale("50m"), linewidth=0.6)
    ax.add_feature(cfeature.BORDERS.with_scale("50m"), linewidth=0.5)
    ax.add_feature(cfeature.LAND, facecolor="whitesmoke")
    ax.add_feature(cfeature.OCEAN, facecolor="lightcyan")
    ax.add_feature(cfeature.LAKES.with_scale("50m"), facecolor="lightcyan", linewidth=0.3)

    # Station plot
    stationplot = StationPlot(
        ax,
        plot_df["Lon"].values,
        plot_df["Lat"].values,
        clip_on=True,
        transform=ccrs.PlateCarree(),
        fontsize=8,
    )

    # Temperature (upper left, °C)
    stationplot.plot_parameter(
        "NW", plot_df["AirTemperatureCelsius"].values, color="darkred"
    )

    # Dew point (lower left, °C)
    stationplot.plot_parameter(
        "SW", plot_df["DewPointCelsius"].values, color="darkgreen"
    )

    # SLP encoded (upper right)
    stationplot.plot_parameter(
        "NE", plot_df["slp_encoded"].values, formatter=lambda v: f"{v:03.0f}"
    )

    # Visibility (lower right, SM)
    stationplot.plot_parameter(
        "SE", plot_df["visibility_sm"].values, formatter=lambda v: f"{v:.1f}"
    )

    # Wind barbs (knots) — pass full arrays; MetPy skips NaN entries
    stationplot.plot_barb(
        plot_df["wind_u_kt"].values,
        plot_df["wind_v_kt"].values,
    )

    # Sky cover symbol (centre circle)
    stationplot.plot_symbol("C", plot_df["sky_oktas"].values, sky_cover)

    # Present weather symbol (left of centre)
    wx_mask = plot_df["wx_code"].notna()
    if wx_mask.any():
        stationplot.plot_symbol(
            "W",
            plot_df.loc[wx_mask, "wx_code"].astype(int).values,
            current_weather,
            zorder=3,
        )

    # Station identifier (below plot)
    stationplot.plot_text(
        "S", plot_df["StationIcao"].values, fontsize=6, color="navy"
    )

    obs_time = pd.to_datetime(plot_df["ObservationUtc"]).max()
    ax.set_title(f"Surface Observations  —  {obs_time.strftime('%Y-%m-%d %H%MZ')}", fontsize=11)

    plt.tight_layout()
    plt.savefig(output_path, dpi=dpi, bbox_inches="tight")
    plt.close(fig)
    logger.info(f"Saved station plot → {output_path}")


# ── Entry point ───────────────────────────────────────────────────────────────

if __name__ == "__main__":
    from db import get_engine, load_latest_metars, load_output_dir

    engine = get_engine()
    df = load_latest_metars(engine)
    logger.info(f"Loaded {len(df)} METAR observations.")

    out_dir = load_output_dir()
    render_station_plots(df, str(out_dir / "station_plot.png"))
