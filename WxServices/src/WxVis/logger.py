"""
logger.py — WxVis logging configuration.

Configures a single 'wxvis' logger that writes to
C:\\HarderWare\\Logs\\wxvis.log with size-based rotation, using the same
timestamp format as the C# log4net appenders:

    2026-04-02 10:30:45.123 INFO  Saved forecast map → ...

Import the module-level ``logger`` instance in any WxVis script:

    from logger import logger
    logger.info("...")
"""

import logging
import logging.handlers
from pathlib import Path

_LOG_PATH     = Path(r"C:\HarderWare\Logs\wxvis.log")
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

    # ── File handler ──────────────────────────────────────────────────────────
    _LOG_PATH.parent.mkdir(parents=True, exist_ok=True)
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
    ch = logging.StreamHandler()
    ch.setLevel(logging.INFO)
    ch.setFormatter(formatter)
    log.addHandler(ch)

    return log


logger = _configure()
