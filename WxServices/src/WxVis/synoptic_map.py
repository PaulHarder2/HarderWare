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

import numpy as np
import pandas as pd
import matplotlib.pyplot as plt
import cartopy.crs as ccrs
import cartopy.feature as cfeature
from scipy.ndimage import gaussian_filter, maximum_filter, minimum_filter
from scipy.ndimage import label as _connected_components

from metpy.plots import StationPlot
from metpy.plots.wx_symbols import sky_cover, current_weather
from metpy.calc import reduce_point_density
from metpy.interpolate import interpolate_to_grid

from metar_plot import prepare_plot_data
from logger import logger


# ── Projection helpers ────────────────────────────────────────────────────────

def _inner_proj_limits(
    proj, extent: tuple[float, float, float, float], n: int = 200
) -> tuple[float, float, float, float]:
    """
    Return the largest axis-aligned rectangle (in projection metres) that fits
    entirely within the projected shape of a lat/lon bounding box.

    See :func:`metar_plot._inner_proj_limits` for full documentation.
    """
    lon_min, lon_max, lat_min, lat_max = extent
    lons_h = np.linspace(lon_min, lon_max, n)
    lats_v = np.linspace(lat_min, lat_max, n)

    top   = proj.transform_points(ccrs.PlateCarree(), lons_h,              np.full(n, lat_max))
    bot   = proj.transform_points(ccrs.PlateCarree(), lons_h,              np.full(n, lat_min))
    left  = proj.transform_points(ccrs.PlateCarree(), np.full(n, lon_min), lats_v)
    right = proj.transform_points(ccrs.PlateCarree(), np.full(n, lon_max), lats_v)

    return (
        left[:, 0].max(),
        right[:, 0].min(),
        bot[:, 1].max(),
        top[:, 1].min(),
    )


# ── Default map extents ───────────────────────────────────────────────────────

# CONUS with a small buffer
CONUS_EXTENT = (-126.0, -65.0, 22.0, 50.0)

# South-central US (Texas + surrounding states)
SOUTH_CENTRAL_EXTENT = (-106.0, -88.0, 25.0, 38.0)


# ── Contour analysis ──────────────────────────────────────────────────────────

def _mark_extrema(
    ax,
    x2d: np.ndarray,
    y2d: np.ndarray,
    data: np.ndarray,
    high_label: str,
    low_label: str,
    high_color: str = "navy",
    low_color: str = "maroon",
    neighborhood: int = 16,
    min_depth: float = 0.0,
    transform=None,
) -> None:
    """
    Annotate local extrema in a 2-D gridded field with bold text labels.

    A cell qualifies as a local maximum (minimum) when its value equals the
    neighbourhood maximum (minimum) over a square window of *neighborhood*
    cells.  Connected blobs of equal extreme values — which arise when a
    pressure or temperature centre is broad and flat — are collapsed to their
    centroid so that each feature produces exactly one marker.  A border strip
    of half the neighbourhood width is excluded to suppress edge artifacts.

    Parameters
    ----------
    ax:
        Cartopy GeoAxes to annotate.
    x2d, y2d:
        2-D coordinate arrays with the same shape as *data*.
    data:
        2-D smoothed gridded field (NaN-masked at edges).
    high_label, low_label:
        Text strings placed at local maxima and minima (e.g. ``"H"``/``"L"``
        for pressure or ``"W"``/``"K"`` for temperature).
    high_color, low_color:
        Matplotlib colour strings for each label.
    neighborhood:
        Filter window size in grid cells.  Default 16.
    min_depth:
        Minimum prominence required for a feature to be labelled, in the same
        units as *data*.  A local maximum must rise at least *min_depth* above
        the neighbourhood minimum; a local minimum must fall at least
        *min_depth* below the neighbourhood maximum.  ``0.0`` (default) disables
        the check.
    transform:
        Cartopy CRS transform, or ``None`` when coordinates are already in
        the projection's native units (metres).
    """
    valid = ~np.isnan(data)
    if not valid.any():
        return

    filled = np.where(valid, data, float(np.nanmean(data)))

    max_filt = maximum_filter(filled, size=neighborhood)
    min_filt = minimum_filter(filled, size=neighborhood)

    local_max = (max_filt == filled) & valid
    local_min = (min_filt == filled) & valid

    if min_depth > 0.0:
        local_max &= (filled - min_filt) >= min_depth
        local_min &= (max_filt - filled) >= min_depth

    # Suppress markers within half a neighbourhood width of the border
    pad = neighborhood // 2
    interior = np.zeros(data.shape, dtype=bool)
    interior[pad:-pad, pad:-pad] = True
    local_max &= interior
    local_min &= interior

    txt_kw = dict(fontsize=16, fontweight="bold", ha="center", va="center", zorder=6)
    if transform is not None:
        txt_kw["transform"] = transform

    for mask, lbl, color in (
        (local_max, high_label, high_color),
        (local_min, low_label,  low_color),
    ):
        labeled_arr, n = _connected_components(mask)
        for idx in range(1, n + 1):
            ys, xs = np.where(labeled_arr == idx)
            iy = int(round(ys.mean()))
            ix = int(round(xs.mean()))
            ax.text(x2d[iy, ix], y2d[iy, ix], lbl, color=color, **txt_kw)


