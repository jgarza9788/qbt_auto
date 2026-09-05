"""Advanced mode: a user-written SQL condition over the snapshot schema.

The escape hatch for people whose question the visual builder cannot express.
It is genuinely powerful, so it is genuinely fenced:

* **Authorizer.** SQLite refuses anything but SELECT and reads of the documented
  snapshot tables, checked before the statement runs. Attaching a database,
  writing, reading a table that is not on the list, calling an unapproved
  function -- all denied at the engine level, not by pattern-matching the text.
* **Read-only connection.** The snapshot is opened again in read-only mode, so
  even a hole in the authorizer cannot modify the data other rules are reading.
* **Statement timeout.** A progress handler interrupts a query that runs too long.
* **Row limit.** A condition matching every torrent is capped rather than
  materialising an unbounded result.
* **Validated on save.** EXPLAIN runs the statement through the parser and the
  authorizer without executing it, so a broken condition is refused at the point
  the user can still fix it.
"""

from __future__ import annotations

import re
import sqlite3
import time
from dataclasses import dataclass

import structlog

from qbitflow.snapshot import udf
from qbitflow.snapshot.schema import READABLE_TABLES

log = structlog.get_logger(__name__)

DEFAULT_TIMEOUT_SECONDS = 5.0
DEFAULT_ROW_LIMIT = 50_000

#: Checked every N virtual-machine instructions.
_PROGRESS_INTERVAL = 1_000

SELECT_PREFIX = "SELECT t.source_id, t.hash FROM torrents t"

#: Scalar functions a raw condition may call: SQLite's own safe builtins plus
#: our registered helpers. Anything else is denied.
_ALLOWED_FUNCTIONS = frozenset(
    {
        "abs", "coalesce", "ifnull", "iif", "instr", "length", "lower", "upper",
        "ltrim", "rtrim", "trim", "max", "min", "nullif", "replace", "round",
        "substr", "substring", "count", "sum", "avg", "total", "group_concat",
        "like", "glob", "printf", "format", "cast", "typeof", "date", "datetime",
        "strftime", "julianday", "unixepoch", "json_extract", "json_array_length",
        *udf.FUNCTIONS.keys(),
        "regexp",
    }
)

_LOOKS_LIKE_SELECT = re.compile(r"^\s*(?:WITH|SELECT)\b", re.IGNORECASE)


class RawSqlError(ValueError):
    """A raw condition that is rejected, with a message meant for the editor."""


class RawSqlTimeout(RawSqlError):
    pass


@dataclass(slots=True)
class RawQuery:
    sql: str
    #: True when the user wrote a bare WHERE clause and we wrapped it.
    wrapped: bool


def build_raw_query(raw: str) -> RawQuery:
    """Accept either a bare WHERE clause or a full SELECT.

    Most people want to write ``t.ratio > 2 AND t.category_lower = 'movies'``;
    the ones who need a CTE can write the whole statement.
    """
    text = (raw or "").strip().rstrip(";").strip()
    if not text:
        raise RawSqlError("the condition is empty")
    if ";" in text:
        raise RawSqlError("only a single statement is allowed")

    if _LOOKS_LIKE_SELECT.match(text):
        return RawQuery(sql=text, wrapped=False)
    return RawQuery(sql=f"{SELECT_PREFIX} WHERE ({text})", wrapped=True)


def _authorizer(action: int, arg1: str | None, arg2: str | None, *_: object) -> int:
    """Allow reads of the snapshot tables and nothing else."""
    if action == sqlite3.SQLITE_SELECT:
        return sqlite3.SQLITE_OK
    if action == sqlite3.SQLITE_READ:
        table = (arg1 or "").lower()
        return sqlite3.SQLITE_OK if table in READABLE_TABLES else sqlite3.SQLITE_DENY
    if action == sqlite3.SQLITE_FUNCTION:
        name = (arg2 or "").lower()
        return sqlite3.SQLITE_OK if name in _ALLOWED_FUNCTIONS else sqlite3.SQLITE_DENY
    # Everything else -- ATTACH, PRAGMA, writes, transactions, table creation.
    return sqlite3.SQLITE_DENY


