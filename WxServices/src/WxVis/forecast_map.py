"""
forecast_map.py — GFS model forecast parameter maps.

Renders a map of GFS forecast parameters for a given forecast hour:
  - Temperature isopleths: red dashed contour lines
  - Dewpoint isopleths:    green dashed contour lines
  - MSLP isobars:          solid black labelled contour lines

The GFS data is already on a regular 0.25° lat/lon grid, so no spatial
interpolation is required — the DataFrame rows are pivoted directly into 2D
arrays for contouring.

Typical usage:
    from db import get_engine, load_gfs_grid
    from forecast_map import render_forecast_map

    engine = get_engine()
    df = load_gfs_grid(engine, forecast_hour=84)
    render_forecast_map(df, output_path="plots/forecast_f084.png")
"""

import numpy as np
import pandas as pd
import matplotlib.pyplot as plt
import cartopy.crs as ccrs
import cartopy.feature as cfeature
from scipy.ndimage import gaussian_filter, maximum_filter, minimum_filter
from scipy.ndimage import label as _connected_components
from datetime import timedelta


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


# ── Grid helpers ──────────────────────────────────────────────────────────────

def _to_grid(df: pd.DataFrame, value_col: str) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """
    Pivot a flat GFS DataFrame column into a 2D lat/lon grid.

    GFS lat/lon values are rounded to two decimal places before pivoting to
    avoid floating-point duplicates at the 0.25° grid spacing.

    Returns
    -------
    lons, lats, grid:
        1-D longitude and latitude arrays (ascending) and the corresponding
        2-D value array shaped ``(n_lats, n_lons)``.
    """
    tmp = df.copy()
    tmp["_lat"] = tmp["Lat"].round(2)
    tmp["_lon"] = tmp["Lon"].round(2)

    pivot = tmp.pivot_table(index="_lat", columns="_lon", values=value_col, aggfunc="mean")

    lats = pivot.index.values.astype(float)
    lons = pivot.columns.values.astype(float)
    grid = pivot.values.astype(float)

    return lons, lats, grid


def _smooth(grid: np.ndarray, sigma: float) -> np.ndarray:
    """Apply Gaussian smoothing while preserving the NaN mask."""
    nan_mask      = np.isnan(grid)
    filled        = np.where(nan_mask, 0.0, grid)
    weights       = (~nan_mask).astype(float)
    smooth_data   = gaussian_filter(filled,   sigma=sigma)
    smooth_weight = gaussian_filter(weights,  sigma=sigma)
    with np.errstate(invalid="ignore"):
        result = smooth_data / smooth_weight
    result[nan_mask] = np.nan
    return result


# ── Extrema annotation ───────────────────────────────────────────────────────

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
    transform=None,
) -> None:
    """
    Annotate local extrema in a 2-D gridded field with bold text labels.

    See :func:`synoptic_map._mark_extrema` for full documentation.
    """
    valid = ~np.isnan(data)
    if not valid.any():
        return

    filled = np.where(valid, data, float(np.nanmean(data)))

    local_max = (maximum_filter(filled, size=neighborhood) == filled) & valid
    local_min = (minimum_filter(filled, size=neighborhood) == filled) & valid

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


# ── Rendering ─────────────────────────────────────────────────────────────────

