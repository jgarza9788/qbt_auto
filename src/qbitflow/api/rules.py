"""Rules: read, save, compile-preview, and test against real torrents.

There are two ways to write, and they exist for different callers:

* ``POST /api/rules`` is an atomic reconcile of the *whole* list -- it deletes any
  stored rule the payload omits. That is what the rule list and configuration
  import want: either the whole edit lands or none of it does, and there is no
  state where half a reorder is saved.
* ``POST /api/rules/new``, ``PUT`` and ``DELETE`` on ``/api/rules/{id}`` touch one
  rule and leave the rest alone. The editor is its own page, so it only ever
  holds one rule and must not be able to delete the others by omission.

Both go through the same ``_prepare`` and ``_write`` helpers, so a rule cannot be
validated, clamped or compiled differently depending on which door it came in by.
"""

from __future__ import annotations

from datetime import UTC, datetime
from typing import Any

from fastapi import APIRouter, HTTPException
from pydantic import BaseModel, Field
from sqlalchemy import func, select

from qbitflow.actions import get_handler
from qbitflow.api.deps import StateDep
from qbitflow.conditions.compiler import CompileError, compile_tree
from qbitflow.conditions.execute import (
    ConditionSpec,
    StoredCompilation,
    compile_for_storage,
    evaluate,
)
from qbitflow.conditions.rawsql import RawSqlError
from qbitflow.conditions.rawsql import validate as validate_raw
from qbitflow.conditions.tree import Group, parse_tree
from qbitflow.db.base import session_scope
from qbitflow.db.models import Rule, RuleAction
from qbitflow.domain import ConditionMode
from qbitflow.engine.schedule_text import CronError, clamp

router = APIRouter(prefix="/api/rules", tags=["rules"])

PREVIEW_LIMIT = 300
MAX_PREVIEW_LIMIT = 1000


class ActionIn(BaseModel):
    type: str
    params: dict[str, Any] = Field(default_factory=dict)


class RuleIn(BaseModel):
    #: Null for a rule the editor created but has never saved.
    id: int | None = None
    name: str = Field(min_length=1, max_length=128)
    description: str = ""
    enabled: bool = True
    condition_mode: ConditionMode = ConditionMode.BUILDER
    condition_tree: dict[str, Any] | None = None
    raw_sql: str | None = None
    target_source_ids: list[int] = Field(default_factory=list)
    cron: str = "*/15 * * * *"
    timezone: str | None = None
    dry_run: bool | None = None
    stop_on_match: bool | None = None
    cooldown_seconds: int | None = None
    actions: list[ActionIn] = Field(default_factory=list)


class RulesPayload(BaseModel):
    rules: list[RuleIn]


class CompileRequest(BaseModel):
    condition_mode: ConditionMode = ConditionMode.BUILDER
    condition_tree: dict[str, Any] | None = None
    raw_sql: str | None = None
    target_source_ids: list[int] = Field(default_factory=list)


class TestRequest(CompileRequest):
    limit: int = PREVIEW_LIMIT


def _serialise(rule: Rule) -> dict[str, Any]:
    return {
        "id": rule.id,
        "name": rule.name,
        "description": rule.description,
        "enabled": rule.enabled,
        "order": rule.order,
        "condition_mode": str(rule.condition_mode),
        "condition_tree": rule.condition_tree,
        "raw_sql": rule.raw_sql,
        "compiled_sql": rule.compiled_sql,
        "compile_valid": rule.compile_valid,
        "compile_error": rule.compile_error,
        "target_source_ids": rule.target_source_ids or [],
        "cron": rule.cron,
        "timezone": rule.timezone,
        "dry_run": rule.dry_run,
        "stop_on_match": rule.stop_on_match,
        "cooldown_seconds": rule.cooldown_seconds,
        "last_run_at": rule.last_run_at.isoformat() if rule.last_run_at else None,
        "last_matched_count": rule.last_matched_count,
        "actions": [{"type": a.type, "params": a.params} for a in rule.actions],
    }


@router.get("")
async def list_rules(state: StateDep) -> list[dict[str, Any]]:
    async with state.session_factory() as session:
        rules = (
            await session.execute(select(Rule).order_by(Rule.order, Rule.id))
        ).scalars().all()
    return [_serialise(rule) for rule in rules]


@router.post("/compile")
async def compile_preview(request: CompileRequest, state: StateDep) -> dict[str, Any]:
    """The SQL a condition will run, shown read-only in the editor.

    Making the generated query visible is what keeps the visual builder from
    being a black box, and it is the on-ramp to raw mode.
    """
    if request.condition_mode is ConditionMode.RAW_SQL:
        snapshot = state.snapshot.current if state.snapshot else None
        if snapshot is None:
            return {
                "valid": False,
                "error": "no snapshot yet -- refresh sources before validating raw SQL",
                "sql": request.raw_sql or "",
            }
        try:
            query = validate_raw(snapshot.conn, request.raw_sql or "")
        except RawSqlError as exc:
            return {"valid": False, "error": str(exc), "sql": request.raw_sql or ""}
        return {"valid": True, "error": None, "sql": query.sql, "wrapped": query.wrapped}

    try:
        compiled = compile_tree(
            parse_tree(request.condition_tree),
            source_ids=request.target_source_ids or None,
        )
    except (CompileError, ValueError) as exc:
        return {"valid": False, "error": str(exc), "sql": ""}

    return {
        "valid": True,
        "error": None,
        "sql": compiled.sql,
        "preview": compiled.preview,
        "params": compiled.params,
        "fields": sorted(compiled.fields),
    }


