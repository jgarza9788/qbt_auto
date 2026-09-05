"""Configuration import and export.

Export redacts every credential. An export is therefore safe to paste into an
issue or share with someone reproducing a problem, at the cost of needing the
secrets re-entered after an import -- which is the right trade for a file people
will inevitably share.

Import defaults to a preview. Nothing is written until ``apply`` is set, and
imported rules always arrive disabled, so a shared rule set cannot start moving
files the moment it lands.
"""

from __future__ import annotations

import json
from datetime import UTC, datetime
from typing import Any

import structlog
import yaml
from fastapi import APIRouter, HTTPException, Query
from fastapi.responses import Response
from pydantic import BaseModel
from sqlalchemy import select

from qbitflow.actions import get_handler
from qbitflow.api.deps import StateDep
from qbitflow.conditions.execute import (
    ConditionSpec,
    StoredCompilation,
    compile_for_storage,
)
from qbitflow.conditions.tree import parse_tree
from qbitflow.db.base import session_scope
from qbitflow.db.models import PathMapping, Rule, RuleAction, SourceConnection, StoragePath
from qbitflow.domain import ConditionMode, SourceKind
from qbitflow.engine.schedule_text import CronError, clamp
from qbitflow.examples import example_payload

log = structlog.get_logger(__name__)

router = APIRouter(prefix="/api/config", tags=["config"])

EXPORT_VERSION = 1
REDACTED = "<redacted -- re-enter after importing>"


class ImportRequest(BaseModel):
    content: str
    #: False previews what would change without writing anything.
    apply: bool = False
    #: Replace the existing rules instead of adding to them.
    replace_rules: bool = False


@router.get("/export")
async def export_config(
    state: StateDep, format: str = Query(default="yaml", pattern="^(yaml|json)$")
) -> Response:
    settings = await state.settings.get_engine_settings()

    async with state.session_factory() as session:
        sources = (await session.execute(select(SourceConnection))).scalars().all()
        storage = (await session.execute(select(StoragePath))).scalars().all()
        mappings = (await session.execute(select(PathMapping))).scalars().all()
        rules = (await session.execute(select(Rule).order_by(Rule.order))).scalars().all()

    payload: dict[str, Any] = {
        "version": EXPORT_VERSION,
        "exported_at": datetime.now(UTC).isoformat(),
        "settings": settings.model_dump(mode="json"),
        "sources": [
            {
                "name": s.name,
                "kind": str(s.kind),
                "base_url": s.base_url,
                "username": s.username,
                # Never exported. There is no safe way to include it.
                "secret": REDACTED if s.secret_ciphertext else "",
                "enabled": s.enabled,
                "timeout_seconds": s.timeout_seconds,
                "verify_ssl": s.verify_ssl,
                "options": s.options or {},
            }
            for s in sources
        ],
        "storage_paths": [
            {
                "name": s.name,
                "path": s.path,
                "enabled": s.enabled,
                "measure_folder_size": s.measure_folder_size,
                "folder_scan_interval_minutes": s.folder_scan_interval_minutes,
            }
            for s in storage
        ],
        "path_mappings": [
            {
                "source": next((s.name for s in sources if s.id == m.source_id), None),
                "from_prefix": m.from_prefix,
                "to_prefix": m.to_prefix,
                "order": m.order,
            }
            for m in mappings
        ],
        "rules": [
            {
                "name": r.name,
                "description": r.description,
                "enabled": r.enabled,
                "condition_mode": str(r.condition_mode),
                "condition_tree": r.condition_tree,
                "raw_sql": r.raw_sql,
                "targets": [
                    s.name for s in sources if s.id in (r.target_source_ids or [])
                ],
                "cron": r.cron,
                "timezone": r.timezone,
                "dry_run": r.dry_run,
                "stop_on_match": r.stop_on_match,
                "cooldown_seconds": r.cooldown_seconds,
                "actions": [{"type": a.type, "params": a.params} for a in r.actions],
            }
            for r in rules
        ],
    }

    if format == "json":
        return Response(
            json.dumps(payload, indent=2),
            media_type="application/json",
            headers={"Content-Disposition": 'attachment; filename="qbitflow-config.json"'},
        )
    return Response(
        yaml.safe_dump(payload, sort_keys=False, allow_unicode=True),
        media_type="application/x-yaml",
        headers={"Content-Disposition": 'attachment; filename="qbitflow-config.yaml"'},
    )


