"""
meteogram.py — GFS point-forecast meteogram.

Renders a meteogram for a specified lat/lon location interpolated from the GFS
forecast grid.  Two PNG files are produced per invocation:

  - A 24-hour meteogram covering the first 24 forecast hours.
  - A full-period meteogram covering all available forecast hours.

Layout
------
The figure is divided into two vertically stacked panels sharing a common
time axis:

  Top panel (1/3 height):   wind barbs (always in knots).
  Bottom panel (2/3 height): temperature line (black, left axis) and
                             relative humidity line (green, right axis).

Bold vertical lines mark every UTC midnight boundary.  The top panel shows
the day name and date centred in each day's segment.  The x-axis has ticks
at every six hours, labelled HH:MM.

Typical usage::

    python meteogram.py \\
        --lat    29.97 \\
        --lon   -95.34 \\
        --icao   KDWH \\
        --locality "The Woodlands" \\
        --temp-unit F \\
        --out-24h  C:/HarderWare/plots/meteogram_KDWH_24h.png \\
        --out-full C:/HarderWare/plots/meteogram_KDWH_full.png

    # With an explicit model run:
    python meteogram.py \\
        --run    20260404_00 \\
        --lat    29.97 --lon -95.34 \\
        --icao   KDWH --locality "The Woodlands" --temp-unit F \\
        --out-24h  C:/HarderWare/plots/meteogram_20260404_00_KDWH_24h.png \\
        --out-full C:/HarderWare/plots/meteogram_20260404_00_KDWH_full.png
"""

import os
import argparse
from datetime import datetime, timedelta, timezone
from zoneinfo import ZoneInfo

import numpy as np
import pandas as pd
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.transforms import blended_transform_factory

from db import get_engine, load_gfs_nearby, load_output_dir
from logger import logger


# ── Physics helpers ───────────────────────────────────────────────────────────

def _compute_rh(tmp_c: np.ndarray, dwp_c: np.ndarray) -> np.ndarray:
    """
    Relative humidity (%) from temperature and dewpoint (both °C) using the
    Magnus formula.  Returns NaN wherever either input is NaN; clamps to
    [0, 100].
    """
    with np.errstate(invalid="ignore"):
        e_td = np.exp(17.625 * dwp_c / (243.04 + dwp_c))
        e_t  = np.exp(17.625 * tmp_c  / (243.04 + tmp_c))
        rh   = 100.0 * e_td / e_t
    return np.clip(rh, 0.0, 100.0)


# ── Point extraction ──────────────────────────────────────────────────────────

def _nearest_point_series(df: pd.DataFrame, lat: float, lon: float) -> pd.DataFrame:
    """
    For each forecast hour in *df*, select the single grid point nearest to
    (*lat*, *lon*) using Euclidean distance in lat/lon space.

    Returns a DataFrame with one row per forecast hour, sorted ascending.
    """
    df = df.copy()
    df["_dist2"] = (df["Lat"] - lat) ** 2 + (df["Lon"] - lon) ** 2
    idx    = df.groupby("ForecastHour")["_dist2"].idxmin()
    result = df.loc[idx].drop(columns=["_dist2"])
    return result.sort_values("ForecastHour").reset_index(drop=True)


# ── Rendering ─────────────────────────────────────────────────────────────────

