"""Tautulli adapter -- windowed watch history for a Plex server.

Tautulli is the one source with genuinely reliable time-window data, so its
records populate the ``week``/``month``/``year`` counts that rules like "nobody
has watched this in 90 days" depend on.

History is paged newest-first up to ``max_history_records``. The ``all`` window is
only reported when the whole history fit inside that cap -- claiming a lifetime
total from a truncated read would quietly under-count exactly the long-lived
items these rules are about.
"""

from __future__ import annotations

import contextlib
from datetime import UTC, datetime, timedelta
from time import perf_counter
from typing import Any

import structlog

from qbitflow.domain import HealthResult, SourceKind
from qbitflow.sources.base import (
    SourceAdapter,
    SourceContext,
    SourcePayload,
    WatchRecord,
)
from qbitflow.sources.http import HttpSession

log = structlog.get_logger(__name__)

PAGE_SIZE = 1_000
DEFAULT_MAX_RECORDS = 20_000

_WINDOW_DAYS = {"week": 7, "month": 30, "year": 365}


class TautulliError(RuntimeError):
    pass


class TautulliAdapter(SourceAdapter):
    kind = SourceKind.TAUTULLI

    def __init__(self, ctx: SourceContext) -> None:
        super().__init__(ctx)
        self._http = HttpSession(ctx, headers={"Accept": "application/json"})

    async def aclose(self) -> None:
        await self._http.aclose()

    async def _cmd(self, command: str, **params: Any) -> Any:
        response = await self._http.get(
            "/api/v2",
            params={"apikey": self.ctx.secret, "cmd": command, **params},
        )
        response.raise_for_status()
        body = response.json()
        envelope = body.get("response") or {}
        if envelope.get("result") != "success":
            raise TautulliError(envelope.get("message") or f"{command} failed")
        return envelope.get("data")

    async def test(self) -> HealthResult:
        started = perf_counter()
        try:
            await self._cmd("get_server_friendly_name")
        except Exception as exc:  # noqa: BLE001
            return HealthResult.unhealthy(str(exc), int((perf_counter() - started) * 1000))
        return HealthResult.healthy(int((perf_counter() - started) * 1000))

    async def fetch(self) -> SourcePayload:
        payload = SourcePayload(
            source_id=self.ctx.source_id,
            source_name=self.ctx.name,
            kind=self.kind,
            fetched_at=datetime.now(UTC),
        )
        try:
            payload.watch_records = await self._fetch_history()
        except Exception as exc:  # noqa: BLE001 - isolation
            log.warning("tautulli.fetch.failed", source=self.ctx.name, error=str(exc))
            payload.error = f"{self.ctx.name}: {exc}"
        return payload

    async def _fetch_history(self) -> list[WatchRecord]:
        cap = int(self.ctx.options.get("max_history_records", DEFAULT_MAX_RECORDS))
        rows: list[dict[str, Any]] = []
        start = 0
        exhausted = False

        while len(rows) < cap:
            data = await self._cmd(
                "get_history",
                start=start,
                length=min(PAGE_SIZE, cap - len(rows)),
                order_column="date",
                order_dir="desc",
            )
            batch = (data or {}).get("data") or []
            rows.extend(batch)
            total = int((data or {}).get("recordsFiltered") or 0)
            start += len(batch)
            if not batch or start >= total:
                exhausted = True
                break

        if not exhausted:
            log.info(
                "tautulli.history_truncated",
                source=self.ctx.name,
                cap=cap,
                note="lifetime play counts will not be reported",
            )

        return self._aggregate(rows, exhausted=exhausted)

    def _aggregate(self, rows: list[dict[str, Any]], *, exhausted: bool) -> list[WatchRecord]:
        now = datetime.now(UTC)
        cutoffs = {name: now - timedelta(days=days) for name, days in _WINDOW_DAYS.items()}
        aggregate: dict[tuple[str, str], WatchRecord] = {}

        for row in rows:
            media_type = str(row.get("media_type") or "")
            if media_type == "episode":
                title = row.get("grandparent_title") or row.get("title") or ""
                normalized = "show"
            elif media_type == "movie":
                title = row.get("title") or row.get("full_title") or ""
                normalized = "movie"
            else:
                continue
            if not title:
                continue

            try:
                watched_at = datetime.fromtimestamp(int(row.get("date") or 0), tz=UTC)
            except (TypeError, ValueError, OSError):
                continue

            key = (normalized, str(title))
            record = aggregate.get(key)
            if record is None:
                record = WatchRecord(
                    source_id=self.ctx.source_id,
                    source_name=self.ctx.name,
                    source_item_id=str(
                        row.get("grandparent_rating_key") or row.get("rating_key") or ""
                    ),
                    title=str(title),
                    media_type=normalized,
                    watched_seconds=0,
                )
                aggregate[key] = record

            record.play_count += 1
            if record.last_played_at is None or watched_at > record.last_played_at:
                record.last_played_at = watched_at
            with contextlib.suppress(TypeError, ValueError):
                record.watched_seconds = (record.watched_seconds or 0) + int(
                    row.get("duration") or 0
                )

            for window, cutoff in cutoffs.items():
                if watched_at >= cutoff:
                    record.window_counts[window] = record.window_counts.get(window, 0) + 1

        if exhausted:
            for record in aggregate.values():
                record.window_counts["all"] = record.play_count

        return list(aggregate.values())
