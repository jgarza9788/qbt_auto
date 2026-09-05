"""Metadata the UI reads instead of hardcoding.

The field list, the operators valid for each field type, the action parameter
schemas and the schedule helper all come from here. Nothing in the browser
duplicates a Python enum, so the two cannot drift apart.
"""

from __future__ import annotations

from typing import Any

from fastapi import APIRouter, HTTPException, Query

from qbitflow.actions import action_schemas
from qbitflow.api.deps import StateDep
from qbitflow.conditions.registry import OPERATORS_BY_TYPE, get_registry, helper_functions
from qbitflow.domain import VALUELESS_OPERATORS
from qbitflow.engine.schedule_text import PRESETS, CronError, describe_schedule
from qbitflow.snapshot.schema import READABLE_TABLES

router = APIRouter(prefix="/api", tags=["meta"])

#: A few live values per field make the reference panel concrete rather than
#: abstract -- "87.4" from the user's own disk beats a made-up example.
SAMPLE_LIMIT = 3


@router.get("/fields")
async def fields(state: StateDep, live: bool = Query(default=True)) -> dict[str, Any]:
    registry = get_registry()
    snapshot = state.snapshot.current if state.snapshot else None

    groups: list[dict[str, Any]] = []
    for group, definitions in registry.by_group().items():
        entries = []
        for definition in definitions:
            entry: dict[str, Any] = {
                "key": definition.key,
                "label": definition.label,
                "type": str(definition.type),
                "source": definition.group,
                "description": definition.description,
                "sample": definition.sample,
                "nullable": definition.nullable,
                "takes_qualifier": definition.takes_qualifier,
                "qualifier_label": definition.qualifier_label,
                "operators": [str(op) for op in registry.operators_for(definition.type)],
                "sql": definition.expr,
            }
            if live and snapshot is not None:
                entry["live_samples"] = _live_samples(snapshot, definition)
            entries.append(entry)
        groups.append({"name": group, "fields": entries})

    return {
        "groups": groups,
        "helpers": helper_functions(),
        "tables": sorted(READABLE_TABLES),
        "valueless_operators": [str(op) for op in VALUELESS_OPERATORS],
    }


def _live_samples(snapshot, definition) -> list[str]:  # noqa: ANN001
    """Real values from this user's data, where the field is a plain column."""
    if definition.takes_qualifier or "SELECT" in definition.expr.upper():
        return []
    joins = " ".join(
        {
            "media": "LEFT JOIN media_items m ON m.id = t.media_item_id",
            "play": "LEFT JOIN play_counts pc ON pc.media_item_id = t.media_item_id",
        }[name]
        for name in definition.joins
    )
    sql = (
        f"SELECT DISTINCT {definition.expr} AS v FROM torrents t {joins} "
        f"WHERE {definition.expr} IS NOT NULL LIMIT {SAMPLE_LIMIT}"
    )
    try:
        rows = snapshot.query(sql)
    except Exception:  # noqa: BLE001 - a sample is a nicety, never an error
        return []
    return [str(r["v"]) for r in rows if r["v"] not in (None, "")]


@router.get("/operators")
async def operators() -> dict[str, Any]:
    return {
        str(field_type): [
            {"value": str(op), "valueless": op in VALUELESS_OPERATORS} for op in ops
        ]
        for field_type, ops in OPERATORS_BY_TYPE.items()
    }


@router.get("/actions")
async def actions() -> list[dict[str, Any]]:
    return action_schemas()


@router.get("/schedule/describe")
async def schedule(
    expression: str = Query(..., description="A cron expression or a phrase"),
    timezone: str | None = Query(default=None),
) -> dict[str, Any]:
    """Turn either input form into a cron expression plus its next few runs."""
    try:
        info = describe_schedule(expression, timezone=timezone)
    except CronError as exc:
        raise HTTPException(422, str(exc)) from exc
    return {
        "cron": info.cron,
        "description": info.description,
        "next_runs": [d.isoformat() for d in info.next_runs],
        "clamped_from": info.clamped_from,
        "warning": info.warning,
        "presets": sorted(PRESETS),
    }
