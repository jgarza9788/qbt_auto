"""Alembic environment.

Runs synchronously against the same SQLite file the app uses asynchronously --
migrations are a startup step, not a hot path, and the sync driver keeps this
script simple.
"""

from __future__ import annotations

from logging.config import fileConfig

from alembic import context
from sqlalchemy import create_engine, event, pool

from qbitflow.config import get_config
from qbitflow.db.base import Base, _apply_pragmas
from qbitflow.db import models  # noqa: F401 - imported for its side effect of registering tables

config = context.config
if config.config_file_name is not None:
    fileConfig(config.config_file_name)

target_metadata = Base.metadata


def _url() -> str:
    """Prefer a URL the caller set explicitly.

    The application passes the database it is actually going to open, so a test
    or an embedded instance does not migrate whatever the environment happens to
    point at.
    """
    configured = config.get_main_option("sqlalchemy.url", None)
    if configured:
        return configured
    return get_config().sync_database_url


def run_migrations_offline() -> None:
    context.configure(
        url=_url(),
        target_metadata=target_metadata,
        literal_binds=True,
        dialect_opts={"paramstyle": "named"},
        render_as_batch=True,
    )
    with context.begin_transaction():
        context.run_migrations()


def run_migrations_online() -> None:
    engine = create_engine(_url(), poolclass=pool.NullPool, future=True)
    event.listen(engine, "connect", _apply_pragmas)

    with engine.connect() as connection:
        context.configure(
            connection=connection,
            target_metadata=target_metadata,
            # SQLite cannot ALTER most things in place; batch mode rebuilds the
            # table instead, which is the only way ALTER COLUMN ever works here.
            render_as_batch=True,
        )
        with context.begin_transaction():
            context.run_migrations()


if context.is_offline_mode():
    run_migrations_offline()
else:
    run_migrations_online()
