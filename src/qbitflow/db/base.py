"""Async SQLAlchemy engine and session factory for the persistent database.

Only config, rules and run history live here. Torrent and media data is
materialised into the in-memory snapshot database each cycle (see
:mod:`qbitflow.snapshot`) and never touches disk.
"""

from __future__ import annotations

import sqlite3
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Any

from sqlalchemy import event
from sqlalchemy.ext.asyncio import (
    AsyncEngine,
    AsyncSession,
    async_sessionmaker,
    create_async_engine,
)
from sqlalchemy.orm import DeclarativeBase


class Base(DeclarativeBase):
    pass


def _apply_pragmas(dbapi_conn: Any, _record: Any) -> None:
    """WAL plus a generous busy timeout.

    A background scheduler and the web handlers share one SQLite file. Without WAL
    a reader blocks the engine's writes; without a busy timeout a concurrent write
    raises ``database is locked`` instead of waiting.
    """
    if not isinstance(dbapi_conn, sqlite3.Connection):
        return
    cur = dbapi_conn.cursor()
    try:
        cur.execute("PRAGMA journal_mode=WAL")
        cur.execute("PRAGMA synchronous=NORMAL")
        cur.execute("PRAGMA busy_timeout=30000")
        cur.execute("PRAGMA foreign_keys=ON")
    finally:
        cur.close()


def create_engine(db_path: Path, *, echo: bool = False) -> AsyncEngine:
    db_path.parent.mkdir(parents=True, exist_ok=True)
    engine = create_async_engine(
        f"sqlite+aiosqlite:///{db_path.as_posix()}",
        echo=echo,
        future=True,
        # SQLite connections are cheap and per-connection PRAGMAs must be re-applied;
        # a small pool keeps the scheduler and web handlers from serialising.
        pool_pre_ping=False,
    )
    event.listen(engine.sync_engine, "connect", _apply_pragmas)
    return engine


def create_session_factory(engine: AsyncEngine) -> async_sessionmaker[AsyncSession]:
    return async_sessionmaker(
        engine,
        class_=AsyncSession,
        expire_on_commit=False,
        autoflush=False,
    )


@asynccontextmanager
async def session_scope(
    factory: async_sessionmaker[AsyncSession],
) -> AsyncIterator[AsyncSession]:
    """A transaction that commits on success and rolls back on any exception."""
    async with factory() as session:
        try:
            yield session
            await session.commit()
        except Exception:
            await session.rollback()
            raise
