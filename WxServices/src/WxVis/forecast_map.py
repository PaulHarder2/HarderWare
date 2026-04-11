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

import os
import numpy as np
import pandas as pd
import matplotlib.pyplot as plt
import cartopy.crs as ccrs
import cartopy.feature as cfeature
from datetime import timedelta

from metpy.plots import StationPlot
from metpy.plots.wx_symbols import sky_cover

from logger import logger
from map_utils import _inner_proj_limits, _mark_extrema, _smooth_with_nans, parse_extent, choose_projection


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


# ── Rendering ─────────────────────────────────────────────────────────────────

def render_forecast_map(
    df: pd.DataFrame,
    output_path: str,
    extent: tuple[float, float, float, float] | None = None,
    margin_deg: float = 0.5,
    isobar_interval_hpa: float = 4.0,
    isotherm_interval_c: float = 3.0,
    smooth_sigma: float = 1.5,
    barb_spacing_deg: float = 1.0,
    station_locs: pd.DataFrame | None = None,
    precip_threshold_mmhr: float = 0.1,
    dpi: int = 150,
) -> None:
    """
    Render a GFS forecast map and save it to *output_path*.

    Draws a semi-transparent green precipitation fill over areas where the
    GFS precipitation rate exceeds *precip_threshold_mmhr*, followed by red
    temperature isopleths, green dewpoint isopleths, and black MSLP isobars.

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
    barb_spacing_deg:
        Approximate spacing between wind barbs in degrees when *station_locs*
        is not provided.  The grid is subsampled so that one barb appears
        roughly every *barb_spacing_deg* degrees in both lat and lon.  Default 1.0.
    station_locs:
        Optional DataFrame with ``Lat`` and ``Lon`` columns (e.g. from
        :func:`db.load_latest_metars`).  When supplied, one wind barb is drawn
        at each station position using the GFS value at the nearest grid point,
        instead of the regular grid subsampling.  Default ``None``.
    precip_threshold_mmhr:
        Minimum precipitation rate in mm/hr required to shade a grid cell
        green.  Cells below this value are left unshaded.  Default 0.1 mm/hr.
    dpi:
        Output resolution.  Default 150.
    """
    df = df.dropna(subset=["Lat", "Lon", "TmpC"])
    if df.empty:
        logger.warning("No GFS data with valid coordinates and temperature — nothing to render.")
        return

    # ── Map extent ────────────────────────────────────────────────────────────
    if extent is None:
        extent = (
            float(df["Lon"].min()) - margin_deg,
            float(df["Lon"].max()) + margin_deg,
            float(df["Lat"].min()) - margin_deg,
            float(df["Lat"].max()) + margin_deg,
        )

    proj = choose_projection(extent)

    # ── Build 2D grids ────────────────────────────────────────────────────────
    lons, lats, tmp_raw = _to_grid(df, "TmpC")
    tmp_smooth = _smooth_with_nans(tmp_raw, sigma=smooth_sigma)
    lon_grid, lat_grid = np.meshgrid(lons, lats)

    has_dwp = df["DwpC"].notna().any()
    if has_dwp:
        dew_df = df.dropna(subset=["DwpC"])
        _, _, dew_raw = _to_grid(dew_df, "DwpC")
        dew_smooth = _smooth_with_nans(dew_raw, sigma=smooth_sigma)

    has_slp = df["PrMslPa"].notna().any()
    if has_slp:
        slp_df = df.dropna(subset=["PrMslPa"])
        _, _, slp_raw = _to_grid(slp_df, "PrMslPa")
        slp_hpa = _smooth_with_nans(slp_raw / 100.0, sigma=smooth_sigma)
    else:
        logger.warning("No MSLP data available for this forecast hour — isobars skipped.")

    has_prate = "PRateKgM2s" in df.columns and df["PRateKgM2s"].notna().any()
    if has_prate:
        _, _, prate_raw = _to_grid(df.dropna(subset=["PRateKgM2s"]), "PRateKgM2s")
        # Convert kg/m²/s → mm/hr and smooth to soften the 0.25° grid edges.
        prate_mm_hr = _smooth_with_nans(prate_raw * 3600.0, sigma=smooth_sigma)
        has_prate = np.nanmax(prate_mm_hr) >= precip_threshold_mmhr

    # ── Figure setup ─────────────────────────────────────────────────────────
    fig = plt.figure(figsize=(11, 11))
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

    # ── Precipitation shading (semi-transparent green fill) ──────────────────
    if has_prate:
        ax.contourf(
            lon_grid, lat_grid, prate_mm_hr,
            levels=[precip_threshold_mmhr, np.nanmax(prate_mm_hr) + 1.0],
            colors=["#66bb6a"],
            alpha=0.45,
            transform=ccrs.PlateCarree(),
            zorder=1,
        )

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
            colors="#00838f",  # teal — distinct from the green precip fill
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
                      high_color="navy", low_color="maroon", neighborhood=12, min_depth=1.0,
                      transform=ccrs.PlateCarree())

    # ── Wind / station model ──────────────────────────────────────────────────
    has_wind = "UGrdMs" in df.columns and "VGrdMs" in df.columns \
               and df["UGrdMs"].notna().any() and df["VGrdMs"].notna().any()
    if has_wind:
        wind_df = df.dropna(subset=["UGrdMs", "VGrdMs"])
        _, _, u_raw = _to_grid(wind_df, "UGrdMs")
        _, _, v_raw = _to_grid(wind_df, "VGrdMs")
        MS_TO_KT = 1.94384
        u_kt = u_raw * MS_TO_KT
        v_kt = v_raw * MS_TO_KT

    has_tcc = "TcdcPct" in df.columns and df["TcdcPct"].notna().any()
    if has_tcc:
        _, _, tcc_raw = _to_grid(df.dropna(subset=["TcdcPct"]), "TcdcPct")

    if station_locs is not None and not station_locs.empty:
        # ── Station model at METAR locations, GFS values ──────────────────────
        stn = station_locs.dropna(subset=["Lat", "Lon"])
        stn = stn[
            (stn["Lon"] >= extent[0]) & (stn["Lon"] <= extent[1]) &
            (stn["Lat"] >= extent[2]) & (stn["Lat"] <= extent[3])
        ]
        records = []
        for _, row in stn.iterrows():
            i_lat = int(np.argmin(np.abs(lats - row["Lat"])))
            i_lon = int(np.argmin(np.abs(lons - row["Lon"])))
            rec = {"lon": float(row["Lon"]), "lat": float(row["Lat"]),
                   "icao": row.get("StationIcao", "")}
            rec["tmp_c"]   = float(tmp_raw[i_lat, i_lon])
            rec["dwp_c"]   = float(dew_raw[i_lat, i_lon]) if has_dwp  else np.nan
            rec["u_kt"]    = float(u_kt[i_lat, i_lon])    if has_wind else np.nan
            rec["v_kt"]    = float(v_kt[i_lat, i_lon])    if has_wind else np.nan
            if has_slp:
                p = float(slp_raw[i_lat, i_lon]) / 100.0   # Pa → hPa
                rec["slp_enc"] = round(p * 10) % 1000 if not np.isnan(p) else np.nan
            else:
                rec["slp_enc"] = np.nan
            if has_tcc:
                t = float(tcc_raw[i_lat, i_lon])
                rec["oktas"] = int(np.clip(round(t / 100.0 * 8), 0, 8)) \
                               if not np.isnan(t) else -1
            else:
                rec["oktas"] = -1
            records.append(rec)

        if records:
            stn_df = pd.DataFrame(records)
            sp = StationPlot(
                ax,
                stn_df["lon"].values, stn_df["lat"].values,
                clip_on=True,
                transform=ccrs.PlateCarree(),
                fontsize=8,
            )
            sp.plot_parameter("NW", stn_df["tmp_c"].values,   color="darkred")
            if has_dwp:
                sp.plot_parameter("SW", stn_df["dwp_c"].values, color="darkgreen")
            if has_slp:
                sp.plot_parameter("NE", stn_df["slp_enc"].values,
                                  formatter=lambda v: f"{int(v):03d}")
            if has_wind:
                sp.plot_barb(stn_df["u_kt"].values, stn_df["v_kt"].values)
            if has_tcc:
                ok = stn_df["oktas"].values.astype(int)
                sp.plot_symbol("C", np.where(ok >= 0, ok, 0), sky_cover)
            if "icao" in stn_df.columns:
                sp.plot_text("SE", stn_df["icao"].values, fontsize=7, color="navy")

    elif has_wind:
        # ── Fallback: grid-subsampled barbs only ──────────────────────────────
        grid_spacing = float(np.median(np.diff(lons)))
        step = max(1, round(barb_spacing_deg / grid_spacing))
        sub_lon = lon_grid[::step, ::step]
        sub_lat = lat_grid[::step, ::step]
        sub_u   = u_kt[::step, ::step]
        sub_v   = v_kt[::step, ::step]
        valid_mask = ~(np.isnan(sub_u) | np.isnan(sub_v))
        if valid_mask.any():
            ax.barbs(
                sub_lon[valid_mask], sub_lat[valid_mask],
                sub_u[valid_mask],   sub_v[valid_mask],
                length=5, linewidth=0.6, color="dimgray",
                transform=ccrs.PlateCarree(), zorder=5,
            )

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
    # Re-apply viewport limits after tight_layout(): that call adjusts subplot
    # padding, which can shift the effective axes boundary and make borderline
    # labels appear outside the map.
    ax.set_xlim(x_min, x_max)
    ax.set_ylim(y_min, y_max)
    tmp_path = output_path + ".tmp"
    plt.savefig(tmp_path, dpi=dpi, format="png")
    plt.close(fig)
    os.replace(tmp_path, output_path)
    logger.info(f"Saved forecast map -> {output_path}")


