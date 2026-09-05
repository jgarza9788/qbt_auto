"""Engine control and run history, including the live log stream."""

from __future__ import annotations

import asyncio
import json
from collections.abc import AsyncIterator
from typing import Any

from fastapi import APIRouter, HTTPException, Query
from fastapi.responses import StreamingResponse
from pydantic import BaseModel
from sqlalchemy import func, select

from qbitflow.api.deps import RunnerDep, StateDep
from qbitflow.db.models import Rule, RunHistory, RunLogEntry, RunRuleResult
from qbitflow.domain import ParallelismTier, RunTrigger
from qbitflow.settings import EngineSettings

router = APIRouter(prefix="/api", tags=["engine"])

#: Sent when nothing has happened for a while, so proxies do not close the stream.
HEARTBEAT_SECONDS = 20


class RunRequest(BaseModel):
    rule_id: int | None = None
    dry_run: bool | None = None
    force_refresh: bool = False


@router.get("/engine")
async def engine_status(state: StateDep) -> dict[str, Any]:
    settings = await state.settings.get_engine_settings()
    snapshot = state.snapshot.current if state.snapshot else None

    async with state.session_factory() as session:
        total = (await session.execute(select(func.count()).select_from(Rule))).scalar() or 0
        enabled = (
            await session.execute(
                select(func.count()).select_from(Rule).where(Rule.enabled.is_(True))
            )
        ).scalar() or 0

    return {
        "enabled": settings.engine_enabled,
        "dry_run": settings.dry_run,
        "parallelism": str(settings.parallelism),
        "workers": settings.workers,
        "running": state.runner.is_running if state.runner else False,
        "scheduler_running": state.scheduler.running if state.scheduler else False,
        "rules_total": total,
        "rules_enabled": enabled,
        "startup_error": state.startup_error,
        "snapshot": (
            {
                "built_at": snapshot.stats.built_at.isoformat(),
                "duration_ms": snapshot.stats.duration_ms,
                "torrents": snapshot.stats.torrents,
                "media_items": snapshot.stats.media_items,
                "matched_torrents": snapshot.stats.matched_torrents,
                "storage_paths": snapshot.stats.storage_paths,
                "source_errors": snapshot.stats.source_errors,
            }
            if snapshot
            else None
        ),
    }


@router.post("/engine/run")
async def run_now(
    state: StateDep, runner: RunnerDep, request: RunRequest | None = None
) -> dict[str, Any]:
    """Trigger a run.

    Every field is optional and so is the body itself: "run everything now"
    is the common case, and it should not require a caller to post ``{}``.

    Awaited rather than fired into a request-scoped background task: a task tied
    to this request would be cancelled the moment the response is returned,
    leaving actions half-applied.
    """
    request = request or RunRequest()
    result = await runner.run(
        trigger=RunTrigger.API,
        rule_ids=[request.rule_id] if request.rule_id else None,
        dry_run_override=request.dry_run,
        force_refresh=request.force_refresh,
    )
    if result.skipped_overlap:
        return {"started": False, "reason": "a run is already in progress"}
    return {
        "started": True,
        "run_id": result.run_id,
        "status": str(result.status),
        "dry_run": result.dry_run,
        "torrents_evaluated": result.torrents_evaluated,
        "torrents_matched": result.torrents_matched,
        "actions_applied": result.actions_applied,
        "actions_would_apply": result.actions_would_apply,
        "actions_skipped": result.actions_skipped,
        "errors": result.error_count,
        "duration_ms": result.duration_ms,
    }


@router.post("/engine/refresh")
async def refresh_now(runner: RunnerDep) -> dict[str, Any]:
    """Rebuild the snapshot immediately, ignoring every source's TTL."""
    snapshot = await runner.refresh_snapshot(force=True)
    return {
        "torrents": snapshot.stats.torrents,
        "media_items": snapshot.stats.media_items,
        "storage_paths": snapshot.stats.storage_paths,
        "duration_ms": snapshot.stats.duration_ms,
        "source_errors": snapshot.stats.source_errors,
    }


class ToggleRequest(BaseModel):
    enabled: bool


@router.post("/engine/enabled")
async def set_enabled(request: ToggleRequest, state: StateDep) -> dict[str, Any]:
    """The global kill switch.

    Stops new runs starting. A run already in flight finishes rather than being
    torn up partway through applying actions.
    """
    settings = await state.settings.get_engine_settings()
    saved = await state.settings.save_engine_settings(
        settings.model_copy(update={"engine_enabled": request.enabled})
    )
    return {"enabled": saved.engine_enabled}


# --------------------------------------------------------------------------- #
# Settings
# --------------------------------------------------------------------------- #


class SettingsIn(BaseModel):
    engine_enabled: bool | None = None
    dry_run: bool | None = None
    parallelism: ParallelismTier | None = None
    stop_on_first_match: bool | None = None
    source_ttl_overrides: dict[str, int] | None = None
    run_retention: int | None = None
    per_host_connection_cap: int | None = None
    action_batch_size: int | None = None
    timezone: str | None = None


@router.get("/settings")
async def get_settings(state: StateDep) -> dict[str, Any]:
    settings = await state.settings.get_engine_settings()
    return settings.model_dump() | {"workers": settings.workers}


@router.post("/settings")
async def save_settings(payload: SettingsIn, state: StateDep) -> dict[str, Any]:
    """Persist settings, returning what was actually stored.

    Values out of range are clamped rather than rejected, and the response shows
    the clamped value -- so the UI can say "your 2 seconds became 15" instead of
    pretending the input took.
    """
    current = await state.settings.get_engine_settings()
    updates = {k: v for k, v in payload.model_dump().items() if v is not None}
    saved = await state.settings.save_engine_settings(current.model_copy(update=updates))

    if state.scheduler is not None:
        await state.scheduler.sync()
    return saved.model_dump() | {
        "workers": saved.workers,
        "clamped": saved.model_dump() != current.model_copy(update=updates).model_dump(),
    }


