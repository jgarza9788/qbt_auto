"""Runs a rule's condition against a snapshot and returns the matched set.

One entry point for both condition modes, so the runner, the "test against
current torrents" preview and the API all get identical semantics.
"""

from __future__ import annotations

import sqlite3
from dataclasses import dataclass, field
from time import perf_counter

import structlog

from qbitflow.conditions import rawsql
from qbitflow.conditions.compiler import CompiledQuery, CompileError, compile_tree
from qbitflow.conditions.tree import Group, parse_tree
from qbitflow.domain import ConditionMode
from qbitflow.snapshot.builder import Snapshot

log = structlog.get_logger(__name__)

DEFAULT_ROW_LIMIT = 50_000


@dataclass(slots=True)
class ConditionSpec:
    """Everything about a rule that determines which torrents it matches."""

    mode: ConditionMode = ConditionMode.BUILDER
    tree: Group | None = None
    raw_sql: str | None = None
    source_ids: list[int] = field(default_factory=list)

    @classmethod
    def from_rule(cls, rule) -> ConditionSpec:  # noqa: ANN001 - avoids a models import cycle
        return cls(
            mode=ConditionMode(rule.condition_mode),
            tree=parse_tree(rule.condition_tree),
            raw_sql=rule.raw_sql,
            source_ids=list(rule.target_source_ids or []),
        )


@dataclass(slots=True)
class MatchSet:
    matched: list[tuple[int, str]] = field(default_factory=list)
    sql: str = ""
    params: list[object] = field(default_factory=list)
    duration_ms: int = 0
    truncated: bool = False
    #: Set when the condition could not be evaluated. The rule is skipped for
    #: this cycle; other rules are unaffected.
    error: str | None = None

    @property
    def ok(self) -> bool:
        return self.error is None

    def hashes_by_source(self) -> dict[int, list[str]]:
        grouped: dict[int, list[str]] = {}
        for source_id, digest in self.matched:
            grouped.setdefault(source_id, []).append(digest)
        return grouped


def compile_spec(spec: ConditionSpec) -> CompiledQuery:
    """The SQL a builder-mode rule will run. Also drives the editor's preview."""
    tree = spec.tree or Group()
    return compile_tree(tree, source_ids=spec.source_ids or None)


def evaluate(
    snapshot: Snapshot,
    spec: ConditionSpec,
    *,
    row_limit: int = DEFAULT_ROW_LIMIT,
    timeout_seconds: float = rawsql.DEFAULT_TIMEOUT_SECONDS,
) -> MatchSet:
    started = perf_counter()

    try:
        if spec.mode is ConditionMode.RAW_SQL:
            matched = rawsql.execute(
                snapshot.conn,
                spec.raw_sql or "",
                source_ids=spec.source_ids or None,
                timeout_seconds=timeout_seconds,
                row_limit=row_limit,
            )
            query_sql = rawsql.build_raw_query(spec.raw_sql or "").sql
            params: list[object] = []
        else:
            compiled = compile_spec(spec)
            query_sql, params = compiled.sql, compiled.params
            rows = snapshot.query(f"{compiled.sql} LIMIT {int(row_limit)}", compiled.params)
            matched = [(int(r["source_id"]), str(r["hash"])) for r in rows]
    except (CompileError, rawsql.RawSqlError) as exc:
        return MatchSet(
            duration_ms=int((perf_counter() - started) * 1000), error=str(exc)
        )
    except sqlite3.DatabaseError as exc:
        log.warning("condition.execute_failed", error=str(exc))
        return MatchSet(
            duration_ms=int((perf_counter() - started) * 1000), error=str(exc)
        )

    return MatchSet(
        matched=matched,
        sql=query_sql,
        params=list(params),
        duration_ms=int((perf_counter() - started) * 1000),
        truncated=len(matched) >= row_limit,
    )


@dataclass(slots=True, frozen=True)
class StoredCompilation:
    """What gets written onto a rule row when it is saved.

    Compiling at save time rather than at run time is what makes a broken rule
    visible in the rule list, instead of at four in the morning in a run log.
    """

    sql: str | None
    params: list[object]
    valid: bool
    error: str | None


def compile_for_storage(spec: ConditionSpec, snapshot: Snapshot | None) -> StoredCompilation:
    """Compile a rule for storage, by whichever route its mode implies.

    Shared by the rule editor's save and by configuration import, so an imported
    rule is not left flagged invalid merely because it arrived down a different
    path than the editor's.
    """
    if spec.mode is ConditionMode.RAW_SQL:
        if snapshot is None:
            # Nothing to validate against yet. Left valid rather than flagged:
            # the runner validates again before it uses it.
            return StoredCompilation(spec.raw_sql, [], True, None)
        try:
            query = rawsql.validate(snapshot.conn, spec.raw_sql or "")
        except rawsql.RawSqlError as exc:
            return StoredCompilation(spec.raw_sql, [], False, str(exc))
        return StoredCompilation(query.sql, [], True, None)

    try:
        compiled = compile_spec(spec)
    except (CompileError, ValueError) as exc:
        return StoredCompilation(None, [], False, str(exc))
    return StoredCompilation(compiled.sql, list(compiled.params), True, None)
