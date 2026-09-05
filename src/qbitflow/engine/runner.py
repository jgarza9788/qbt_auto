"""Executes rules against a snapshot and applies the resulting actions.

The shape of a run:

1. Refresh whatever sources are stale and rebuild the snapshot, once.
2. For each enabled rule in order, evaluate its condition -- one SQL query
   returning the whole matched set.
3. Ask the rule's handlers what each matched torrent needs. Bidirectional
   handlers also get a targeted reverse set, found by a second query rather than
   a walk over every torrent.
4. Group the resulting intents and flush them as batched requests.
5. Record what happened.

A run belongs to the application's lifespan, never to the HTTP request that
triggered it. A background task tied to a request is cancelled the moment that
response is returned, which would truncate a run halfway through applying
actions.
"""

from __future__ import annotations

import asyncio
import re
import sqlite3
from collections import defaultdict
from dataclasses import dataclass, field
from datetime import UTC, datetime
from time import perf_counter
from typing import Any

import structlog
from sqlalchemy import delete, select

from qbitflow.actions import ActionContext, ActionIntent, TorrentState, get_handler
from qbitflow.actions.base import ActionDecision, ActionHandler
from qbitflow.conditions.execute import ConditionSpec, evaluate
from qbitflow.db.base import session_scope
from qbitflow.db.models import (
    Rule,
    RunHistory,
    RunLogEntry,
    RunRuleResult,
    ScriptRunMarker,
)
from qbitflow.domain import ActionOutcome, RunStatus, RunTrigger
from qbitflow.engine.cooldown import CooldownTracker
from qbitflow.engine.logbus import RunLogBuffer, RunLogBus
from qbitflow.settings import EngineSettings
from qbitflow.snapshot.builder import Snapshot, SnapshotBuilder
from qbitflow.state import AppState

log = structlog.get_logger(__name__)

_PLACEHOLDER = re.compile(r"\{([a-zA-Z_][a-zA-Z0-9_]*)\}")

RowKey = tuple[int, str]


class PlaceholderError(ValueError):
    pass


def substitute(template: str, row: sqlite3.Row) -> str:
    """Fill ``{column}`` placeholders from the torrent's snapshot row.

    An unknown placeholder is an error rather than a silent empty string: a rule
    moving torrents to ``/media/{categoy}`` should fail loudly, not quietly file
    everything under ``/media/``.
    """

    def replace(match: re.Match[str]) -> str:
        key = match.group(1)
        try:
            value = row[key]
        except (IndexError, KeyError) as exc:
            raise PlaceholderError(
                f"unknown placeholder '{{{key}}}' -- no snapshot column has that name"
            ) from exc
        return "" if value is None else str(value)

    return _PLACEHOLDER.sub(replace, template)


def _resolve_params(params: dict[str, Any], row: sqlite3.Row) -> dict[str, Any]:
    return {
        key: substitute(value, row) if isinstance(value, str) else value
        for key, value in params.items()
    }


@dataclass(slots=True)
class RuleOutcome:
    rule_id: int | None
    rule_name: str
    matched: int = 0
    applied: int = 0
    would_apply: int = 0
    skipped: int = 0
    errors: int = 0
    duration_ms: int = 0
    error: str | None = None
    sql: str = ""


@dataclass(slots=True)
class RunResult:
    run_id: int | None = None
    status: RunStatus = RunStatus.SUCCEEDED
    dry_run: bool = False
    trigger: RunTrigger = RunTrigger.MANUAL
    started_at: datetime = field(default_factory=lambda: datetime.now(UTC))
    duration_ms: int = 0
    torrents_evaluated: int = 0
    rules_evaluated: int = 0
    torrents_matched: int = 0
    actions_applied: int = 0
    actions_would_apply: int = 0
    actions_skipped: int = 0
    error_count: int = 0
    rule_outcomes: list[RuleOutcome] = field(default_factory=list)
    source_errors: list[str] = field(default_factory=list)
    #: True when the run did not start because one was already in flight.
    skipped_overlap: bool = False