def _smooth_with_nans(data: np.ndarray, sigma: float) -> np.ndarray:
    """
    Apply Gaussian smoothing to *data*, correctly handling NaN gaps.

    A plain ``gaussian_filter`` propagates NaN values; this function weights
    each output cell by how many non-NaN input values contributed to it,
    which preserves the NaN mask while smoothing valid data cleanly.
    """
    nan_mask = np.isnan(data)
    filled   = np.where(nan_mask, 0.0, data)
    weights  = (~nan_mask).astype(float)
    smooth_data    = gaussian_filter(filled,   sigma=sigma)
    smooth_weights = gaussian_filter(weights,  sigma=sigma)
    with np.errstate(invalid="ignore"):
        result = smooth_data / smooth_weights
    result[nan_mask] = np.nan
    return result


def _add_analysis_contours(
    ax,
    plot_df: pd.DataFrame,
    extent: tuple[float, float, float, float],
    proj,
    isobar_interval_hpa: float = 4.0,
    isotherm_interval_c: float = 3.0,
    hres_km: float = 25.0,
    search_radius_km: float = 400.0,
    smooth_sigma: float = 2.5,
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

    slp_min = np.floor(np.nanmin(slp_smooth) / isobar_interval_hpa) * isobar_interval_hpa
    slp_max = np.ceil( np.nanmax(slp_smooth) / isobar_interval_hpa) * isobar_interval_hpa
    isobar_levels = np.arange(slp_min, slp_max + isobar_interval_hpa, isobar_interval_hpa)

    cs = ax.contour(
        grid_x, grid_y, slp_smooth,
        levels=isobar_levels,
        colors="black",
        linewidths=1.2,
        zorder=2,
    )
    ax.clabel(cs, inline=True, fontsize=8, fmt="%d")
    _mark_extrema(ax, grid_x, grid_y, slp_smooth, "H", "L",
                  high_color="navy", low_color="maroon", neighborhood=12, min_depth=1.0)

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
        grid_x, grid_y, tmp_smooth,
        levels=isotherm_levels,
        colors="red",
        linewidths=0.8,
        linestyles="dashed",
        zorder=2,
    )
    ax.clabel(ct, inline=True, fontsize=7, fmt="%d°C")
    _mark_extrema(ax, grid_x, grid_y, tmp_smooth, "W", "K",
                  high_color="darkred", low_color="steelblue", neighborhood=12)

    # ── Dewpoint isopleths ────────────────────────────────────────────────────
    dew_data = data.dropna(subset=["DewPointCelsius"])
    if len(dew_data) >= 6:
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
            grid_x, grid_y, dew_smooth,
            levels=dew_levels,
            colors="green",
            linewidths=0.8,
            linestyles="dashed",
            zorder=2,
        )
        ax.clabel(cd, inline=True, fontsize=7, fmt="%d°C")


# ── Rendering ─────────────────────────────────────────────────────────────────

def render_synoptic_map(
    df: pd.DataFrame,
    output_path: str,
    extent: tuple[float, float, float, float] | None = None,
    margin_deg: float = 0.5,
    point_density_km: float = 75.0,
    dpi: int = 150,
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

    proj = ccrs.LambertConformal(
        central_longitude=(extent[0] + extent[1]) / 2,
        central_latitude=(extent[2] + extent[3]) / 2,
    )

    # ── Figure setup ─────────────────────────────────────────────────────────
    fig = plt.figure(figsize=(11, 11))
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
    _add_analysis_contours(ax, plot_df, extent, proj)

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
        fontsize=9,
    )

    stationplot.plot_parameter("NW", station_df["AirTemperatureCelsius"].values, color="darkred")
    stationplot.plot_parameter("SW", station_df["DewPointCelsius"].values,       color="darkgreen")
    stationplot.plot_parameter(
        "NE", station_df["slp_encoded"].values, formatter=lambda v: f"{v:03.0f}"
    )
    stationplot.plot_parameter(
        "SE", station_df["visibility_sm"].values, formatter=lambda v: f"{v:.1f}"
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

    stationplot.plot_text("S", station_df["StationIcao"].values, fontsize=7, color="navy")

    obs_time = pd.to_datetime(station_df["ObservationUtc"]).max()
    ax.set_title(
        f"Synoptic Analysis  —  {obs_time.strftime('%Y-%m-%d %H%MZ')}",
        fontsize=13, fontweight="bold",
    )

    plt.tight_layout()
    plt.savefig(output_path, dpi=dpi)
    plt.close(fig)
    logger.info(f"Saved synoptic map → {output_path}")


# ── Entry point ───────────────────────────────────────────────────────────────

if __name__ == "__main__":
    import argparse
    from db import get_engine, load_latest_metars, load_output_dir

    parser = argparse.ArgumentParser(description="Render a synoptic analysis map.")
    parser.add_argument(
        "--extent", choices=["conus", "south_central"], default=None,
        help="Map extent preset (default: auto-fit to station data)",
    )
    parser.add_argument(
        "--density", type=float, default=75.0,
        help="Minimum station spacing in km (default: 75)",
    )
    args = parser.parse_args()

    extent_map = {
        "conus":         CONUS_EXTENT,
        "south_central": SOUTH_CENTRAL_EXTENT,
    }

    engine = get_engine()
    df = load_latest_metars(engine)
    logger.info(f"Loaded {len(df)} METAR observations.")

    obs_time = pd.to_datetime(df["ObservationUtc"]).max()
    ts       = obs_time.strftime("%Y%m%d_%H")

    out_dir = load_output_dir()
    label = args.extent or "auto"
    render_synoptic_map(
        df,
        output_path=str(out_dir / f"synoptic_{label}_{ts}.png"),
        extent=extent_map.get(args.extent),
        point_density_km=args.density,
    )