@router.post("/test")
async def test_rule(request: TestRequest, state: StateDep) -> dict[str, Any]:
    """Evaluate a condition against the current snapshot without changing anything.

    Read-only: it writes no run record and fires no action. Seeing the actual
    matched torrents before saving is the difference between a rules engine you
    can trust and one you have to guess at.
    """
    snapshot = state.snapshot.current if state.snapshot else None
    if snapshot is None:
        raise HTTPException(409, "no snapshot yet -- refresh sources first")

    limit = max(1, min(request.limit, MAX_PREVIEW_LIMIT))
    spec = ConditionSpec(
        mode=request.condition_mode,
        tree=parse_tree(request.condition_tree),
        raw_sql=request.raw_sql,
        source_ids=request.target_source_ids,
    )
    result = evaluate(snapshot, spec, row_limit=limit)
    if not result.ok:
        raise HTTPException(422, result.error or "the condition could not be evaluated")

    matched = set(result.matched)
    rows = snapshot.query(
        "SELECT source_id, hash, name, category, size_gb, ratio, state "
        f"FROM torrents ORDER BY name LIMIT {MAX_PREVIEW_LIMIT}"
    )
    return {
        "matched_count": len(result.matched),
        "evaluated": snapshot.stats.torrents,
        "duration_ms": result.duration_ms,
        "truncated": result.truncated,
        "sql": result.sql,
        "rows": [
            {
                "source_id": r["source_id"],
                "hash": r["hash"],
                "name": r["name"],
                "category": r["category"],
                "size_gb": round(r["size_gb"], 2),
                "ratio": round(r["ratio"], 2),
                "state": r["state"],
                "matched": (int(r["source_id"]), str(r["hash"])) in matched,
            }
            for r in rows
        ],
    }


# --------------------------------------------------------------------------- #
# Preparing one incoming rule
#
# Shared by the whole-list reconcile below and by the single-rule routes the
# editor page uses, so validation, cron clamping and compilation cannot come out
# differently depending on which door the rule came through.
# --------------------------------------------------------------------------- #


def _compile_for_storage(incoming: RuleIn, state: StateDep) -> StoredCompilation:
    return compile_for_storage(
        ConditionSpec(
            mode=incoming.condition_mode,
            tree=parse_tree(incoming.condition_tree)
            if incoming.condition_mode is not ConditionMode.RAW_SQL
            else None,
            raw_sql=incoming.raw_sql,
            source_ids=incoming.target_source_ids,
        ),
        state.snapshot.current if state.snapshot else None,
    )


def _prepare(incoming: RuleIn, state: StateDep) -> tuple[str, StoredCompilation, str | None]:
    """Validate, clamp and compile. Raises HTTPException the editor can show."""
    for action in incoming.actions:
        handler = get_handler(action.type)
        if handler is None:
            raise HTTPException(422, f"'{incoming.name}': unknown action '{action.type}'")
        try:
            action.params = handler.validate_params(action.params).model_dump()
        except Exception as exc:  # noqa: BLE001 - reported to the editor
            raise HTTPException(
                422, f"'{incoming.name}': action '{action.type}': {exc}"
            ) from exc

    try:
        cron, warning = clamp(incoming.cron)
    except CronError as exc:
        raise HTTPException(422, f"'{incoming.name}': {exc}") from exc

    return cron, _compile_for_storage(incoming, state), warning


def _write(
    rule: Rule, incoming: RuleIn, *, cron: str, compiled: StoredCompilation, order: int
) -> None:
    rule.name = incoming.name
    rule.description = incoming.description
    rule.enabled = incoming.enabled
    rule.order = order
    rule.condition_mode = incoming.condition_mode
    rule.condition_tree = incoming.condition_tree
    rule.raw_sql = incoming.raw_sql
    rule.target_source_ids = incoming.target_source_ids
    rule.cron = cron
    rule.timezone = incoming.timezone
    rule.dry_run = incoming.dry_run
    rule.stop_on_match = incoming.stop_on_match
    rule.cooldown_seconds = incoming.cooldown_seconds
    rule.compiled_sql = compiled.sql
    rule.compiled_params = compiled.params
    rule.compile_valid = compiled.valid
    rule.compile_error = compiled.error
    rule.compiled_at = datetime.now(UTC)

    # The action list is replaced wholesale, which is what the editor sends and
    # what cascade-delete-orphan expects.
    rule.actions.clear()
    for position, action in enumerate(incoming.actions):
        rule.actions.append(
            RuleAction(order=position, type=action.type, params=action.params)
        )


