"""SQLite user-defined functions available to compiled and raw conditions.

Kept deliberately small. A UDF forces SQLite to call back into Python once per
row, which turns a set-based query into a loop -- so anything needed in a hot
path is a precomputed column instead (``days_since_added``, ``size_gb``,
``media_key``). These exist for the raw-SQL escape hatch and for the operators
SQLite has no native support for.

Regular expressions run through the ``regex`` module with a wall-clock timeout.
A user-supplied pattern meeting a user-supplied filename is exactly the shape
that produces catastrophic backtracking, and the standard library offers no way
to bound it.
"""

from __future__ import annotations

import sqlite3
from datetime import UTC, datetime

import regex
import structlog

log = structlog.get_logger(__name__)

SECONDS_PER_DAY = 86_400
GB = 1024**3

#: Matches Dev8's evaluation timeout. Long enough for any sane pattern, short
#: enough that a pathological one costs one rule, not the cycle.
REGEX_TIMEOUT_SECONDS = 2.0
MAX_PATTERN_LENGTH = 512

_pattern_cache: dict[str, regex.Pattern | None] = {}


def _compile(pattern: str) -> regex.Pattern | None:
    if pattern in _pattern_cache:
        return _pattern_cache[pattern]
    compiled: regex.Pattern | None
    if len(pattern) > MAX_PATTERN_LENGTH:
        log.warning("udf.pattern_too_long", length=len(pattern))
        compiled = None
    else:
        try:
            compiled = regex.compile(pattern, regex.IGNORECASE)
        except regex.error as exc:
            log.warning("udf.bad_pattern", pattern=pattern, error=str(exc))
            compiled = None
    _pattern_cache[pattern] = compiled
    return compiled


def days_since(epoch_seconds: int | None) -> float | None:
    """Days between an epoch timestamp and now. NULL stays NULL."""
    if epoch_seconds is None:
        return None
    try:
        return (datetime.now(UTC).timestamp() - float(epoch_seconds)) / SECONDS_PER_DAY
    except (TypeError, ValueError):
        return None


def size_gb(byte_count: int | None) -> float | None:
    if byte_count is None:
        return None
    try:
        return float(byte_count) / GB
    except (TypeError, ValueError):
        return None


def regex_match(text: str | None, pattern: str | None) -> int:
    """1 when the pattern matches. A bad pattern or a timeout is 0, never an error."""
    if text is None or pattern is None:
        return 0
    compiled = _compile(pattern)
    if compiled is None:
        return 0
    try:
        return 1 if compiled.search(str(text), timeout=REGEX_TIMEOUT_SECONDS) else 0
    except TimeoutError:
        log.warning("udf.regex_timeout", pattern=pattern)
        return 0
    except regex.error:
        return 0


def path_matches(left: str | None, right: str | None) -> int:
    """Whether two normalized paths refer to the same thing, either containing the other.

    Library and client paths often differ by a wrapping release folder, so plain
    equality misses cases that are obviously the same file to a human.
    """
    if not left or not right:
        return 0
    a, b = str(left).lower(), str(right).lower()
    if a == b:
        return 1
    # Both sides must be substantial, or short strings match everything.
    if len(a) < 5 or len(b) < 5:
        return 0
    return 1 if a in b or b in a else 0


def contains_token(haystack: str | None, token: str | None) -> int:
    """Membership in a comma-wrapped list column, e.g. tags or genres.

    Exists so a raw-SQL author does not have to remember the ``,tag,`` wrapping
    convention and accidentally write a LIKE that matches a longer tag.
    """
    if not haystack or not token:
        return 0
    return 1 if f",{str(token).strip().lower()}," in str(haystack).lower() else 0


#: Name to (arity, implementation). Also serves as the registry the field
#: reference panel introspects, so the helper list in the UI cannot go stale.
FUNCTIONS: dict[str, tuple[int, object]] = {
    "days_since": (1, days_since),
    "size_gb": (1, size_gb),
    "regex_match": (2, regex_match),
    "path_matches": (2, path_matches),
    "contains_token": (2, contains_token),
}

#: Documentation for the field reference panel.
FUNCTION_DOCS: dict[str, str] = {
    "days_since": "days_since(epoch) -> days between an epoch timestamp and now",
    "size_gb": "size_gb(bytes) -> a byte count as gigabytes",
    "regex_match": "regex_match(text, pattern) -> 1 if the pattern matches (case-insensitive)",
    "path_matches": "path_matches(a, b) -> 1 if either path contains the other",
    "contains_token": "contains_token(list, token) -> 1 if a comma-wrapped list holds the token",
}


def register(conn: sqlite3.Connection) -> None:
    for name, (arity, fn) in FUNCTIONS.items():
        conn.create_function(name, arity, fn, deterministic=False)
    # So a raw condition can use the idiomatic "column REGEXP pattern" form.
    conn.create_function("regexp", 2, lambda p, t: regex_match(t, p), deterministic=False)
