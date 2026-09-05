"""A JSON watch-history adapter whose endpoints and field names are configuration.

Jellystat and Jellyglance are small, fast-moving self-hosted projects whose HTTP
APIs this code could not be verified against. Rather than hard-coding a guess and
shipping an adapter that silently returns nothing, the request paths and the
field mapping are options on the source connection.

The shipped defaults are a best reading of each project. If they are wrong, the
connection-test button says so and the endpoint can be corrected in the UI --
without a code change or a new image.
"""

from __future__ import annotations

import contextlib
from datetime import UTC, datetime
from time import perf_counter
from typing import Any

import structlog

from qbitflow.domain import HealthResult, SourceKind
from qbitflow.sources.base import SourceAdapter, SourceContext, SourcePayload, WatchRecord
from qbitflow.sources.http import HttpSession

log = structlog.get_logger(__name__)


def _dig(obj: Any, path: str) -> Any:
    """Follow a dotted path into nested dicts, tolerating anything missing."""
    current = obj
    for part in path.split("."):
        if isinstance(current, dict):
            current = current.get(part)
        else:
            return None
    return current


def _as_datetime(value: Any) -> datetime | None:
    if value in (None, ""):
        return None
    if isinstance(value, int | float):
        try:
            # Milliseconds are common enough to be worth detecting.
            seconds = float(value)
            if seconds > 1e11:
                seconds /= 1000.0
            return datetime.fromtimestamp(seconds, tz=UTC)
        except (ValueError, OSError, OverflowError):
            return None
    text = str(value).strip().replace("Z", "+00:00")
    try:
        parsed = datetime.fromisoformat(text)
    except ValueError:
        return None
    return parsed if parsed.tzinfo else parsed.replace(tzinfo=UTC)


