"""Jellyfin adapter.

Jellyfin has no time-windowed history API -- ``UserData.PlayCount`` is a lifetime
total. That total is reported as the ``all`` window and no other, so a rule asking
"played in the last week" gets nothing from Jellyfin rather than a wrong answer.
Pair it with Jellystat or Tautulli when windowed history matters.
"""

from __future__ import annotations

from datetime import UTC, datetime
from time import perf_counter
from typing import Any

import structlog

from qbitflow.domain import HealthResult, SourceKind
from qbitflow.sources.base import (
    MediaFileRef,
    MediaItemRecord,
    SourceAdapter,
    SourceContext,
    SourcePayload,
    WatchRecord,
)
from qbitflow.sources.http import HttpSession

log = structlog.get_logger(__name__)

PAGE_SIZE = 500
TICKS_PER_MS = 10_000

_MEDIA_TYPE = {"Movie": "movie", "Series": "show", "Episode": "episode"}

_FIELDS = ",".join(
    [
        "Path",
        "Genres",
        "ProductionYear",
        "CommunityRating",
        "CriticRating",
        "OfficialRating",
        "RunTimeTicks",
        "MediaSources",
        "DateCreated",
    ]
)


def _parse_dt(value: Any) -> datetime | None:
    if not value or not isinstance(value, str):
        return None
    try:
        # Jellyfin emits more than six fractional digits, which fromisoformat
        # rejects on older Pythons; truncating is lossless at our resolution.
        text = value.replace("Z", "+00:00")
        if "." in text:
            head, _, tail = text.partition(".")
            digits = "".join(c for c in tail if c.isdigit())[:6]
            offset = tail[len(digits) :] if len(tail) > len(digits) else ""
            text = f"{head}.{digits}{offset}" if digits else head + offset
        parsed = datetime.fromisoformat(text)
    except ValueError:
        return None
    return parsed if parsed.tzinfo else parsed.replace(tzinfo=UTC)


class JellyfinAdapter(SourceAdapter):
    kind = SourceKind.JELLYFIN

    def __init__(self, ctx: SourceContext) -> None:
        super().__init__(ctx)
        self._http = HttpSession(
            ctx,
            headers={"X-Emby-Token": ctx.secret, "Accept": "application/json"},
        )

    async def aclose(self) -> None:
        await self._http.aclose()

    async def _json(self, path: str, **params: Any) -> Any:
        response = await self._http.get(path, params=params or None)
        response.raise_for_status()
        return response.json()

    async def _paged_items(self, path: str, **params: Any) -> list[dict[str, Any]]:
        out: list[dict[str, Any]] = []
        start = 0
        while True:
            data = await self._json(path, StartIndex=start, Limit=PAGE_SIZE, **params)
            batch = data.get("Items") or []
            out.extend(batch)
            total = data.get("TotalRecordCount")
            start += PAGE_SIZE
            if len(batch) < PAGE_SIZE or (total is not None and start >= int(total)):
                break
        return out

    async def test(self) -> HealthResult:
        started = perf_counter()
        try:
            await self._json("/System/Info")
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
            raw = await self._paged_items(
                "/Items",
                Recursive="true",
                IncludeItemTypes="Movie,Series,Episode",
                Fields=_FIELDS,
            )
            payload.media_items = [self._to_item(i) for i in raw]
            payload.watch_records = await self._fetch_watch()
        except Exception as exc:  # noqa: BLE001 - isolation
            log.warning("jellyfin.fetch.failed", source=self.ctx.name, error=str(exc))
            payload.error = f"{self.ctx.name}: {exc}"
        return payload

    def _to_item(self, item: dict[str, Any]) -> MediaItemRecord:
        ticks = item.get("RunTimeTicks")
        paths = []
        if item.get("Path"):
            paths.append(MediaFileRef(path=str(item["Path"])))
        for source in item.get("MediaSources") or []:
            path = source.get("Path")
            if path and all(path != p.path for p in paths):
                paths.append(MediaFileRef(path=str(path), size_bytes=source.get("Size")))

        return MediaItemRecord(
            source_id=self.ctx.source_id,
            source_name=self.ctx.name,
            source_item_id=str(item.get("Id", "")),
            title=str(item.get("Name", "")),
            media_type=_MEDIA_TYPE.get(str(item.get("Type", "")), "movie"),
            year=item.get("ProductionYear"),
            rating=item.get("CommunityRating"),
            audience_rating=item.get("CriticRating"),
            content_rating=item.get("OfficialRating"),
            genres=list(item.get("Genres") or []),
            duration_ms=int(ticks) // TICKS_PER_MS if ticks else None,
            added_at=_parse_dt(item.get("DateCreated")),
            series_title=item.get("SeriesName"),
            season=item.get("ParentIndexNumber"),
            episode=item.get("IndexNumber"),
            files=paths,
        )

    async def _fetch_watch(self) -> list[WatchRecord]:
        """Play counts summed across the users in scope.

        ``user_scope`` is "all" or a comma-separated list of usernames -- a shared
        server usually wants "has *anyone* watched this", but a household with
        separate libraries may want one person's activity only.
        """
        users = await self._json("/Users")
        if not isinstance(users, list):
            return []

        scope = str(self.ctx.options.get("user_scope", "all")).strip()
        if scope and scope.lower() != "all":
            wanted = {u.strip().lower() for u in scope.split(",") if u.strip()}
            users = [u for u in users if str(u.get("Name", "")).lower() in wanted]

        aggregate: dict[tuple[str, str], WatchRecord] = {}
        for user in users:
            user_id = user.get("Id")
            if not user_id:
                continue
            items = await self._paged_items(
                f"/Users/{user_id}/Items",
                Recursive="true",
                IncludeItemTypes="Movie,Series,Episode",
                Fields=_FIELDS,
                EnableUserData="true",
                IsPlayed="true",
            )
            for item in items:
                data = item.get("UserData") or {}
                play_count = int(data.get("PlayCount") or 0)
                if play_count <= 0:
                    continue

                if str(item.get("Type")) == "Episode":
                    title = item.get("SeriesName") or item.get("Name") or ""
                    media_type = "show"
                else:
                    title = item.get("Name") or ""
                    media_type = _MEDIA_TYPE.get(str(item.get("Type", "")), "movie")
                if not title:
                    continue

                key = (media_type, title)
                record = aggregate.get(key)
                if record is None:
                    record = WatchRecord(
                        source_id=self.ctx.source_id,
                        source_name=self.ctx.name,
                        source_item_id=str(item.get("Id", "")),
                        title=str(title),
                        media_type=media_type,
                    )
                    aggregate[key] = record

                record.play_count += play_count
                last = _parse_dt(data.get("LastPlayedDate"))
                if last and (record.last_played_at is None or last > record.last_played_at):
                    record.last_played_at = last
                # Jellyfin exposes no windowing, so the lifetime total is the
                # only honest window to report.
                record.window_counts["all"] = record.play_count

        return list(aggregate.values())