@router.get("/examples")
async def examples() -> Response:
    """A starter rule set, in the same shape the import endpoint accepts."""
    return Response(
        yaml.safe_dump(example_payload(), sort_keys=False, allow_unicode=True),
        media_type="application/x-yaml",
        headers={"Content-Disposition": 'attachment; filename="qbitflow-example-rules.yaml"'},
    )


def _parse(content: str) -> dict[str, Any]:
    text = (content or "").strip()
    if not text:
        raise HTTPException(422, "nothing to import")
    try:
        # YAML is a superset of JSON, so one parser handles both formats.
        data = yaml.safe_load(text)
    except yaml.YAMLError as exc:
        raise HTTPException(422, f"could not parse the configuration: {exc}") from exc
    if not isinstance(data, dict):
        raise HTTPException(422, "the configuration must be a mapping at the top level")
    return data


@router.post("/import")
async def import_config(request: ImportRequest, state: StateDep) -> dict[str, Any]:
    data = _parse(request.content)
    plan: dict[str, Any] = {
        "applied": request.apply,
        "sources": [],
        "storage_paths": [],
        "path_mappings": [],
        "rules": [],
        "warnings": [],
    }

    for source in data.get("sources") or []:
        name = str(source.get("name") or "").strip()
        kind = str(source.get("kind") or "")
        if not name or kind not in set(SourceKind):
            plan["warnings"].append(f"skipped a source with an unknown kind: {kind!r}")
            continue
        plan["sources"].append(name)
        if source.get("secret") in (REDACTED, "", None):
            plan["warnings"].append(
                f"'{name}': no credential in the import -- set it before enabling this source"
            )

    for entry in data.get("storage_paths") or []:
        if entry.get("name") and entry.get("path"):
            plan["storage_paths"].append(entry["name"])

    for mapping in data.get("path_mappings") or []:
        if mapping.get("from_prefix") and mapping.get("to_prefix"):
            plan["path_mappings"].append(f"{mapping['from_prefix']} -> {mapping['to_prefix']}")

    for rule in data.get("rules") or []:
        name = str(rule.get("name") or "").strip()
        if not name:
            plan["warnings"].append("skipped a rule with no name")
            continue
        for action in rule.get("actions") or []:
            if get_handler(str(action.get("type"))) is None:
                plan["warnings"].append(
                    f"'{name}': unknown action '{action.get('type')}' will be dropped"
                )
        try:
            _, warning = clamp(str(rule.get("cron") or "*/15 * * * *"))
            if warning:
                plan["warnings"].append(f"'{name}': {warning}")
        except CronError as exc:
            plan["warnings"].append(f"'{name}': {exc}; it will use the default schedule")
        plan["rules"].append(name)

    if not request.apply:
        plan["note"] = "preview only -- nothing was changed"
        return plan

    await _apply(data, state, replace_rules=request.replace_rules)
    plan["note"] = "imported; rules are disabled until you enable them"

    if state.source_cache is not None:
        state.source_cache.invalidate_all()
        await state.source_cache.refresh_registry()
    if state.scheduler is not None:
        await state.scheduler.sync()
    return plan