def render_meteogram(
    series_df: pd.DataFrame,
    output_path: str,
    title: str,
    temp_unit: str = "F",
    tz_name: str = "UTC",
    max_hours: int | None = None,
    fig_width: float = 12.0,
    dpi: int = 100,
) -> None:
    """
    Render a meteogram from a time-series DataFrame and save it to
    *output_path*.

    Parameters
    ----------
    series_df:
        DataFrame as returned by :func:`_nearest_point_series`.  Must contain
        ``ModelRunUtc``, ``ForecastHour``, ``TmpC``, ``DwpC``,
        ``UGrdMs``, and ``VGrdMs`` columns.
    output_path:
        Destination file path (PNG).
    title:
        Chart title displayed above the wind panel (e.g. "The Woodlands (°F)").
    temp_unit:
        ``"F"`` for Fahrenheit (default) or ``"C"`` for Celsius.
    tz_name:
        IANA timezone name for the time axis (e.g. ``"America/Chicago"``).
        Day boundaries and tick labels are shown in this local time.
        Defaults to ``"UTC"``.
    max_hours:
        When set, only forecast hours ``<= max_hours`` are plotted.
    fig_width:
        Figure width in inches.  Height is fixed at 3.0".
    dpi:
        Output resolution.  Default 100.
    """
    df = series_df.copy()
    if max_hours is not None:
        df = df[df["ForecastHour"] <= max_hours]

    if df.empty:
        logger.warning(f"render_meteogram: no data for '{title}' — skipping.")
        return

    model_run = pd.to_datetime(df["ModelRunUtc"].iloc[0])
    fh_vals   = df["ForecastHour"].values.astype(float)

    # ── Derived quantities ────────────────────────────────────────────────────
    if temp_unit == "F":
        tmp_display = df["TmpC"].values * 9.0 / 5.0 + 32.0
        t_label     = "T (°F)"
    else:
        tmp_display = df["TmpC"].values.copy()
        t_label     = "T (°C)"

    rh = _compute_rh(df["TmpC"].values, df["DwpC"].values)

    MS_TO_KT = 1.94384
    u_kt = df["UGrdMs"].values * MS_TO_KT
    v_kt = df["VGrdMs"].values * MS_TO_KT

    # ── Valid times in local timezone ─────────────────────────────────────────
    local_tz    = ZoneInfo(tz_name)
    utc_tz      = timezone.utc
    valid_times = [
        (model_run.replace(tzinfo=utc_tz) + timedelta(hours=int(fh))).astimezone(local_tz)
        for fh in fh_vals
    ]

    # ── Figure ────────────────────────────────────────────────────────────────
    fig, (ax_wind, ax_data) = plt.subplots(
        2, 1, figsize=(fig_width, 3.0),
        gridspec_kw={"height_ratios": [1, 2]},
        sharex=True,
    )
    fig.patch.set_facecolor("white")
    fig.subplots_adjust(hspace=0.0, left=0.06, right=0.94, top=0.88, bottom=0.14)

    # ── Wind panel ────────────────────────────────────────────────────────────
    ax_wind.set_ylim(-1.5, 1.5)
    ax_wind.axhline(0, color="#aaaaaa", linewidth=0.5, zorder=1)

    # Thin barbs if too dense (target at most 1 per ~0.18")
    n_pts = len(df)
    pts_per_inch = n_pts / fig_width
    step = max(1, int(pts_per_inch * 0.18))
    bi   = slice(None, None, step)

    valid_wind = ~(np.isnan(u_kt) | np.isnan(v_kt))
    bfh = fh_vals[bi]
    bu  = u_kt[bi]
    bv  = v_kt[bi]
    bm  = valid_wind[bi]

    if bm.any():
        ax_wind.barbs(
            bfh[bm], np.zeros(bm.sum()),
            bu[bm], bv[bm],
            length=5, linewidth=0.7, color="black", zorder=2,
        )

    ax_wind.set_yticks([])
    ax_wind.text(0.0, 0.97, "Wind", transform=ax_wind.transAxes,
                 ha='right', va='top', fontsize=9, clip_on=False)
    for sp in ("top", "right", "left", "bottom"):
        ax_wind.spines[sp].set_visible(False)

    ax_wind.set_title(title, fontsize=10, fontweight="bold", pad=4)

    # ── T / RH panel ─────────────────────────────────────────────────────────
    ax_rh = ax_data.twinx()

    ax_data.plot(fh_vals, tmp_display, color="black",  linewidth=1.5, zorder=3)
    ax_rh.plot(  fh_vals, rh,          color="green",  linewidth=1.5, zorder=3)

    ax_data.text(0.0, 1.12, t_label, transform=ax_data.transAxes,
                 ha='right', va='top', fontsize=9, clip_on=False)
    ax_data.text(1.015, 1.12, "RH (%)", transform=ax_data.transAxes,
                 ha='left', va='top', fontsize=9, color='green', clip_on=False)
    ax_rh.set_ylim(0, 105)

    # Horizontal grid lines anchored to temperature ticks; RH grid suppressed
    # so only one set of lines appears.
    ax_data.yaxis.grid(True, color="#cccccc", linewidth=0.4, linestyle="-", zorder=1)
    ax_rh.yaxis.grid(False)

    ax_data.spines["top"].set_visible(False)
    ax_rh.spines["top"].set_visible(False)

    # ── Day boundaries and labels ─────────────────────────────────────────────
    midnight_fhs = [
        fh for fh, vt in zip(fh_vals, valid_times)
        if vt.hour == 0 and int(fh) > 0
    ]
    for mfh in midnight_fhs:
        ax_wind.axvline(mfh, color="black", linewidth=1.5, zorder=3)
        ax_data.axvline(mfh, color="black", linewidth=1.5, zorder=3)

    # Segment boundaries for day labels (include start and end)
    seg_bounds = [float(fh_vals[0])] + list(midnight_fhs) + [float(fh_vals[-1])]
    wind_trans = blended_transform_factory(ax_wind.transData, ax_wind.transAxes)
    for i in range(len(seg_bounds) - 1):
        mid_fh  = (seg_bounds[i] + seg_bounds[i + 1]) / 2
        mid_vt  = (model_run.replace(tzinfo=utc_tz) + timedelta(hours=mid_fh)).astimezone(local_tz)
        day_lbl = f"{mid_vt.strftime('%a')} {mid_vt.day}".upper()
        ax_wind.text(
            mid_fh, 0.90, day_lbl,
            ha="center", va="top",
            transform=wind_trans,
            fontsize=8, color="#333333", fontweight="bold",
        )

    # ── X axis ticks (every 6 h in local time, HH:MM) ────────────────────────
    tick_pairs  = [(fh, vt) for fh, vt in zip(fh_vals, valid_times) if vt.hour % 6 == 0]
    tick_fhs    = [fh for fh, _  in tick_pairs]
    tick_labels = [vt.strftime("%H:%M") for _, vt in tick_pairs]
    ax_data.set_xticks(tick_fhs)
    ax_data.set_xticklabels(tick_labels, rotation=45, ha="right", fontsize=7)
    ax_data.set_xlim(fh_vals[0], fh_vals[-1])

    # ── Save ──────────────────────────────────────────────────────────────────
    tmp_path = output_path + ".tmp"
    plt.savefig(tmp_path, format="png", dpi=dpi, bbox_inches="tight")
    plt.close(fig)
    os.replace(tmp_path, output_path)
    logger.info(f"Saved meteogram -> {output_path}")