# ── Entry point ───────────────────────────────────────────────────────────────

if __name__ == "__main__":
    import argparse
    from db import get_engine, load_gfs_grid, load_latest_metars, load_output_dir

    parser = argparse.ArgumentParser(description="Render a GFS forecast parameter map.")
    parser.add_argument(
        "--fh", type=int, default=84,
        help="Forecast hour offset to render (default: 84)",
    )
    parser.add_argument(
        "--run", type=str, required=True,
        help="GFS model run timestamp in YYYYMMDD_HH format (e.g. 20260402_18)",
    )
    parser.add_argument(
        "--extent", default=None,
        help="Map extent: preset name (conus, south_central) or W,E,S,N coordinates (default: auto-fit to GFS data bounds)",
    )
    args = parser.parse_args()

    extent = parse_extent(args.extent)

    from datetime import datetime
    model_run = datetime.strptime(args.run, "%Y%m%d_%H")

    engine = get_engine()
    df     = load_gfs_grid(engine, forecast_hour=args.fh, model_run=model_run)

    if df.empty:
        logger.error(f"No GFS data found for run {args.run} forecast hour {args.fh}.")
    else:
        logger.info(f"Loaded {len(df)} grid points for f{args.fh:03d}.")
        metar_df = load_latest_metars(engine)
        station_locs = metar_df[["Lat", "Lon", "StationIcao"]].dropna() if not metar_df.empty else None
        if station_locs is not None:
            logger.info(f"Loaded {len(station_locs)} METAR station locations for forecast station models.")
        out_dir = load_output_dir()
        render_forecast_map(
            df,
            str(out_dir / f"forecast_{args.run}_f{args.fh:03d}.png"),
            extent=extent,
            station_locs=station_locs,
        )
