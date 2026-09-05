"""Per-rule cron scheduling.

Each enabled rule gets its own APScheduler job. Overlap is prevented twice over:
``max_instances=1`` stops APScheduler firing a job that is still running, and the
runner refuses a second concurrent run of the same rule regardless of who asks.
A tick that arrives during a slow run is dropped rather than queued -- a backlog
of identical runs helps nobody.

Jobs live on the application's scheduler, which belongs to the lifespan. Nothing
here is tied to an HTTP request, so a manually triggered run is not cancelled
when its response returns.
"""

from __future__ import annotations

from datetime import UTC, datetime
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

import structlog
from apscheduler.schedulers.asyncio import AsyncIOScheduler
from apscheduler.triggers.cron import CronTrigger
from apscheduler.triggers.interval import IntervalTrigger
from sqlalchemy import delete, select

from qbitflow.db.base import session_scope
from qbitflow.db.models import Rule, Session
from qbitflow.domain import RunTrigger
from qbitflow.engine.runner import RuleRunner
from qbitflow.engine.schedule_text import CronError, clamp
from qbitflow.state import AppState

log = structlog.get_logger(__name__)

JOB_PREFIX = "rule:"
MAINTENANCE_JOB = "maintenance"


def _zone(name: str | None, fallback: str) -> ZoneInfo:
    for candidate in (name, fallback, "UTC"):
        if not candidate:
            continue
        try:
            return ZoneInfo(candidate)
        except (ZoneInfoNotFoundError, ValueError):
            continue
    return ZoneInfo("UTC")


class RuleScheduler:
    def __init__(self, state: AppState, runner: RuleRunner) -> None:
        self.state = state
        self.runner = runner
        self._scheduler = AsyncIOScheduler(timezone="UTC")
        self._started = False

    @property
    def running(self) -> bool:
        return self._started

    async def start(self) -> None:
        if self._started:
            return
        self._scheduler.start()
        self._started = True
        self._scheduler.add_job(
            self._maintenance,
            IntervalTrigger(hours=6),
            id=MAINTENANCE_JOB,
            max_instances=1,
            coalesce=True,
        )
        await self.sync()
        log.info("scheduler.started")

    async def shutdown(self) -> None:
        if not self._started:
            return
        self._scheduler.shutdown(wait=False)
        self._started = False
        log.info("scheduler.stopped")

    # -- scheduling ---------------------------------------------------------

    async def sync(self) -> None:
        """Rebuild the job list from the database.

        Called at startup and whenever rules change, so an edit takes effect
        without a restart.
        """
        if not self._started:
            return

        settings = await self.state.settings.get_engine_settings()
        async with self.state.session_factory() as session:
            rules = list(
                (
                    await session.execute(
                        select(Rule).where(Rule.enabled.is_(True)).order_by(Rule.order)
                    )
                ).scalars().all()
            )

        wanted: set[str] = set()
        for rule in rules:
            job_id = f"{JOB_PREFIX}{rule.id}"
            wanted.add(job_id)
            try:
                cron, warning = clamp(rule.cron)
            except CronError as exc:
                log.warning("scheduler.bad_cron", rule=rule.name, cron=rule.cron, error=str(exc))
                continue
            if warning:
                log.info("scheduler.clamped", rule=rule.name, cron=cron)

            self._scheduler.add_job(
                self._fire,
                CronTrigger.from_crontab(
                    cron, timezone=_zone(rule.timezone, settings.timezone)
                ),
                id=job_id,
                args=[rule.id, rule.name],
                replace_existing=True,
                # APScheduler's own overlap guard; the runner has a second one.
                max_instances=1,
                # A run missed while the process was down fires once, not once
                # per missed interval.
                coalesce=True,
                misfire_grace_time=300,
            )

        for job in self._scheduler.get_jobs():
            if job.id.startswith(JOB_PREFIX) and job.id not in wanted:
                self._scheduler.remove_job(job.id)

        log.info("scheduler.synced", rules=len(wanted))

    async def _fire(self, rule_id: int, rule_name: str) -> None:
        settings = await self.state.settings.get_engine_settings()
        if not settings.engine_enabled:
            # The global kill switch stops new runs starting. A run already in
            # flight is left to finish rather than torn up mid-action.
            log.debug("scheduler.engine_disabled", rule=rule_name)
            return

        result = await self.runner.run(trigger=RunTrigger.SCHEDULE, rule_ids=[rule_id])
        if result.skipped_overlap:
            log.info("scheduler.overlap", rule=rule_name)

    # -- housekeeping -------------------------------------------------------

    async def _maintenance(self) -> None:
        """Expired sessions and anything else that should not grow forever."""
        try:
            async with session_scope(self.state.session_factory) as session:
                await session.execute(
                    delete(Session).where(Session.expires_at < datetime.now(UTC))
                )
        except Exception:  # noqa: BLE001 - housekeeping never fails the app
            log.warning("scheduler.maintenance_failed", exc_info=True)

    # -- introspection ------------------------------------------------------

    def next_run_for(self, rule_id: int) -> datetime | None:
        job = self._scheduler.get_job(f"{JOB_PREFIX}{rule_id}")
        return getattr(job, "next_run_time", None) if job else None

    def scheduled_rule_ids(self) -> list[int]:
        return [
            int(job.id.removeprefix(JOB_PREFIX))
            for job in self._scheduler.get_jobs()
            if job.id.startswith(JOB_PREFIX)
        ]