async def _resync(state: StateDep, *, forget: list[int] | None = None) -> None:
    """A changed rule should take effect now, not on the next restart."""
    if state.scheduler is not None:
        await state.scheduler.sync()
    if state.runner is not None:
        for rule_id in forget or []:
            state.runner.cooldowns.forget_rule(rule_id)


# --------------------------------------------------------------------------- #
# Whole-list reconcile
# --------------------------------------------------------------------------- #


@router.post("")
async def save_rules(payload: RulesPayload, state: StateDep) -> dict[str, Any]:
    """Make the stored rules match the posted list, atomically.

    Note that this *deletes* any stored rule the payload omits. It is the rule
    list's own save and the import path's; a page editing a single rule must use
    ``PUT /api/rules/{id}`` instead.
    """
    warnings: list[str] = []
    prepared: list[tuple[RuleIn, str, StoredCompilation]] = []

    for incoming in payload.rules:
        cron, compiled, warning = _prepare(incoming, state)
        if warning:
            warnings.append(f"'{incoming.name}': {warning}")
        prepared.append((incoming, cron, compiled))

    async with session_scope(state.session_factory) as session:
        existing = {
            rule.id: rule
            for rule in (await session.execute(select(Rule))).scalars().all()
        }
        seen: set[int] = set()

        for order, (incoming, cron, compiled) in enumerate(prepared):
            rule = existing.get(incoming.id) if incoming.id else None
            if rule is None:
                rule = Rule(name=incoming.name)
                session.add(rule)
            else:
                seen.add(rule.id)
            _write(rule, incoming, cron=cron, compiled=compiled, order=order)

        for rule_id, rule in existing.items():
            if rule_id not in seen:
                await session.delete(rule)

    await _resync(state, forget=list(existing))
    return {"saved": len(prepared), "warnings": warnings}


# --------------------------------------------------------------------------- #
# Single rule
#
# Declared before "/{rule_id}" so the literal path wins the match.
# --------------------------------------------------------------------------- #


@router.post("/new")
async def create_rule(payload: RuleIn, state: StateDep) -> dict[str, Any]:
    """Add one rule to the end of the list, leaving every other rule alone."""
    cron, compiled, warning = _prepare(payload, state)

    async with session_scope(state.session_factory) as session:
        last = (
            await session.execute(select(func.max(Rule.order)))
        ).scalar()
        rule = Rule(name=payload.name)
        session.add(rule)
        _write(rule, payload, cron=cron, compiled=compiled, order=(last or 0) + 1)
        await session.flush()
        rule_id = rule.id

    await _resync(state)
    async with state.session_factory() as session:
        created = await session.get(Rule, rule_id)
    return {"rule": _serialise(created), "warnings": [warning] if warning else []}


@router.put("/{rule_id}")
async def update_rule(rule_id: int, payload: RuleIn, state: StateDep) -> dict[str, Any]:
    """Save one rule in place, keeping its position in the list."""
    cron, compiled, warning = _prepare(payload, state)

    async with session_scope(state.session_factory) as session:
        rule = await session.get(Rule, rule_id)
        if rule is None:
            raise HTTPException(404, "no such rule")
        _write(rule, payload, cron=cron, compiled=compiled, order=rule.order)

    await _resync(state, forget=[rule_id])
    async with state.session_factory() as session:
        saved = await session.get(Rule, rule_id)
    return {"rule": _serialise(saved), "warnings": [warning] if warning else []}


@router.delete("/{rule_id}")
async def delete_rule(rule_id: int, state: StateDep) -> dict[str, Any]:
    async with session_scope(state.session_factory) as session:
        rule = await session.get(Rule, rule_id)
        if rule is None:
            raise HTTPException(404, "no such rule")
        await session.delete(rule)
        await session.flush()

        # Close the gap, so ordering stays contiguous and "evaluated top to
        # bottom" keeps meaning what the list shows.
        remaining = (
            await session.execute(select(Rule).order_by(Rule.order, Rule.id))
        ).scalars().all()
        for order, row in enumerate(remaining):
            row.order = order

    await _resync(state, forget=[rule_id])
    return {"deleted": rule_id}


@router.get("/{rule_id}")
async def get_rule(rule_id: int, state: StateDep) -> dict[str, Any]:
    async with state.session_factory() as session:
        rule = await session.get(Rule, rule_id)
    if rule is None:
        raise HTTPException(404, "no such rule")
    return _serialise(rule)


class ReorderPayload(BaseModel):
    rule_ids: list[int]


@router.post("/reorder")
async def reorder(payload: ReorderPayload, state: StateDep) -> dict[str, Any]:
    async with session_scope(state.session_factory) as session:
        for order, rule_id in enumerate(payload.rule_ids):
            rule = await session.get(Rule, rule_id)
            if rule is not None:
                rule.order = order
    await _resync(state)
    return {"reordered": len(payload.rule_ids)}


def empty_tree() -> Group:
    return Group()