def render_forecast_map(
    df: pd.DataFrame,
    output_path: str,
    extent: tuple[float, float, float, float] | None = None,
    margin_deg: float = 0.5,
    isobar_interval_hpa: float = 4.0,
    isotherm_interval_c: float = 3.0,
    smooth_sigma: float = 1.5,
    dpi: int = 150,
) -> None:
    """
    Render a GFS forecast map and save it to *output_path*.

    Draws red temperature isopleths, green dewpoint isopleths, and black MSLP
    isobars as contour lines on a plain land/ocean background (no colour fill).

    Parameters
    ----------
    df:
        DataFrame as returned by :func:`db.load_gfs_grid`.  Must contain at
        minimum ``Lat``, ``Lon``, ``TmpC``, ``ModelRunUtc``, and
        ``ForecastHour`` columns.
    output_path:
        Destination file path (PNG recommended).
    extent:
        Map bounds as ``(lon_min, lon_max, lat_min, lat_max)``.  Defaults to
        the bounding box of the grid data plus *margin_deg* on each side.
    margin_deg:
        Degrees of padding added around the data bounding box when *extent*
        is not provided.  Default 0.5.
    isobar_interval_hpa:
        Isobar contour interval in hPa.  Default 4.
    isotherm_interval_c:
        Temperature and dewpoint contour interval in °C.  Default 5.
    smooth_sigma:
        Gaussian smoothing sigma in grid cells (0.25° each).  Default 1.5.
    dpi:
        Output resolution.  Default 150.
    """
    df = df.dropna(subset=["Lat", "Lon", "TmpC"])
    if df.empty:
        print("No GFS data with valid coordinates and temperature — nothing to render.")
        return

    # ── Map extent ────────────────────────────────────────────────────────────
    if extent is None:
        extent = (
            float(df["Lon"].min()) - margin_deg,
            float(df["Lon"].max()) + margin_deg,
            float(df["Lat"].min()) - margin_deg,
            float(df["Lat"].max()) + margin_deg,
        )

    proj = ccrs.LambertConformal(
        central_longitude=(extent[0] + extent[1]) / 2,
        central_latitude=(extent[2] + extent[3]) / 2,
    )

    # ── Build 2D grids ────────────────────────────────────────────────────────
    lons, lats, tmp_raw = _to_grid(df, "TmpC")
    tmp_smooth = _smooth(tmp_raw, sigma=smooth_sigma)
    lon_grid, lat_grid = np.meshgrid(lons, lats)

    has_dwp = df["DwpC"].notna().any()
    if has_dwp:
        dew_df = df.dropna(subset=["DwpC"])
        _, _, dew_raw = _to_grid(dew_df, "DwpC")
        dew_smooth = _smooth(dew_raw, sigma=smooth_sigma)

    has_slp = df["PrMslPa"].notna().any()
    if has_slp:
        slp_df = df.dropna(subset=["PrMslPa"])
        _, _, slp_raw = _to_grid(slp_df, "PrMslPa")
        slp_hpa = _smooth(slp_raw / 100.0, sigma=smooth_sigma)
    else:
        print("No MSLP data available for this forecast hour — isobars skipped.")

    # ── Figure setup ─────────────────────────────────────────────────────────
    fig = plt.figure(figsize=(16, 10))
    ax = fig.add_subplot(1, 1, 1, projection=proj)

    x_min, x_max, y_min, y_max = _inner_proj_limits(proj, extent)
    ax.set_xlim(x_min, x_max)
    ax.set_ylim(y_min, y_max)

    # ── Map features ─────────────────────────────────────────────────────────
    ax.add_feature(cfeature.OCEAN.with_scale("50m"),      facecolor="#cce5f0", zorder=0)
    ax.add_feature(cfeature.LAND.with_scale("50m"),       facecolor="#f0f0ee", zorder=0)
    ax.add_feature(cfeature.LAKES.with_scale("50m"),      facecolor="#cce5f0", linewidth=0.3, zorder=1)
    ax.add_feature(cfeature.COASTLINE.with_scale("50m"),  linewidth=0.7, zorder=4)
    ax.add_feature(cfeature.BORDERS.with_scale("50m"),    linewidth=0.6, zorder=4)
    ax.add_feature(cfeature.STATES.with_scale("50m"),     linewidth=0.4, edgecolor="#808080", zorder=4)

    # ── Temperature isopleths (red dashed) ───────────────────────────────────
    valid_tmp   = tmp_smooth[~np.isnan(tmp_smooth)]
    tmp_levels  = np.arange(
        np.floor(valid_tmp.min() / isotherm_interval_c) * isotherm_interval_c,
        np.ceil( valid_tmp.max() / isotherm_interval_c) * isotherm_interval_c + isotherm_interval_c,
        isotherm_interval_c,
    )
    ct = ax.contour(
        lon_grid, lat_grid, tmp_smooth,
        levels=tmp_levels,
        colors="red",
        linewidths=0.8,
        linestyles="dashed",
        transform=ccrs.PlateCarree(),
        zorder=2,
    )
    ax.clabel(ct, inline=True, fontsize=7, fmt="%d°C")
    _mark_extrema(ax, lon_grid, lat_grid, tmp_smooth, "W", "K",
                  high_color="darkred", low_color="steelblue", neighborhood=12,
                  transform=ccrs.PlateCarree())

    # ── Dewpoint isopleths (green dashed) ─────────────────────────────────────
    if has_dwp:
        valid_dew  = dew_smooth[~np.isnan(dew_smooth)]
        dew_levels = np.arange(
            np.floor(valid_dew.min() / isotherm_interval_c) * isotherm_interval_c,
            np.ceil( valid_dew.max() / isotherm_interval_c) * isotherm_interval_c + isotherm_interval_c,
            isotherm_interval_c,
        )
        cd = ax.contour(
            lon_grid, lat_grid, dew_smooth,
            levels=dew_levels,
            colors="green",
            linewidths=0.8,
            linestyles="dashed",
            transform=ccrs.PlateCarree(),
            zorder=2,
        )
        ax.clabel(cd, inline=True, fontsize=7, fmt="%d°C")

    # ── Isobars (black solid) ─────────────────────────────────────────────────
    if has_slp:
        valid_slp  = slp_hpa[~np.isnan(slp_hpa)]
        slp_levels = np.arange(
            np.floor(valid_slp.min() / isobar_interval_hpa) * isobar_interval_hpa,
            np.ceil( valid_slp.max() / isobar_interval_hpa) * isobar_interval_hpa + isobar_interval_hpa,
            isobar_interval_hpa,
        )
        cs = ax.contour(
            lon_grid, lat_grid, slp_hpa,
            levels=slp_levels,
            colors="black",
            linewidths=1.2,
            transform=ccrs.PlateCarree(),
            zorder=3,
        )
        ax.clabel(cs, inline=True, fontsize=8, fmt="%d")
        _mark_extrema(ax, lon_grid, lat_grid, slp_hpa, "H", "L",
                      high_color="navy", low_color="maroon", neighborhood=20,
                      transform=ccrs.PlateCarree())

    # ── Title ─────────────────────────────────────────────────────────────────
    model_run  = pd.to_datetime(df["ModelRunUtc"].iloc[0])
    fh         = int(df["ForecastHour"].iloc[0])
    valid_time = model_run + timedelta(hours=fh)
    ax.set_title(
        f"GFS Forecast  —  Init: {model_run.strftime('%Y-%m-%d %H%MZ')}  "
        f"Valid: {valid_time.strftime('%Y-%m-%d %H%MZ')}  (f{fh:03d})",
        fontsize=11, fontweight="bold",
    )

    plt.tight_layout()
    plt.savefig(output_path, dpi=dpi, bbox_inches="tight")
    plt.close(fig)
    print(f"Saved forecast map → {output_path}")


# ── Entry point ───────────────────────────────────────────────────────────────

if __name__ == "__main__":
    import argparse
    from db import get_engine, load_gfs_grid, load_output_dir

    parser = argparse.ArgumentParser(description="Render a GFS forecast parameter map.")
    parser.add_argument(
        "--fh", type=int, default=84,
        help="Forecast hour offset to render (default: 84)",
    )
    args = parser.parse_args()

    engine = get_engine()
    df     = load_gfs_grid(engine, forecast_hour=args.fh)

    if df.empty:
        print(f"No GFS data found for forecast hour {args.fh}.")
    else:
        print(f"Loaded {len(df)} grid points for f{args.fh:03d}.")
        out_dir = load_output_dir()
        render_forecast_map(df, str(out_dir / f"forecast_f{args.fh:03d}.png"))
