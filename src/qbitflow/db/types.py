"""Custom column types."""

from __future__ import annotations

from datetime import UTC, datetime
from typing import Any

from sqlalchemy import Dialect, Integer
from sqlalchemy.types import TypeDecorator


class UtcTimestamp(TypeDecorator[datetime]):
    """A timezone-aware datetime stored as Unix epoch seconds.

    Integers sort and compare correctly in SQLite with no collation surprises, and
    stay meaningful to any tool that opens the file. Python code deals in aware
    ``datetime`` objects; naive values are rejected rather than silently assumed
    to be local time.
    """

    impl = Integer
    cache_ok = True

    def process_bind_param(self, value: datetime | None, dialect: Dialect) -> int | None:
        if value is None:
            return None
        if value.tzinfo is None:
            raise ValueError("naive datetime passed to a UtcTimestamp column")
        return int(value.timestamp())

    def process_result_value(self, value: Any, dialect: Dialect) -> datetime | None:
        if value is None:
            return None
        return datetime.fromtimestamp(int(value), tz=UTC)


def utcnow() -> datetime:
    return datetime.now(UTC)