def _install_guards(conn: sqlite3.Connection, timeout_seconds: float) -> None:
    deadline = time.monotonic() + timeout_seconds

    def _watchdog() -> int:
        # A non-zero return aborts the statement with sqlite3.OperationalError.
        return 1 if time.monotonic() > deadline else 0

    conn.set_progress_handler(_watchdog, _PROGRESS_INTERVAL)
    conn.set_authorizer(_authorizer)


def _clear_guards(conn: sqlite3.Connection) -> None:
    conn.set_progress_handler(None, 0)
    conn.set_authorizer(None)


def validate(
    conn: sqlite3.Connection, raw: str, *, timeout_seconds: float = DEFAULT_TIMEOUT_SECONDS
) -> RawQuery:
    """Parse and authorize the statement without running it.

    EXPLAIN compiles the statement, which is enough to surface a syntax error, an
    unknown column, or a denied table -- all before the rule is saved.
    """
    query = build_raw_query(raw)
    _install_guards(conn, timeout_seconds)
    try:
        cursor = conn.execute(f"EXPLAIN {query.sql}")
        cursor.close()
    except sqlite3.OperationalError as exc:
        message = str(exc)
        if "not authorized" in message.lower():
            raise RawSqlError(
                "this condition reads something it is not allowed to: "
                f"{message}. Readable tables are: {', '.join(sorted(READABLE_TABLES))}."
            ) from exc
        raise RawSqlError(message) from exc
    except sqlite3.DatabaseError as exc:
        raise RawSqlError(str(exc)) from exc
    finally:
        _clear_guards(conn)

    _check_shape(conn, query)
    return query


def _check_shape(conn: sqlite3.Connection, query: RawQuery) -> None:
    """A full SELECT must return the two columns the engine needs."""
    if query.wrapped:
        return
    _install_guards(conn, 1.0)
    try:
        cursor = conn.execute(f"SELECT * FROM ({query.sql}) LIMIT 0")
        columns = [d[0].lower() for d in (cursor.description or [])]
        cursor.close()
    except sqlite3.DatabaseError as exc:
        raise RawSqlError(str(exc)) from exc
    finally:
        _clear_guards(conn)

    if "hash" not in columns:
        raise RawSqlError(
            "a full query must return at least a 'hash' column "
            "(and 'source_id' when more than one qBittorrent instance is targeted); "
            f"this one returns: {', '.join(columns) or 'nothing'}"
        )


def execute(
    conn: sqlite3.Connection,
    raw: str,
    *,
    source_ids: list[int] | None = None,
    timeout_seconds: float = DEFAULT_TIMEOUT_SECONDS,
    row_limit: int = DEFAULT_ROW_LIMIT,
) -> list[tuple[int, str]]:
    """Run a validated raw condition and return the matched (source_id, hash) pairs."""
    query = build_raw_query(raw)

    sql = query.sql
    params: list[object] = []
    if source_ids:
        placeholders = ",".join("?" * len(source_ids))
        sql = f"SELECT * FROM ({sql}) WHERE source_id IN ({placeholders})"
        params.extend(source_ids)
    sql = f"{sql} LIMIT {int(row_limit)}"

    _install_guards(conn, timeout_seconds)
    try:
        cursor = conn.execute(sql, params)
        rows = cursor.fetchall()
        columns = [d[0].lower() for d in (cursor.description or [])]
        cursor.close()
    except sqlite3.OperationalError as exc:
        message = str(exc)
        if "interrupted" in message.lower():
            raise RawSqlTimeout(
                f"the condition took longer than {timeout_seconds:g}s and was stopped"
            ) from exc
        raise RawSqlError(message) from exc
    except sqlite3.DatabaseError as exc:
        raise RawSqlError(str(exc)) from exc
    finally:
        _clear_guards(conn)

    return _extract_pairs(rows, columns)


def _extract_pairs(rows: list, columns: list[str]) -> list[tuple[int, str]]:
    try:
        hash_index = columns.index("hash")
    except ValueError as exc:
        raise RawSqlError("the condition did not return a 'hash' column") from exc
    source_index = columns.index("source_id") if "source_id" in columns else None

    out: list[tuple[int, str]] = []
    for row in rows:
        values = tuple(row)
        source_id = int(values[source_index]) if source_index is not None else 0
        out.append((source_id, str(values[hash_index])))
    return out