async def _apply(data: dict[str, Any], state: StateDep, *, replace_rules: bool) -> None:
    snapshot = state.snapshot.current if state.snapshot else None

    async with session_scope(state.session_factory) as session:
        existing_sources = {
            s.name: s for s in (await session.execute(select(SourceConnection))).scalars().all()
        }

        for entry in data.get("sources") or []:
            name = str(entry.get("name") or "").strip()
            kind = str(entry.get("kind") or "")
            if not name or kind not in set(SourceKind):
                continue
            row = existing_sources.get(name)
            if row is None:
                row = SourceConnection(name=name, kind=SourceKind(kind), base_url="")
                session.add(row)
                existing_sources[name] = row
            row.kind = SourceKind(kind)
            row.base_url = str(entry.get("base_url") or "")
            row.username = str(entry.get("username") or "")
            row.enabled = bool(entry.get("enabled", True))
            row.timeout_seconds = int(entry.get("timeout_seconds", 30))
            row.verify_ssl = bool(entry.get("verify_ssl", True))
            row.options = entry.get("options") or {}
            secret = entry.get("secret")
            if secret and secret != REDACTED:
                ciphertext, nonce = state.protector.protect(str(secret))
                row.secret_ciphertext = ciphertext
                row.secret_nonce = nonce

        existing_storage = {
            s.name: s for s in (await session.execute(select(StoragePath))).scalars().all()
        }
        for entry in data.get("storage_paths") or []:
            name = str(entry.get("name") or "").strip()
            if not name or not entry.get("path"):
                continue
            row = existing_storage.get(name)
            if row is None:
                row = StoragePath(name=name, path=str(entry["path"]))
                session.add(row)
            row.path = str(entry["path"])
            row.enabled = bool(entry.get("enabled", True))
            row.measure_folder_size = bool(entry.get("measure_folder_size", False))
            row.folder_scan_interval_minutes = int(
                entry.get("folder_scan_interval_minutes", 360)
            )

        await session.flush()

        for mapping in data.get("path_mappings") or []:
            if not (mapping.get("from_prefix") and mapping.get("to_prefix")):
                continue
            source_name = mapping.get("source")
            source_row = existing_sources.get(source_name) if source_name else None
            session.add(
                PathMapping(
                    source_id=source_row.id if source_row else None,
                    from_prefix=str(mapping["from_prefix"]),
                    to_prefix=str(mapping["to_prefix"]),
                    order=int(mapping.get("order", 0)),
                )
            )

        if replace_rules:
            for rule in (await session.execute(select(Rule))).scalars().all():
                await session.delete(rule)
            await session.flush()

        next_order = len((await session.execute(select(Rule))).scalars().all())
        for entry in data.get("rules") or []:
            name = str(entry.get("name") or "").strip()
            if not name:
                continue
            try:
                cron, _ = clamp(str(entry.get("cron") or "*/15 * * * *"))
            except CronError:
                cron = "*/15 * * * *"

            target_ids = [
                existing_sources[t].id
                for t in (entry.get("targets") or [])
                if t in existing_sources
            ]

            mode = str(entry.get("condition_mode") or "builder")
            condition_mode = (
                ConditionMode(mode) if mode in set(ConditionMode) else ConditionMode.BUILDER
            )
            # Compiled here for the same reason the editor compiles on save: an
            # imported rule that cannot compile should say so in the rule list,
            # not the first time it is due to run.
            try:
                spec = ConditionSpec(
                    mode=condition_mode,
                    tree=(
                        parse_tree(entry.get("condition_tree"))
                        if condition_mode is not ConditionMode.RAW_SQL
                        else None
                    ),
                    raw_sql=entry.get("raw_sql"),
                    source_ids=target_ids,
                )
                compiled = compile_for_storage(spec, snapshot)
            except ValueError as exc:
                compiled = StoredCompilation(None, [], False, str(exc))

            rule = Rule(
                name=name,
                description=str(entry.get("description") or ""),
                # Always disabled: a shared rule set must not start acting on
                # someone's library the moment it is imported.
                enabled=False,
                order=next_order,
                condition_mode=condition_mode,
                condition_tree=entry.get("condition_tree"),
                raw_sql=entry.get("raw_sql"),
                target_source_ids=target_ids,
                compiled_sql=compiled.sql,
                compiled_params=compiled.params,
                compile_valid=compiled.valid,
                compile_error=compiled.error,
                compiled_at=datetime.now(UTC),
                cron=cron,
                timezone=entry.get("timezone"),
                dry_run=entry.get("dry_run"),
                stop_on_match=entry.get("stop_on_match"),
                cooldown_seconds=entry.get("cooldown_seconds"),
            )
            for position, action in enumerate(entry.get("actions") or []):
                handler = get_handler(str(action.get("type")))
                if handler is None:
                    continue
                try:
                    params = handler.validate_params(action.get("params") or {}).model_dump()
                except Exception:  # noqa: BLE001 - drop the action, keep the rule
                    log.warning("import.bad_action", rule=name, action=action.get("type"))
                    continue
                rule.actions.append(
                    RuleAction(order=position, type=str(action["type"]), params=params)
                )
            session.add(rule)
            next_order += 1