class ConfigurableHistoryAdapter(SourceAdapter):
    """Base for JSON sources that expose a list of playback events."""

    #: Overridden per concrete source.
    default_health_path = "/"
    default_history_path = "/"
    default_history_method = "GET"
    default_auth_header = "x-api-token"
    #: Where the list of rows sits in the response, "" meaning the body itself.
    default_rows_key = ""
    default_field_map: dict[str, str] = {
        "title": "title",
        "series_title": "series_title",
        "media_type": "media_type",
        "played_at": "played_at",
        "play_count": "play_count",
        "item_id": "item_id",
        "user": "user",
        "duration_seconds": "duration_seconds",
    }

    def __init__(self, ctx: SourceContext) -> None:
        super().__init__(ctx)
        header = str(ctx.options.get("auth_header", self.default_auth_header))
        self._http = HttpSession(
            ctx,
            headers={header: ctx.secret, "Accept": "application/json"},
        )

    async def aclose(self) -> None:
        await self._http.aclose()

    # -- configuration ------------------------------------------------------

    @property
    def health_path(self) -> str:
        return str(self.ctx.options.get("health_path", self.default_health_path))

    @property
    def history_path(self) -> str:
        return str(self.ctx.options.get("history_path", self.default_history_path))

    @property
    def history_method(self) -> str:
        return str(self.ctx.options.get("history_method", self.default_history_method)).upper()

    @property
    def rows_key(self) -> str:
        return str(self.ctx.options.get("rows_key", self.default_rows_key))

    @property
    def field_map(self) -> dict[str, str]:
        merged = dict(self.default_field_map)
        override = self.ctx.options.get("field_map")
        if isinstance(override, dict):
            merged.update({str(k): str(v) for k, v in override.items()})
        return merged

    # -- adapter ------------------------------------------------------------

    async def test(self) -> HealthResult:
        started = perf_counter()
        try:
            response = await self._http.get(self.health_path)
            response.raise_for_status()
        except Exception as exc:  # noqa: BLE001
            return HealthResult.unhealthy(
                f"{exc} (endpoint is configurable: health_path)",
                int((perf_counter() - started) * 1000),
            )
        return HealthResult.healthy(int((perf_counter() - started) * 1000))

    async def fetch(self) -> SourcePayload:
        payload = SourcePayload(
            source_id=self.ctx.source_id,
            source_name=self.ctx.name,
            kind=self.kind,
            fetched_at=datetime.now(UTC),
        )
        try:
            rows = await self._fetch_rows()
            payload.watch_records = self._aggregate(rows)
        except Exception as exc:  # noqa: BLE001 - isolation
            log.warning(
                "rest_history.fetch.failed",
                source=self.ctx.name,
                kind=str(self.kind),
                path=self.history_path,
                error=str(exc),
            )
            payload.error = f"{self.ctx.name}: {exc} (history_path={self.history_path})"
        return payload

    async def _fetch_rows(self) -> list[dict[str, Any]]:
        body = self.ctx.options.get("history_body")
        if self.history_method == "POST":
            response = await self._http.post(self.history_path, json=body or {})
        else:
            response = await self._http.get(self.history_path)
        response.raise_for_status()
        data = response.json()

        rows = _dig(data, self.rows_key) if self.rows_key else data
        if isinstance(rows, dict):
            # Some of these APIs wrap the list one level deeper than documented.
            for candidate in ("results", "data", "items", "rows"):
                if isinstance(rows.get(candidate), list):
                    rows = rows[candidate]
                    break
        if not isinstance(rows, list):
            raise ValueError(
                f"expected a list of history rows at '{self.rows_key or 'the response root'}', "
                f"got {type(rows).__name__}; set rows_key in this source's options"
            )
        return [r for r in rows if isinstance(r, dict)]

    def _aggregate(self, rows: list[dict[str, Any]]) -> list[WatchRecord]:
        fields = self.field_map
        aggregate: dict[tuple[str, str], WatchRecord] = {}

        for row in rows:
            raw_type = str(_dig(row, fields["media_type"]) or "").lower()
            series = _dig(row, fields["series_title"])
            if raw_type in {"episode", "series", "show", "tv"} or series:
                title = str(series or _dig(row, fields["title"]) or "")
                media_type = "show"
            else:
                title = str(_dig(row, fields["title"]) or "")
                media_type = "movie"
            if not title:
                continue

            key = (media_type, title)
            record = aggregate.get(key)
            if record is None:
                record = WatchRecord(
                    source_id=self.ctx.source_id,
                    source_name=self.ctx.name,
                    source_item_id=str(_dig(row, fields["item_id"]) or ""),
                    title=title,
                    media_type=media_type,
                    user=_dig(row, fields["user"]),
                )
                aggregate[key] = record

            try:
                plays = int(_dig(row, fields["play_count"]) or 1)
            except (TypeError, ValueError):
                plays = 1
            record.play_count += max(plays, 1)

            played_at = _as_datetime(_dig(row, fields["played_at"]))
            if played_at and (record.last_played_at is None or played_at > record.last_played_at):
                record.last_played_at = played_at

            duration = _dig(row, fields["duration_seconds"])
            if duration is not None:
                with contextlib.suppress(TypeError, ValueError):
                    record.watched_seconds = (record.watched_seconds or 0) + int(float(duration))

        return list(aggregate.values())


class JellystatAdapter(ConfigurableHistoryAdapter):
    """Jellystat. Authenticates with an ``x-api-token`` header.

    Endpoint defaults are unverified; correct them in the source's options if the
    connection test fails.
    """

    kind = SourceKind.JELLYSTAT

    default_health_path = "/api/getLibraries"
    default_history_path = "/api/getAllUserActivity"
    default_history_method = "POST"
    default_auth_header = "x-api-token"
    default_rows_key = ""
    default_field_map = {
        "title": "NowPlayingItemName",
        "series_title": "SeriesName",
        "media_type": "MediaType",
        "played_at": "ActivityDateInserted",
        "play_count": "PlaybackCount",
        "item_id": "NowPlayingItemId",
        "user": "UserName",
        "duration_seconds": "PlaybackDuration",
    }


class JellyglanceAdapter(ConfigurableHistoryAdapter):
    """Jellyglance.

    The least-documented of the supported sources. Defaults are a plausible
    guess; expect to set ``history_path``, ``rows_key`` and ``field_map`` from the
    UI against a live instance.
    """

    kind = SourceKind.JELLYGLANCE

    default_health_path = "/api/health"
    default_history_path = "/api/history"
    default_history_method = "GET"
    default_auth_header = "x-api-key"
    default_rows_key = ""
    default_field_map = {
        "title": "title",
        "series_title": "seriesName",
        "media_type": "type",
        "played_at": "playedAt",
        "play_count": "playCount",
        "item_id": "itemId",
        "user": "userName",
        "duration_seconds": "duration",
    }
