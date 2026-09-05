"""Run Alembic migrations at startup.

Self-hosted users upgrade by pulling a new image and restarting; there is no
separate migration step they could reasonably be expected to run.

The database URL is passed in rather than re-read from the environment, so a
test or an embedded instance migrates the database it is actually going to use.
"""

from __future__ import annotations

from pathlib import Path

import structlog
from alembic import command
from alembic.config import Config

log = structlog.get_logger(__name__)

_ROOT = Path(__file__).resolve().parents[3]


def alembic_config(db_path: Path | None = None) -> Config:
    cfg = Config(str(_ROOT / "alembic.ini"))
    cfg.set_main_option("script_location", str(_ROOT / "alembic"))
    if db_path is not None:
        db_path.parent.mkdir(parents=True, exist_ok=True)
        cfg.set_main_option("sqlalchemy.url", f"sqlite:///{db_path.as_posix()}")
    return cfg


def upgrade_to_head(db_path: Path | None = None) -> None:
    log.info("db.migrate.start", db=str(db_path) if db_path else "(from environment)")
    command.upgrade(alembic_config(db_path), "head")
    log.info("db.migrate.done")
