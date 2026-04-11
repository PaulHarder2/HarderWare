"""
logger.py — WxVis logging configuration.

Configures a single 'wxvis' logger that writes to the Logs directory with
size-based rotation, using the same timestamp format as the C# log4net
appenders:

    2026-04-02 10:30:45.123 INFO  Saved forecast map → ...

The log directory is read from the WXVIS_LOG_DIR environment variable
(set by WxVis.Svc), falling back to {InstallRoot}\\Logs.

Import the module-level ``logger`` instance in any WxVis script:

    from logger import logger
    logger.info("...")
"""

import logging
import logging.handlers
import os
import sys
from pathlib import Path

_LOG_DIR      = Path(os.environ.get("WXVIS_LOG_DIR", r"C:\HarderWare\Logs"))
_LOG_PATH     = _LOG_DIR / "wxvis.log"
_MAX_BYTES    = 10 * 1024 * 1024   # 10 MB
_BACKUP_COUNT = 10
_FMT          = "%(asctime)s.%(msecs)03d %(levelname)-5s %(message)s"
_DATEFMT      = "%Y-%m-%d %H:%M:%S"


def _configure() -> logging.Logger:
    log = logging.getLogger("wxvis")
    if log.handlers:
        return log          # already configured (e.g. imported twice)

    log.setLevel(logging.DEBUG)
    formatter = logging.Formatter(fmt=_FMT, datefmt=_DATEFMT)
    formatter.converter = logging.Formatter.converter  # keep default (local); Phase 2 switches to UTC

    # ── File handler ──────────────────────────────────────────────────────────
    _LOG_DIR.mkdir(parents=True, exist_ok=True)
    fh = logging.handlers.RotatingFileHandler(
        _LOG_PATH,
        maxBytes=_MAX_BYTES,
        backupCount=_BACKUP_COUNT,
        encoding="utf-8",
    )
    fh.setLevel(logging.DEBUG)
    fh.setFormatter(formatter)
    log.addHandler(fh)

    # ── Console handler ───────────────────────────────────────────────────────
    # Explicit stdout so that MapRenderer.RunAsync (which logs stderr at WARN)
    # only receives genuine Python errors and tracebacks, not normal progress.
    ch = logging.StreamHandler(sys.stdout)
    ch.setLevel(logging.INFO)
    ch.setFormatter(formatter)
    log.addHandler(ch)

    return log


logger = _configure()
