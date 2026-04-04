"""
map_utils.py — Shared rendering utilities for WxVis map scripts.

This module centralises the projection helpers, extrema labelling, Gaussian
smoothing, and map-extent constants that are common to both synoptic_map.py
and forecast_map.py.  Import from here rather than duplicating code between
the two rendering scripts.
"""

import numpy as np
import cartopy.crs as ccrs
from scipy.ndimage import gaussian_filter, maximum_filter, minimum_filter
from scipy.ndimage import label as _connected_components


# ── Default map extents ───────────────────────────────────────────────────────

#: CONUS with a small buffer.
CONUS_EXTENT = (-126.0, -65.0, 22.0, 50.0)

#: South-central US (Texas + surrounding states).
SOUTH_CENTRAL_EXTENT = (-106.0, -88.0, 25.0, 38.0)


# ── Projection helpers ────────────────────────────────────────────────────────

def _inner_proj_limits(
    proj, extent: tuple[float, float, float, float], n: int = 200
) -> tuple[float, float, float, float]:
    """
    Return the largest axis-aligned rectangle (in projection metres) that fits
    entirely within the projected shape of a lat/lon bounding box.

    Sampling each edge densely handles the curvature of Lambert Conformal
    parallels and meridians: parallels bow away from the equator so the top edge
    peaks at the centre; meridians converge at the pole so the left/right edges
    lean inward at the top.  Taking the tightest constraint from each edge gives
    an inner rectangle fully covered by data.
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


# ── Extrema labelling ─────────────────────────────────────────────────────────

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

    txt_kw = dict(fontsize=16, fontweight="bold", ha="center", va="center", zorder=6, clip_on=True)
    if transform is not None:
        txt_kw["transform"] = transform

    xl, xr = ax.get_xlim()
    yb, yt = ax.get_ylim()
    # Inward margin: reject labels whose anchor is within 3 % of any edge.
    # tight_layout() adjusts subplot padding after labels are placed, which
    # can shift the effective viewport bottom upward by a small amount.
    # A 3 % guard prevents boundary-adjacent anchors from appearing outside
    # the border in the saved image.
    xm = 0.03 * (xr - xl)
    ym = 0.03 * (yt - yb)

    for mask, lbl, color in (
        (local_max, high_label, high_color),
        (local_min, low_label,  low_color),
    ):
        labeled_arr, n = _connected_components(mask)
        for idx in range(1, n + 1):
            ys, xs = np.where(labeled_arr == idx)
            iy = int(round(ys.mean()))
            ix = int(round(xs.mean()))
            lx, ly = x2d[iy, ix], y2d[iy, ix]
            # Skip labels whose anchor falls outside (or too close to) the
            # axes viewport.  Cartopy does not reliably honour clip_on=True
            # when the anchor itself is near or outside the plot area.
            if transform is not None:
                nx, ny = ax.projection.transform_point(lx, ly, transform)
            else:
                nx, ny = lx, ly
            if not (xl + xm <= nx <= xr - xm and yb + ym <= ny <= yt - ym):
                continue
            ax.text(lx, ly, lbl, color=color, **txt_kw)


# ── Gaussian smoothing ────────────────────────────────────────────────────────

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
    smooth_data    = gaussian_filter(filled,  sigma=sigma)
    smooth_weights = gaussian_filter(weights, sigma=sigma)
    with np.errstate(invalid="ignore"):
        result = smooth_data / smooth_weights
    result[nan_mask] = np.nan
    return result
