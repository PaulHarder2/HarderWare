"""
db.py — SQLAlchemy engine and query functions for WxVis.

Uses Windows Authentication (trusted connection) against the local SQLEXPRESS
instance.  All query functions return pandas DataFrames suitable for direct
use with MetPy / matplotlib.
"""

import json
import urllib.parse
import warnings
from pathlib import Path

import pandas as pd
from sqlalchemy import create_engine, text
from sqlalchemy.exc import SAWarning

warnings.filterwarnings("ignore", category=SAWarning, message=".*Unrecognized server version info.*")


def _load_config() -> dict:
    config_path = Path(__file__).parent / "config.json"
    with open(config_path, encoding="utf-8") as f:
        return json.load(f)


def get_engine():
    """
    Build and return a SQLAlchemy engine for the WeatherData database.

    Uses the ODBC connection string style with Windows Authentication so no
    username or password is required.  The engine is lightweight to create and
    can be kept alive for the duration of a script.
    """
    cfg = _load_config()
    db_cfg = cfg["db"]
    odbc_str = (
        f"DRIVER={{{db_cfg['driver']}}};"
        f"SERVER={db_cfg['server']};"
        f"DATABASE={db_cfg['database']};"
        "Trusted_Connection=yes;"
    )
    conn_url = f"mssql+pyodbc:///?odbc_connect={urllib.parse.quote_plus(odbc_str)}"
    return create_engine(conn_url)


def load_output_dir() -> Path:
    """Return the configured output directory, creating it if necessary."""
    cfg = _load_config()
    out = Path(cfg["output_dir"])
    out.mkdir(parents=True, exist_ok=True)
    return out


# ── METAR queries ─────────────────────────────────────────────────────────────

_LATEST_METARS_SQL = text("""
    SELECT
        m.StationIcao,
        m.ObservationUtc,
        m.AirTemperatureCelsius,
        m.DewPointCelsius,
        m.WindDirection,
        m.WindSpeed,
        m.WindGust,
        m.WindUnit,
        m.WindIsVariable,
        m.AltimeterValue,
        m.AltimeterUnit,
        m.VisibilityM,
        m.VisibilityStatuteMiles,
        m.VisibilityCavok,
        m.RawSkyConditions,
        m.RawWeatherPhenomena,
        s.Lat,
        s.Lon,
        s.ElevationFt,
        s.Name  AS StationName
    FROM Metars m
    INNER JOIN WxStations s ON m.StationIcao = s.IcaoId
    WHERE m.ObservationUtc = (
        SELECT MAX(m2.ObservationUtc)
        FROM   Metars m2
        WHERE  m2.StationIcao = m.StationIcao
    )
""")


def load_latest_metars(engine) -> pd.DataFrame:
    """
    Return one row per station, containing the most recent METAR observation
    and the station's geographic metadata.

    Stations that have no corresponding row in WxStations (i.e. not yet
    resolved by WxParser.Svc) are excluded by the INNER JOIN.

    Returns a DataFrame with columns:
        StationIcao, ObservationUtc, AirTemperatureCelsius, DewPointCelsius,
        WindDirection, WindSpeed, WindGust, WindUnit, WindIsVariable,
        AltimeterValue, AltimeterUnit, VisibilityM, VisibilityStatuteMiles,
        VisibilityCavok, RawSkyConditions, RawWeatherPhenomena,
        Lat, Lon, ElevationFt, StationName
    """
    with engine.connect() as conn:
        return pd.read_sql(_LATEST_METARS_SQL, conn)


# ── GFS queries ───────────────────────────────────────────────────────────────

_GFS_GRID_SQL = text("""
    SELECT
        g.ModelRunUtc,
        g.ForecastHour,
        g.Lat,
        g.Lon,
        g.TmpC,
        g.DwpC,
        g.UGrdMs,
        g.VGrdMs,
        g.PRateKgM2s,
        g.TcdcPct,
        g.CapeJKg,
        g.PrMslPa
    FROM GfsGrid g
    WHERE g.ForecastHour = :fh
      AND g.ModelRunUtc  = :run
""")

_GFS_LATEST_COMPLETE_RUN_SQL = text("""
    SELECT MAX(ModelRunUtc) FROM GfsModelRuns WHERE IsComplete = 1
""")

_GFS_NEARBY_SQL = text("""
    SELECT
        g.ModelRunUtc,
        g.ForecastHour,
        g.Lat,
        g.Lon,
        g.TmpC,
        g.DwpC,
        g.UGrdMs,
        g.VGrdMs
    FROM GfsGrid g
    WHERE g.ModelRunUtc = :run
      AND g.Lat BETWEEN :lat_min AND :lat_max
      AND g.Lon BETWEEN :lon_min AND :lon_max
    ORDER BY g.ForecastHour, g.Lat, g.Lon
""")


def load_gfs_grid(engine, forecast_hour: int = 0, model_run=None) -> pd.DataFrame:
    """
    Return all grid points for the specified GFS model run at the given
    forecast hour offset.

    Parameters
    ----------
    engine:
        SQLAlchemy engine from :func:`get_engine`.
    forecast_hour:
        Forecast hour offset (0 = analysis, 1–120 = hourly forecast).
        Default 0.
    model_run:
        Model run timestamp as a ``datetime`` or ISO string.  When ``None``
        (the default), the most recent complete run is used.

    Returns a DataFrame with columns:
        ModelRunUtc, ForecastHour, Lat, Lon, TmpC, DwpC, UGrdMs, VGrdMs,
        PRateKgM2s, TcdcPct, CapeJKg, PrMslPa
    """
    with engine.connect() as conn:
        if model_run is None:
            model_run = conn.execute(_GFS_LATEST_COMPLETE_RUN_SQL).scalar()
        return pd.read_sql(_GFS_GRID_SQL, conn, params={"fh": forecast_hour, "run": model_run})


def load_gfs_nearby(engine, model_run, lat: float, lon: float,
                    radius_deg: float = 0.5) -> pd.DataFrame:
    """
    Return GFS grid points within *radius_deg* degrees of (*lat*, *lon*) for
    all forecast hours of the specified model run.

    Intended for meteogram rendering where only a single point time-series is
    required.  The caller should perform nearest-point selection per forecast
    hour from the returned subset.

    Parameters
    ----------
    engine:
        SQLAlchemy engine from :func:`get_engine`.
    model_run:
        Model run timestamp as a ``datetime`` or ISO string.
    lat:
        Target latitude in decimal degrees.
    lon:
        Target longitude in decimal degrees (negative = west).
    radius_deg:
        Half-width of the bounding box in degrees.  Default 0.5 (±2 grid cells
        at 0.25° resolution).

    Returns a DataFrame with columns:
        ModelRunUtc, ForecastHour, Lat, Lon, TmpC, DwpC, UGrdMs, VGrdMs
    """
    with engine.connect() as conn:
        return pd.read_sql(_GFS_NEARBY_SQL, conn, params={
            "run":     model_run,
            "lat_min": lat - radius_deg,
            "lat_max": lat + radius_deg,
            "lon_min": lon - radius_deg,
            "lon_max": lon + radius_deg,
        })