# --------------------------------------------------------------------------- #
# Runs
# --------------------------------------------------------------------------- #


@router.get("/runs")
async def list_runs(
    state: StateDep,
    limit: int = Query(default=50, ge=1, le=500),
    offset: int = Query(default=0, ge=0),
    status: str | None = None,
) -> dict[str, Any]:
    async with state.session_factory() as session:
        query = select(RunHistory).order_by(RunHistory.started_at.desc())
        count_query = select(func.count()).select_from(RunHistory)
        if status:
            query = query.where(RunHistory.status == status)
            count_query = count_query.where(RunHistory.status == status)

        total = (await session.execute(count_query)).scalar() or 0
        rows = (
            await session.execute(query.limit(limit).offset(offset))
        ).scalars().all()

    return {
        "total": total,
        "limit": limit,
        "offset": offset,
        "runs": [_serialise_run(r) for r in rows],
    }


def _serialise_run(run: RunHistory) -> dict[str, Any]:
    return {
        "id": run.id,
        "trigger": str(run.trigger),
        "status": str(run.status),
        "dry_run": run.dry_run,
        "started_at": run.started_at.isoformat(),
        "finished_at": run.finished_at.isoformat() if run.finished_at else None,
        "duration_ms": run.duration_ms,
        "torrents_evaluated": run.torrents_evaluated,
        "rules_evaluated": run.rules_evaluated,
        "torrents_matched": run.torrents_matched,
        "actions_applied": run.actions_applied,
        "actions_would_apply": run.actions_would_apply,
        "actions_skipped": run.actions_skipped,
        "error_count": run.error_count,
        "summary": run.summary,
    }


@router.get("/runs/{run_id}")
async def run_detail(run_id: int, state: StateDep) -> dict[str, Any]:
    async with state.session_factory() as session:
        run = await session.get(RunHistory, run_id)
        if run is None:
            raise HTTPException(404, "no such run")
        results = (
            await session.execute(
                select(RunRuleResult).where(RunRuleResult.run_id == run_id)
            )
        ).scalars().all()

    return _serialise_run(run) | {
        "rule_results": [
            {
                "rule_id": r.rule_id,
                "rule_name": r.rule_name,
                "matched": r.matched_count,
                "applied": r.actions_applied,
                "would_apply": r.actions_would_apply,
                "skipped": r.actions_skipped,
                "errors": r.error_count,
                "duration_ms": r.duration_ms,
                "error": r.error,
            }
            for r in results
        ]
    }


@router.get("/runs/{run_id}/log")
async def run_log(
    run_id: int, state: StateDep, after: int = 0, limit: int = Query(default=1000, le=5000)
) -> list[dict[str, Any]]:
    async with state.session_factory() as session:
        rows = (
            await session.execute(
                select(RunLogEntry)
                .where(RunLogEntry.run_id == run_id, RunLogEntry.seq > after)
                .order_by(RunLogEntry.seq)
                .limit(limit)
            )
        ).scalars().all()
    return [
        {
            "seq": r.seq,
            "timestamp": r.timestamp.isoformat(),
            "level": r.level,
            "message": r.message,
            "torrent_hash": r.torrent_hash,
            "rule_id": r.rule_id,
        }
        for r in rows
    ]


@router.get("/runs/{run_id}/stream")
async def stream_run_log(run_id: int, state: StateDep, runner: RunnerDep) -> StreamingResponse:
    """Replay what is persisted, then stream what follows.

    Sequence numbers let the client drop anything it has already shown, so the
    endpoint behaves the same whether the run is in flight or already finished.
    """

    async def events() -> AsyncIterator[str]:
        seen = 0
        for entry in await run_log(run_id, state, after=0):
            seen = max(seen, int(entry["seq"]))
            yield f"event: log\ndata: {json.dumps(entry)}\n\n"

        async with state.session_factory() as session:
            run = await session.get(RunHistory, run_id)
            finished = run is not None and run.finished_at is not None
        if finished:
            yield "event: done\ndata: {}\n\n"
            return

        subscription = runner.bus.subscribe(run_id)
        try:
            while True:
                try:
                    line = await asyncio.wait_for(
                        subscription.__anext__(), timeout=HEARTBEAT_SECONDS
                    )
                except TimeoutError:
                    yield ": keep-alive\n\n"
                    continue
                except StopAsyncIteration:
                    break
                if line.seq <= seen:
                    continue
                seen = line.seq
                yield f"event: log\ndata: {json.dumps(line.as_dict())}\n\n"
        finally:
            await subscription.aclose()
        yield "event: done\ndata: {}\n\n"

    return StreamingResponse(
        events(),
        media_type="text/event-stream",
        headers={"Cache-Control": "no-cache", "X-Accel-Buffering": "no"},
    )


@router.get("/settings/defaults")
async def settings_defaults() -> dict[str, Any]:
    """The parallelism mapping, so the UI can label the tiers honestly."""
    from qbitflow.domain import PARALLELISM_WORKERS
    from qbitflow.settings import DEFAULT_SOURCE_TTL_SECONDS, MIN_RULE_INTERVAL_SECONDS

    return {
        "parallelism": {str(k): v for k, v in PARALLELISM_WORKERS.items()},
        "source_ttl_defaults": {str(k): v for k, v in DEFAULT_SOURCE_TTL_SECONDS.items()},
        "min_rule_interval_seconds": MIN_RULE_INTERVAL_SECONDS,
        "engine_defaults": EngineSettings().model_dump(),
    }