# ── Entry point ───────────────────────────────────────────────────────────────

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Render GFS forecast meteograms for a point location.")
    parser.add_argument("--run",       default=None,
                        help="GFS model run in YYYYMMDD_HH format (e.g. 20260404_00); "
                             "defaults to the latest complete run in the database")
    parser.add_argument("--lat",       required=True, type=float,
                        help="Target latitude (decimal degrees)")
    parser.add_argument("--lon",       required=True, type=float,
                        help="Target longitude (decimal degrees, negative = west)")
    parser.add_argument("--icao",      required=True,
                        help="ICAO identifier used in filenames and title (e.g. KDWH)")
    parser.add_argument("--locality",  default="",
                        help="Human-readable locality name for the chart title")
    parser.add_argument("--temp-unit", choices=["F", "C"], default="F",
                        help="Temperature unit for the T axis (default: F)")
    parser.add_argument("--tz",        default="UTC",
                        help="IANA timezone for the time axis (default: UTC)")
    parser.add_argument("--out-abbrev", required=True,
                        help="Output path for the abbreviated (emailed) meteogram PNG")
    parser.add_argument("--out-full",  required=True,
                        help="Output path for the full-period meteogram PNG")
    args = parser.parse_args()

    engine = get_engine()

    if args.run is not None:
        model_run = datetime.strptime(args.run, "%Y%m%d_%H")
    else:
        from sqlalchemy import text as _text
        with engine.connect() as _conn:
            model_run = _conn.execute(
                _text("SELECT MAX(ModelRunUtc) FROM GfsModelRuns WHERE IsComplete = 1")
            ).scalar()
        if model_run is None:
            logger.error("No complete GFS run found in the database.")
            raise SystemExit(1)
        logger.info(f"Using latest complete run: {model_run:%Y%m%d_%H}")

    nearby = load_gfs_nearby(engine, model_run, args.lat, args.lon)

    if nearby.empty:
        logger.error(f"No GFS data found for run {args.run} near "
                     f"lat={args.lat:.2f}, lon={args.lon:.2f}.")
        raise SystemExit(1)

    series = _nearest_point_series(nearby, args.lat, args.lon)
    logger.info(f"Loaded {len(series)} forecast hours for {args.icao} "
                f"(nearest grid point: lat={series['Lat'].iloc[0]:.2f}, "
                f"lon={series['Lon'].iloc[0]:.2f}).")

    locality = args.locality or args.icao
    unit_lbl = "°F" if args.temp_unit == "F" else "°C"
    title    = f"{locality} ({unit_lbl})"

    # 48-hour abbreviated version (emailed)
    render_meteogram(
        series, args.out_abbrev, title,
        temp_unit=args.temp_unit,
        tz_name=args.tz,
        max_hours=48,
        fig_width=10.0,
        dpi=100,
    )

    # Full-period version
    n_hours = int(series["ForecastHour"].max())
    full_width = max(10.0, min(18.0, n_hours / 24.0 * 10.0))
    render_meteogram(
        series, args.out_full, title,
        temp_unit=args.temp_unit,
        tz_name=args.tz,
        max_hours=None,
        fig_width=full_width,
        dpi=100,
    )
