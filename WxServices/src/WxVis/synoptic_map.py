"""
synoptic_map.py — Regional synoptic analysis map.

Plots all available METAR station observations on a regional map using MetPy's
point density reduction to prevent symbol overlap at the displayed scale.
Overlays isobars (4 hPa intervals) and isotherms (5°C intervals) derived from
Barnes interpolation of the station observations.

Typical usage:
    from db import get_engine, load_latest_metars
    from synoptic_map import render_synoptic_map

    engine = get_engine()
    df = load_latest_metars(engine)
    render_synoptic_map(df, output_path="plots/synoptic.png")
"""

import os
import re
import numpy as np
import pandas as pd
import matplotlib.pyplot as plt
import cartopy.crs as ccrs
import cartopy.feature as cfeature
from metpy.plots import StationPlot
from metpy.plots.wx_symbols import sky_cover, current_weather
from metpy.calc import reduce_point_density
from metpy.interpolate import interpolate_to_grid

from logger import logger
from map_utils import _inner_proj_limits, _mark_extrema, _smooth_with_nans, parse_extent, choose_projection


# ── METAR data preparation ───────────────────────────────────────────────────

_COVERAGE_OKTAS: dict[str, int] = {
    "SKC": 0, "CLR": 0, "NCD": 0, "NSC": 0,
    "FEW": 1, "SCT": 3, "BKN": 6, "OVC": 8, "VV": 8,
}
_COVERAGE_ORDER = ["OVC", "VV", "BKN", "SCT", "FEW", "SKC", "CLR", "NCD", "NSC"]

_WX_CODE_MAP: list[tuple[re.Pattern, int]] = [
    (re.compile(r"\bTS\b"),      95),
    (re.compile(r"\bTS.*GR\b"),  99),
    (re.compile(r"\bFZRA\b"),    66),
    (re.compile(r"\bFZDZ\b"),    56),
    (re.compile(r"\+SN\b"),      75),
    (re.compile(r"\bSN\b"),      71),
    (re.compile(r"\bSG\b"),      77),
    (re.compile(r"\+RA\b"),      65),
    (re.compile(r"-RA\b"),       61),
    (re.compile(r"\bRA\b"),      63),
    (re.compile(r"\bDZ\b"),      51),
    (re.compile(r"\bFG\b"),      45),
    (re.compile(r"\bBR\b"),      10),
    (re.compile(r"\bHZ\b"),       5),
    (re.compile(r"\bFU\b"),       4),
    (re.compile(r"\bDU\b"),       6),
    (re.compile(r"\bSA\b"),       7),
    (re.compile(r"\bSS\b"),      34),
    (re.compile(r"\bDS\b"),      34),
]


def _parse_sky_oktas(raw_sky: str | None) -> int:
    """Return the WMO okta value (0–8) for the most significant sky layer."""
    if not isinstance(raw_sky, str) or not raw_sky:
        return 0
    tokens = raw_sky.upper().split()
    for cover_token in _COVERAGE_ORDER:
        for token in tokens:
            if token.startswith(cover_token):
                return _COVERAGE_OKTAS[cover_token]
    return 0


def _parse_present_weather_code(raw_wx: str | None) -> int | None:
    """Return a WMO present-weather code (0–99) or None."""
    if not isinstance(raw_wx, str) or not raw_wx:
        return None
    wx = raw_wx.upper()
    for pattern, code in _WX_CODE_MAP:
        if pattern.search(wx):
            return code
    return None


def _altimeter_to_slp_hpa(altimeter_value: float | None,
                           altimeter_unit: str | None,
                           elevation_ft: float | None) -> float | None:
    """Convert a METAR altimeter setting to approximate MSLP in hPa."""
    if altimeter_value is None:
        return None
    qnh_hpa = altimeter_value * 33.8639 \
        if isinstance(altimeter_unit, str) and altimeter_unit.upper() == "INHG" \
        else float(altimeter_value)
    if elevation_ft is None or elevation_ft < 1.0:
        return qnh_hpa
    return qnh_hpa + (elevation_ft * 0.3048 / 8.5)


def _encode_slp(slp_hpa: float | None) -> float | None:
    """Encode SLP to the 3-digit station-model format (e.g. 1013.2 → 132.0)."""
    if slp_hpa is None or not np.isfinite(slp_hpa):
        return None
    return round((slp_hpa * 10) % 1000, 1)