class RuleRunner:
    def __init__(self, state: AppState, log_bus: RunLogBus | None = None) -> None:
        self.state = state
        self.bus = log_bus or RunLogBus()
        self.cooldowns = CooldownTracker()
        self._running: set[int | None] = set()
        self._lock = asyncio.Lock()

    @property
    def is_running(self) -> bool:
        return bool(self._running)

    # -- snapshot -----------------------------------------------------------

    async def refresh_snapshot(self, *, force: bool = False) -> Snapshot:
        """Refresh stale sources and rebuild the snapshot.

        Sources still within their TTL are reused, so a cycle that only needs
        torrents does not re-scan a Plex library.
        """
        settings = await self.state.settings.get_engine_settings()
        cache = self.state.source_cache
        if cache is None:
            raise RuntimeError("source cache is not initialised")

        await cache.refresh_registry(max_concurrency=settings.per_host_connection_cap)
        if force:
            cache.invalidate_all()

        adapters = cache.adapters()
        ttls = {sid: settings.ttl_for(adapter.kind) for sid, adapter in adapters.items()}
        payloads = await cache.get_many(adapters.keys(), ttls, workers=settings.workers)

        mapper = await cache.path_mapper()
        snapshot = await SnapshotBuilder(mapper).build(payloads)
        if self.state.snapshot is not None:
            await self.state.snapshot.publish(snapshot)
        return snapshot

    # -- running ------------------------------------------------------------

    async def run(
        self,
        *,
        trigger: RunTrigger = RunTrigger.MANUAL,
        rule_ids: list[int] | None = None,
        dry_run_override: bool | None = None,
        force_refresh: bool = False,
    ) -> RunResult:
        key = rule_ids[0] if rule_ids and len(rule_ids) == 1 else None
        async with self._lock:
            if key in self._running:
                # Overlap protection: a rule already in flight does not start
                # again. The tick is skipped rather than queued, so a slow run
                # cannot build a backlog that never drains.
                log.info("run.skipped_overlap", rule_id=key)
                return RunResult(
                    status=RunStatus.CANCELLED, trigger=trigger, skipped_overlap=True
                )
            self._running.add(key)

        try:
            return await self._run(trigger, rule_ids, dry_run_override, force_refresh)
        finally:
            async with self._lock:
                self._running.discard(key)

    async def _run(
        self,
        trigger: RunTrigger,
        rule_ids: list[int] | None,
        dry_run_override: bool | None,
        force_refresh: bool,
    ) -> RunResult:
        started = perf_counter()
        settings = await self.state.settings.get_engine_settings()
        dry_run = dry_run_override if dry_run_override is not None else settings.dry_run

        rules = await self._load_rules(rule_ids)
        result = RunResult(
            trigger=trigger, dry_run=dry_run, rules_evaluated=len(rules)
        )
        if not rules:
            # An idle install should not accumulate empty run rows.
            result.duration_ms = int((perf_counter() - started) * 1000)
            return result

        run_id = await self._create_run_row(trigger, dry_run, rule_ids)
        result.run_id = run_id
        buffer = RunLogBuffer(run_id, self.bus)
        result.status = RunStatus.RUNNING

        try:
            snapshot = await self.refresh_snapshot(force=force_refresh)
            result.source_errors = list(snapshot.stats.source_errors)
            result.torrents_evaluated = snapshot.stats.torrents
            for message in snapshot.stats.source_errors:
                buffer.emit(message, level="error")
                result.error_count += 1

            # Every torrent, once, into a dict -- rules then index into it rather
            # than issuing a lookup per torrent per rule.
            rows = self._load_rows(snapshot)
            markers = await self._load_script_markers()
            stopped: set[RowKey] = set()

            for rule in rules:
                outcome = await self._run_rule(
                    rule, snapshot, rows, markers, dry_run, settings, buffer, stopped
                )
                result.rule_outcomes.append(outcome)
                result.torrents_matched += outcome.matched
                result.actions_applied += outcome.applied
                result.actions_would_apply += outcome.would_apply
                result.actions_skipped += outcome.skipped
                result.error_count += outcome.errors

            result.status = RunStatus.SUCCEEDED
        except Exception as exc:  # noqa: BLE001 - a run must always be finalised
            log.exception("run.failed", run_id=run_id)
            buffer.emit(f"run failed: {exc}", level="error")
            result.status = RunStatus.FAILED
            result.error_count += 1
        finally:
            result.duration_ms = int((perf_counter() - started) * 1000)
            await self._finalise(run_id, result, buffer)
            await self._prune_runs(settings.run_retention)
            self.bus.finish(run_id)

        return result

    async def _load_rules(self, rule_ids: list[int] | None) -> list[Rule]:
        async with self.state.session_factory() as session:
            query = select(Rule).where(Rule.enabled.is_(True)).order_by(Rule.order, Rule.id)
            if rule_ids:
                query = query.where(Rule.id.in_(rule_ids))
            return list((await session.execute(query)).scalars().all())

    @staticmethod
    def _load_rows(snapshot: Snapshot) -> dict[RowKey, sqlite3.Row]:
        return {
            (int(r["source_id"]), str(r["hash"])): r
            for r in snapshot.query("SELECT * FROM torrents")
        }

    async def _load_script_markers(self) -> set[tuple[int, str]]:
        async with self.state.session_factory() as session:
            rows = (
                await session.execute(select(ScriptRunMarker.rule_id, ScriptRunMarker.torrent_hash))
            ).all()
        return {(int(r[0]), str(r[1])) for r in rows}

    # -- one rule -----------------------------------------------------------

    async def _run_rule(  # noqa: C901 - the sequence is the algorithm; splitting hides it
        self,
        rule: Rule,
        snapshot: Snapshot,
        rows: dict[RowKey, sqlite3.Row],
        markers: set[tuple[int, str]],
        global_dry_run: bool,
        settings: EngineSettings,
        buffer: RunLogBuffer,
        stopped: set[RowKey],
    ) -> RuleOutcome:
        started = perf_counter()
        outcome = RuleOutcome(rule_id=rule.id, rule_name=rule.name)
        dry_run = rule.dry_run if rule.dry_run is not None else global_dry_run

        match_set = evaluate(snapshot, ConditionSpec.from_rule(rule))
        outcome.sql = match_set.sql
        if not match_set.ok:
            outcome.error = match_set.error
            outcome.errors += 1
            buffer.emit(
                f"rule '{rule.name}' could not be evaluated: {match_set.error}",
                level="error",
                rule_id=rule.id,
            )
            outcome.duration_ms = int((perf_counter() - started) * 1000)
            return outcome

        matched_set = set(match_set.matched)
        matched = [k for k in match_set.matched if k not in stopped]
        outcome.matched = len(matched)

        if match_set.truncated:
            buffer.emit(
                f"rule '{rule.name}' hit the row limit; results were truncated",
                level="warning",
                rule_id=rule.id,
            )

        intents: list[ActionIntent] = []
        direct_jobs: list[tuple[ActionHandler, ActionContext]] = []

        for action in rule.actions:
            handler = get_handler(action.type)
            if handler is None:
                # Forward compatibility: an action type from a newer version
                # drops that action rather than failing the run.
                buffer.emit(
                    f"rule '{rule.name}': unknown action '{action.type}', skipped",
                    level="warning",
                    rule_id=rule.id,
                )
                continue

            try:
                base_params = handler.validate_params(dict(action.params or {})).model_dump()
            except Exception as exc:  # noqa: BLE001 - a bad rule, not a bad run
                outcome.errors += 1
                buffer.emit(
                    f"rule '{rule.name}': action '{action.type}' has invalid parameters: {exc}",
                    level="error",
                    rule_id=rule.id,
                )
                continue

            targets: list[tuple[RowKey, bool]] = [(k, True) for k in matched]
            if handler.bidirectional:
                targets += [
                    (k, False)
                    for k in self._inverse_set(snapshot, handler, base_params, rule)
                    if k not in matched_set
                ]

            for key, is_match in targets:
                row = rows.get(key)
                if row is None:
                    continue

                # Cooldowns gate real firings only. A dry run must not consume a
                # cooldown, nor be blocked by one -- previewing what a rule would
                # do cannot be allowed to change what it will do.
                if (
                    is_match
                    and not dry_run
                    and rule.id is not None
                    and not self.cooldowns.try_fire(rule.id, key[1], rule.cooldown_seconds)
                ):
                    outcome.skipped += 1
                    continue

                try:
                    params = _resolve_params(base_params, row)
                except PlaceholderError as exc:
                    outcome.errors += 1
                    buffer.emit(
                        f"rule '{rule.name}': {exc}",
                        level="error",
                        torrent_hash=key[1],
                        rule_id=rule.id,
                    )
                    continue

                ctx = ActionContext(
                    rule_id=rule.id,
                    rule_name=rule.name,
                    match=is_match,
                    torrent=TorrentState(row),
                    params=params,
                    dry_run=dry_run,
                    client=self._client_for(key[0]),
                    extras={
                        "exports_dir": str(self.state.config.exports_dir),
                        "script_enabled": self.state.config.enable_script_action,
                        "already_ran": (rule.id, key[1]) in markers,
                    },
                )

                try:
                    decision = handler.decide(ctx)
                except Exception as exc:  # noqa: BLE001 - one torrent, not the run
                    outcome.errors += 1
                    buffer.emit(
                        f"rule '{rule.name}': action '{action.type}' failed for "
                        f"{row['name']}: {exc}",
                        level="error",
                        torrent_hash=key[1],
                        rule_id=rule.id,
                    )
                    continue

                if decision.intent is not None:
                    intents.append(decision.intent)
                    self._log_decision(buffer, rule, row, decision)
                elif handler.direct and decision.outcome is ActionOutcome.APPLIED:
                    direct_jobs.append((handler, ctx))
                else:
                    self._tally(outcome, decision)
                    self._log_decision(buffer, rule, row, decision)

        applied, failed = await self._flush(intents, settings, buffer, rule)
        outcome.applied += applied
        outcome.errors += failed

        direct_applied, direct_failed = await self._run_direct(direct_jobs, buffer, rule)
        outcome.applied += direct_applied
        outcome.errors += direct_failed

        if rule.stop_on_match if rule.stop_on_match is not None else settings.stop_on_first_match:
            stopped.update(matched)

        outcome.duration_ms = int((perf_counter() - started) * 1000)
        buffer.emit(
            f"rule '{rule.name}': matched {outcome.matched}, applied {outcome.applied}, "
            f"would apply {outcome.would_apply}, skipped {outcome.skipped}, "
            f"errors {outcome.errors}",
            rule_id=rule.id,
        )
        await self._record_rule_run(rule, outcome)
        return outcome

    @staticmethod
    def _log_decision(
        buffer: RunLogBuffer, rule: Rule, row: sqlite3.Row, decision: ActionDecision
    ) -> None:
        if decision.outcome is ActionOutcome.NOT_APPLICABLE or not decision.message:
            return
        level = "error" if decision.outcome is ActionOutcome.ERROR else "info"
        buffer.emit(
            f"{row['name']}: {decision.message}",
            level=level,
            torrent_hash=str(row["hash"]),
            rule_id=rule.id,
        )

    def _inverse_set(
        self,
        snapshot: Snapshot,
        handler: ActionHandler,
        params: dict[str, Any],
        rule: Rule,
    ) -> list[RowKey]:
        """Torrents that may need the action undone.

        A targeted query, so a bidirectional rule costs one extra indexed lookup
        rather than a pass over every torrent in the client.
        """
        predicate = handler.inverse_predicate(params)
        if predicate is None:
            return []
        sql, sql_params = predicate
        query = f"SELECT t.source_id, t.hash FROM torrents t WHERE {sql}"
        if rule.target_source_ids:
            placeholders = ",".join("?" * len(rule.target_source_ids))
            query += f" AND t.source_id IN ({placeholders})"
            sql_params = [*sql_params, *rule.target_source_ids]
        try:
            rows = snapshot.query(query, sql_params)
        except sqlite3.DatabaseError:
            log.exception("run.inverse_query_failed", rule=rule.name)
            return []
        return [(int(r["source_id"]), str(r["hash"])) for r in rows]

    @staticmethod
    def _tally(outcome: RuleOutcome, decision: ActionDecision) -> None:
        match decision.outcome:
            case ActionOutcome.WOULD_APPLY:
                outcome.would_apply += 1
            case ActionOutcome.SKIPPED:
                outcome.skipped += 1
            case ActionOutcome.ERROR:
                outcome.errors += 1
            case ActionOutcome.APPLIED:
                outcome.applied += 1
            case _:
                pass  # NOT_APPLICABLE is not an event worth counting.

    def _client_for(self, source_id: int):  # noqa: ANN202 - QbtClient, avoiding an import cycle
        cache = self.state.source_cache
        if cache is None:
            return None
        adapter = cache.adapter(source_id)
        return getattr(adapter, "client", None)

    # -- applying -----------------------------------------------------------

    async def _flush(
        self,
        intents: list[ActionIntent],
        settings: EngineSettings,
        buffer: RunLogBuffer,
        rule: Rule,
    ) -> tuple[int, int]:
        """Send one request per (instance, operation, arguments) group.

        Tagging 500 torrents becomes a handful of requests instead of 500.
        """
        if not intents:
            return 0, 0

        groups: dict[tuple[int, str, tuple], list[str]] = defaultdict(list)
        for intent in intents:
            groups[intent.batch_key].append(intent.torrent_hash)

        applied = 0
        failed = 0
        batch_size = settings.action_batch_size

        for (source_id, op, args), hashes in groups.items():
            client = self._client_for(source_id)
            if client is None:
                failed += len(hashes)
                buffer.emit(
                    f"rule '{rule.name}': qBittorrent instance {source_id} is unavailable; "
                    f"{len(hashes)} action(s) not applied",
                    level="error",
                    rule_id=rule.id,
                )
                continue

            for start in range(0, len(hashes), batch_size):
                chunk = hashes[start : start + batch_size]
                try:
                    await _dispatch(client, op, args, chunk)
                except Exception as exc:  # noqa: BLE001 - one batch, not the run
                    failed += len(chunk)
                    buffer.emit(
                        f"rule '{rule.name}': '{op}' failed for {len(chunk)} torrent(s): {exc}",
                        level="error",
                        rule_id=rule.id,
                    )
                else:
                    applied += len(chunk)

        return applied, failed

    async def _run_direct(
        self,
        jobs: list[tuple[ActionHandler, ActionContext]],
        buffer: RunLogBuffer,
        rule: Rule,
    ) -> tuple[int, int]:
        """Handlers that do their own I/O -- exporting a file, running a command."""
        applied = 0
        failed = 0
        for handler, ctx in jobs:
            try:
                decision = await handler.perform(ctx)
            except Exception as exc:  # noqa: BLE001 - one torrent, not the run
                failed += 1
                buffer.emit(
                    f"rule '{rule.name}': '{handler.type}' failed for {ctx.torrent.name}: {exc}",
                    level="error",
                    torrent_hash=ctx.torrent.hash,
                    rule_id=rule.id,
                )
                continue

            if decision.outcome is ActionOutcome.APPLIED:
                applied += 1
                if handler.type == "script.run" and ctx.params.get("run_once_per_torrent", True):
                    await self._record_script_marker(ctx)
            elif decision.outcome is ActionOutcome.ERROR:
                failed += 1
            buffer.emit(
                f"{ctx.torrent.name}: {decision.message}",
                level="error" if decision.outcome is ActionOutcome.ERROR else "info",
                torrent_hash=ctx.torrent.hash,
                rule_id=rule.id,
            )
        return applied, failed

    async def _record_script_marker(self, ctx: ActionContext) -> None:
        if ctx.rule_id is None:
            return
        async with session_scope(self.state.session_factory) as session:
            session.add(
                ScriptRunMarker(
                    rule_id=ctx.rule_id,
                    torrent_hash=ctx.torrent.hash,
                    run_dir=str(ctx.params.get("working_dir") or ""),
                )
            )

    # -- persistence --------------------------------------------------------

    async def _create_run_row(
        self, trigger: RunTrigger, dry_run: bool, rule_ids: list[int] | None
    ) -> int:
        async with session_scope(self.state.session_factory) as session:
            run = RunHistory(
                trigger=trigger,
                rule_id=rule_ids[0] if rule_ids and len(rule_ids) == 1 else None,
                dry_run=dry_run,
                status=RunStatus.RUNNING,
                started_at=datetime.now(UTC),
            )
            session.add(run)
            await session.flush()
            return run.id

    async def _record_rule_run(self, rule: Rule, outcome: RuleOutcome) -> None:
        async with session_scope(self.state.session_factory) as session:
            row = await session.get(Rule, rule.id)
            if row is not None:
                row.last_run_at = datetime.now(UTC)
                row.last_matched_count = outcome.matched

    async def _finalise(self, run_id: int, result: RunResult, buffer: RunLogBuffer) -> None:
        async with session_scope(self.state.session_factory) as session:
            run = await session.get(RunHistory, run_id)
            if run is None:
                return
            run.status = result.status
            run.finished_at = datetime.now(UTC)
            run.duration_ms = result.duration_ms
            run.torrents_evaluated = result.torrents_evaluated
            run.rules_evaluated = result.rules_evaluated
            run.torrents_matched = result.torrents_matched
            run.actions_applied = result.actions_applied
            run.actions_would_apply = result.actions_would_apply
            run.actions_skipped = result.actions_skipped
            run.error_count = result.error_count
            run.summary = {"source_errors": result.source_errors}

            for outcome in result.rule_outcomes:
                session.add(
                    RunRuleResult(
                        run_id=run_id,
                        rule_id=outcome.rule_id,
                        # Denormalised so history stays readable after the rule
                        # is deleted.
                        rule_name=outcome.rule_name,
                        matched_count=outcome.matched,
                        actions_applied=outcome.applied,
                        actions_would_apply=outcome.would_apply,
                        actions_skipped=outcome.skipped,
                        error_count=outcome.errors,
                        duration_ms=outcome.duration_ms,
                        error=outcome.error,
                    )
                )

            # One bulk insert rather than a transaction per log line.
            for line in buffer.lines:
                session.add(
                    RunLogEntry(
                        run_id=line.run_id,
                        seq=line.seq,
                        timestamp=line.timestamp,
                        level=line.level,
                        message=line.message,
                        torrent_hash=line.torrent_hash,
                        rule_id=line.rule_id,
                    )
                )

    async def _prune_runs(self, keep: int) -> None:
        """Bounded history. Best-effort: pruning must never fail a run."""
        try:
            async with session_scope(self.state.session_factory) as session:
                cutoff = (
                    await session.execute(
                        select(RunHistory.id)
                        .order_by(RunHistory.started_at.desc())
                        .limit(1)
                        .offset(keep)
                    )
                ).scalar_one_or_none()
                if cutoff is None:
                    return
                await session.execute(delete(RunHistory).where(RunHistory.id <= cutoff))
        except Exception:  # noqa: BLE001 - housekeeping, not correctness
            log.warning("run.prune_failed", exc_info=True)


async def _dispatch(client, op: str, args: tuple, hashes: list[str]) -> None:  # noqa: ANN001
    """Map a batched intent onto the qBittorrent client."""
    match op:
        case "add_tags":
            await client.add_tags(hashes, [args[0]])
        case "remove_tags":
            await client.remove_tags(hashes, [args[0]])
        case "set_category":
            category, enable_auto = args
            await client.set_category(hashes, category)
            if enable_auto:
                # Auto-management is what makes qBittorrent actually relocate the
                # files into the category's save path.
                await client.set_auto_management(hashes, True)
        case "set_location":
            # Auto-management has to come off first, or qBittorrent moves the
            # torrent straight back to where its category says it belongs.
            await client.set_auto_management(hashes, False)
            await client.set_location(hashes, args[0])
        case "set_upload_limit":
            await client.set_upload_limit(hashes, args[0])
        case "set_download_limit":
            await client.set_download_limit(hashes, args[0])
        case "set_force_start":
            await client.set_force_start(hashes, args[0])
        case "start":
            await client.start(hashes)
        case "stop":
            await client.stop(hashes)
        case _:
            raise ValueError(f"unknown batched operation '{op}'")