def _wind_components_kt(
    direction: float | None,
    speed: float | None,
    unit: str | None,
    is_variable: bool,
) -> tuple[float | None, float | None]:
    """Return (u, v) wind components in knots."""
    if speed is None:
        return None, None
    speed_kt = speed if not isinstance(unit, str) or unit.upper() != "MPS" \
        else speed * 1.944
    if is_variable or direction is None:
        return 0.0, 0.0
    rad = np.radians(direction)
    return float(-speed_kt * np.sin(rad)), float(-speed_kt * np.cos(rad))


def prepare_plot_data(df: pd.DataFrame) -> pd.DataFrame:
    """
    Augment a METAR DataFrame with derived columns needed for rendering:
    ``sky_oktas``, ``wx_code``, ``slp_hpa``, ``slp_encoded``,
    ``wind_u_kt``, ``wind_v_kt``, ``visibility_sm``.
    """
    out = df.copy()
    out["sky_oktas"]   = out["RawSkyConditions"].apply(_parse_sky_oktas)
    out["wx_code"]     = out["RawWeatherPhenomena"].apply(_parse_present_weather_code)
    out["slp_hpa"]     = out.apply(
        lambda r: _altimeter_to_slp_hpa(r["AltimeterValue"], r["AltimeterUnit"], r["ElevationFt"]),
        axis=1,
    )
    out["slp_encoded"] = out["slp_hpa"].apply(_encode_slp)
    components = out.apply(
        lambda r: _wind_components_kt(
            r["WindDirection"], r["WindSpeed"], r["WindUnit"], bool(r["WindIsVariable"])
        ),
        axis=1, result_type="expand",
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


# ── Contour analysis ──────────────────────────────────────────────────────────

def _add_analysis_contours(
    ax,
    plot_df: pd.DataFrame,
    extent: tuple[float, float, float, float],
    proj,
    isobar_interval_hpa: float = 4.0,
    isotherm_interval_c: float = 3.0,
    draw_dewpoint: bool = True,
    hres_km: float = 25.0,
    search_radius_km: float = 400.0,
    smooth_sigma: float = 2.5,
    font_scale: float = 1.0,
) -> None:
    """
    Interpolate station SLP and temperature to a regular grid and overlay
    isobars and isotherms on *ax*.

    Uses Barnes interpolation (MetPy :func:`interpolate_to_grid`) in the
    projection's native coordinate system (metres), followed by Gaussian
    smoothing.  Contours are drawn in projection coordinates so no Cartopy
    transform is required.

    Parameters
    ----------
    ax:
        Cartopy GeoAxes to draw onto.
    plot_df:
        Full (non-density-reduced) prepared METAR DataFrame, including the
        ``slp_hpa`` and ``AirTemperatureCelsius`` columns added by
        :func:`prepare_plot_data`.
    extent:
        Map bounds as ``(lon_min, lon_max, lat_min, lat_max)``.
    proj:
        The Cartopy projection instance used by *ax*.
    isobar_interval_hpa:
        Isobar contour interval in hPa.  Default 4.
    isotherm_interval_c:
        Isotherm contour interval in °C.  Default 5.
    hres_km:
        Barnes interpolation output grid resolution in kilometres.  Default 25.
    search_radius_km:
        Barnes interpolation search radius in kilometres.  Default 400.
    smooth_sigma:
        Gaussian smoothing sigma (grid cells).  Default 2.5.
    """
    # Use all stations with valid coordinates and required fields
    buf = 3.0
    data = plot_df.dropna(subset=["Lat", "Lon", "slp_hpa", "AirTemperatureCelsius"])
    data = data[
        (data["Lon"] >= extent[0] - buf) & (data["Lon"] <= extent[1] + buf) &
        (data["Lat"] >= extent[2] - buf) & (data["Lat"] <= extent[3] + buf)
    ]

    if len(data) < 6:
        logger.warning("Not enough stations with valid SLP/temperature for contour analysis.")
        return

    # Transform station positions to projection coordinates (metres)
    proj_pts = proj.transform_points(
        ccrs.PlateCarree(), data["Lon"].values, data["Lat"].values
    )
    x_stn = proj_pts[:, 0]
    y_stn = proj_pts[:, 1]

    hres_m          = hres_km * 1000.0
    search_radius_m = search_radius_km * 1000.0

    # ── Isobars ───────────────────────────────────────────────────────────────
    grid_x, grid_y, slp_raw = interpolate_to_grid(
        x_stn, y_stn, data["slp_hpa"].values,
        interp_type="barnes",
        minimum_neighbors=3,
        hres=hres_m,
        search_radius=search_radius_m,
    )
    slp_smooth = _smooth_with_nans(slp_raw, sigma=smooth_sigma)

    # Convert the Barnes grid from projection metres to lat/lon so that Cartopy
    # clips contours to the inner viewport, matching forecast_map rendering.
    _pc  = ccrs.PlateCarree()
    _pts = _pc.transform_points(proj, grid_x.ravel(), grid_y.ravel())
    grid_lon = _pts[:, 0].reshape(grid_x.shape)
    grid_lat = _pts[:, 1].reshape(grid_x.shape)

    slp_min = np.floor(np.nanmin(slp_smooth) / isobar_interval_hpa) * isobar_interval_hpa
    slp_max = np.ceil( np.nanmax(slp_smooth) / isobar_interval_hpa) * isobar_interval_hpa
    isobar_levels = np.arange(slp_min, slp_max + isobar_interval_hpa, isobar_interval_hpa)

    cs = ax.contour(
        grid_lon, grid_lat, slp_smooth,
        levels=isobar_levels,
        colors="black",
        linewidths=0.8 * font_scale,
        transform=_pc,
        zorder=2,
    )
    ax.clabel(cs, inline=True, fontsize=int(8 * font_scale), fmt="%d")
    _mark_extrema(ax, grid_lon, grid_lat, slp_smooth, "H", "L",
                  high_color="navy", low_color="maroon", neighborhood=12, min_depth=1.0,
                  transform=_pc, font_scale=font_scale)

    # ── Isotherms ─────────────────────────────────────────────────────────────
    _, _, tmp_raw = interpolate_to_grid(
        x_stn, y_stn, data["AirTemperatureCelsius"].values,
        interp_type="barnes",
        minimum_neighbors=3,
        hres=hres_m,
        search_radius=search_radius_m,
    )
    tmp_smooth = _smooth_with_nans(tmp_raw, sigma=smooth_sigma)

    tmp_min = np.floor(np.nanmin(tmp_smooth) / isotherm_interval_c) * isotherm_interval_c
    tmp_max = np.ceil( np.nanmax(tmp_smooth) / isotherm_interval_c) * isotherm_interval_c
    isotherm_levels = np.arange(tmp_min, tmp_max + isotherm_interval_c, isotherm_interval_c)

    ct = ax.contour(
        grid_lon, grid_lat, tmp_smooth,
        levels=isotherm_levels,
        colors="red",
        linewidths=0.5 * font_scale,
        linestyles="dashed",
        transform=_pc,
        zorder=2,
    )
    ax.clabel(ct, inline=True, fontsize=int(7 * font_scale), fmt="%d°C")
    _mark_extrema(ax, grid_lon, grid_lat, tmp_smooth, "W", "K",
                  high_color="darkred", low_color="steelblue", neighborhood=12,
                  transform=_pc, font_scale=font_scale)

    # ── Dewpoint isopleths ────────────────────────────────────────────────────
    dew_data = data.dropna(subset=["DewPointCelsius"])
    if draw_dewpoint and len(dew_data) >= 6:
        dew_proj = proj.transform_points(
            ccrs.PlateCarree(), dew_data["Lon"].values, dew_data["Lat"].values
        )
        _, _, dew_raw = interpolate_to_grid(
            dew_proj[:, 0], dew_proj[:, 1], dew_data["DewPointCelsius"].values,
            interp_type="barnes",
            minimum_neighbors=3,
            hres=hres_m,
            search_radius=search_radius_m,
        )
        dew_smooth = _smooth_with_nans(dew_raw, sigma=smooth_sigma)

        dew_min = np.floor(np.nanmin(dew_smooth) / isotherm_interval_c) * isotherm_interval_c
        dew_max = np.ceil( np.nanmax(dew_smooth) / isotherm_interval_c) * isotherm_interval_c
        dew_levels = np.arange(dew_min, dew_max + isotherm_interval_c, isotherm_interval_c)

        cd = ax.contour(
            grid_lon, grid_lat, dew_smooth,
            levels=dew_levels,
            colors="green",
            linewidths=0.8 * font_scale,
            linestyles="dashed",
            transform=_pc,
            zorder=2,
        )
        ax.clabel(cd, inline=True, fontsize=int(7 * font_scale), fmt="%d°C")


# ── Rendering ─────────────────────────────────────────────────────────────────

def render_synoptic_map(
    df: pd.DataFrame,
    output_path: str,
    extent: tuple[float, float, float, float] | None = None,
    margin_deg: float = 0.5,
    point_density_km: float = 75.0,
    dpi: int = 150,
    figsize_in: float = 11.0,
    font_scale: float = 1.0,
) -> None:
    """
    Render a regional synoptic analysis map and save it to *output_path*.

    Isobars and isotherms are derived by Barnes interpolation of all available
    station observations and drawn beneath the station model symbols.
    Stations are thinned with MetPy's :func:`reduce_point_density` so that no
    two plotted stations are closer than *point_density_km* kilometres in the
    projected coordinate system, preventing symbol overlap.

    Parameters
    ----------
    df:
        DataFrame as returned by :func:`db.load_latest_metars`.
    output_path:
        Destination file path (PNG recommended).
    extent:
        Map bounds as ``(lon_min, lon_max, lat_min, lat_max)``.  Defaults to
        the bounding box of the station data plus *margin_deg* on each side.
        Use :data:`CONUS_EXTENT` or :data:`SOUTH_CENTRAL_EXTENT` for fixed presets.
    margin_deg:
        Degrees of padding added around the station bounding box when *extent*
        is not provided.  Default 0.5.
    point_density_km:
        Minimum spacing between plotted stations in kilometres.
        Increase to thin more aggressively; decrease to show more stations.
        Default 75 km works well for a regional map; use 150–200 km for CONUS scale.
    dpi:
        Output resolution.  Default 150.
    """
    plot_df = prepare_plot_data(df)
    plot_df = plot_df.dropna(subset=["Lat", "Lon"])

    if plot_df.empty:
        logger.warning("No stations with valid coordinates — nothing to render.")
        return

    if extent is None:
        extent = (
            plot_df["Lon"].min() - margin_deg,
            plot_df["Lon"].max() + margin_deg,
            plot_df["Lat"].min() - margin_deg,
            plot_df["Lat"].max() + margin_deg,
        )
    else:
        # Restrict to the supplied extent with a small buffer
        buf = 2.0
        in_extent = (
            (plot_df["Lon"] >= extent[0] - buf) & (plot_df["Lon"] <= extent[1] + buf) &
            (plot_df["Lat"] >= extent[2] - buf) & (plot_df["Lat"] <= extent[3] + buf)
        )
        plot_df = plot_df[in_extent].reset_index(drop=True)

    if plot_df.empty:
        logger.warning("No stations within the map extent — nothing to render.")
        return

    proj = choose_projection(extent)

    # ── Figure setup ─────────────────────────────────────────────────────────
    fig = plt.figure(figsize=(figsize_in, figsize_in))
    ax = fig.add_subplot(1, 1, 1, projection=proj)
    x_min, x_max, y_min, y_max = _inner_proj_limits(proj, extent)
    ax.set_xlim(x_min, x_max)
    ax.set_ylim(y_min, y_max)

    ax.add_feature(cfeature.LAND.with_scale("50m"),       facecolor="#f5f5f0")
    ax.add_feature(cfeature.OCEAN.with_scale("50m"),      facecolor="#cce5f0")
    ax.add_feature(cfeature.COASTLINE.with_scale("50m"),  linewidth=0.7, edgecolor="black")
    ax.add_feature(cfeature.BORDERS.with_scale("50m"),    linewidth=0.6, edgecolor="black")
    ax.add_feature(cfeature.STATES.with_scale("50m"),     linewidth=0.4, edgecolor="#808080")
    ax.add_feature(cfeature.LAKES.with_scale("50m"),      facecolor="#cce5f0", linewidth=0.3)
    ax.add_feature(cfeature.RIVERS.with_scale("50m"),     linewidth=0.3, edgecolor="#9ec8d8")

    # ── Isobars and isotherms (drawn first, beneath station symbols) ──────────
    # Use uniform contour intervals across zoom levels.  Higher zooms have more
    # canvas per line, so they appear less crowded naturally.  Dewpoint isopleths
    # are suppressed at z1 (font_scale == 1) to reduce visual clutter.
    _add_analysis_contours(
        ax, plot_df, extent, proj,
        isobar_interval_hpa=8.0,
        isotherm_interval_c=5.0,
        smooth_sigma=5.0,
        draw_dewpoint=(font_scale > 1.0),
        font_scale=font_scale,
    )

    # ── Apply density reduction for station model plotting ────────────────────
    proj_coords = proj.transform_points(
        ccrs.PlateCarree(),
        plot_df["Lon"].values,
        plot_df["Lat"].values,
    )
    keep = reduce_point_density(
        proj_coords[:, :2],
        point_density_km * 1000.0,
    )
    station_df = plot_df[keep].reset_index(drop=True)
    logger.info(f"Rendering {len(station_df)} stations after density reduction.")

    # ── Station plot ─────────────────────────────────────────────────────────
    stationplot = StationPlot(
        ax,
        station_df["Lon"].values,
        station_df["Lat"].values,
        clip_on=True,
        transform=ccrs.PlateCarree(),
        fontsize=int(9 * font_scale),
    )

    stationplot.plot_parameter("NW", station_df["AirTemperatureCelsius"].values, color="darkred")
    stationplot.plot_parameter("SW", station_df["DewPointCelsius"].values,       color="darkgreen")
    stationplot.plot_parameter(
        "NE", station_df["slp_encoded"].values, formatter=lambda v: f"{v:03.0f}"
    )

    stationplot.plot_barb(
        station_df["wind_u_kt"].values,
        station_df["wind_v_kt"].values,
    )

    stationplot.plot_symbol("C", station_df["sky_oktas"].values, sky_cover)

    wx_mask = station_df["wx_code"].notna()
    if wx_mask.any():
        stationplot.plot_symbol(
            "W",
            station_df.loc[wx_mask, "wx_code"].astype(int).values,
            current_weather,
            zorder=3,
        )

    stationplot.plot_text("SE", station_df["StationIcao"].values, fontsize=int(7 * font_scale), color="navy")

    obs_time = pd.to_datetime(station_df["ObservationUtc"]).max()
    ax.set_title(
        f"Synoptic Analysis  —  {obs_time.strftime('%Y-%m-%d %H%MZ')}",
        fontsize=int(11 * font_scale), fontweight="bold",
    )

    plt.tight_layout()
    # Re-apply viewport limits after tight_layout(): that call adjusts subplot
    # padding, which can shift the effective axes boundary and make borderline
    # labels appear outside the map.
    ax.set_xlim(x_min, x_max)
    ax.set_ylim(y_min, y_max)
    tmp_path = output_path + ".tmp"
    plt.savefig(tmp_path, dpi=dpi, format="png")
    plt.close(fig)
    os.replace(tmp_path, output_path)
    logger.info(f"Saved synoptic map -> {output_path}")


# ── Entry point ───────────────────────────────────────────────────────────────

if __name__ == "__main__":
    import argparse
    from db import get_engine, load_latest_metars, load_output_dir

    parser = argparse.ArgumentParser(description="Render a synoptic analysis map.")
    parser.add_argument(
        "--extent", default=None,
        help="Map extent: preset name (conus, south_central) or W,E,S,N coordinates (default: auto-fit to station data)",
    )
    parser.add_argument(
        "--density", type=float, default=None,
        help="Minimum station spacing in km (default: derived from zoom level)",
    )
    parser.add_argument(
        "--zoom-level", type=int, default=1,
        help="Zoom level (1 = base, each successive level doubles the scale factor; default: 1)",
    )
    args = parser.parse_args()

    zoom = max(1, args.zoom_level)
    scale_factor = 2 ** (zoom - 1)
    figsize_in   = 11.0 * scale_factor
    density_km   = args.density if args.density is not None else 150.0 / scale_factor
    font_scale   = scale_factor ** 0.5   # sqrt: z1=1.0, z2=1.41, z3=2.0
    dpi          = 150 if zoom == 1 else 100

    extent = parse_extent(args.extent)

    engine = get_engine()
    df = load_latest_metars(engine)
    logger.info(f"Loaded {len(df)} METAR observations.")

    obs_time = pd.to_datetime(df["ObservationUtc"]).max()
    ts       = obs_time.strftime("%Y%m%d_%H")

    out_dir = load_output_dir()
    label = args.extent or "auto"
    render_synoptic_map(
        df,
        output_path=str(out_dir / f"synoptic_{label}_{ts}_z{zoom}.png"),
        extent=extent,
        point_density_km=density_km,
        dpi=dpi,
        figsize_in=figsize_in,
        font_scale=font_scale,
    )
