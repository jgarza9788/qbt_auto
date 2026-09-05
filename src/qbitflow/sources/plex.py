"""Plex adapter.

Plex speaks XML. Two things this does that the C# predecessor did not:

* **Pagination.** ``/library/sections/{key}/all`` returns the entire section in one
  response otherwise, which times out on a large library.
* **One request for all episodes** (``?type=4``) instead of ``/allLeaves`` per
  series, which was one HTTP round trip per TV show, issued serially.
"""

from __future__ import annotations

from datetime import UTC, datetime
from time import perf_counter
from typing import Any
from xml.etree import ElementTree

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

#: Plex library section types.
TYPE_MOVIE = "1"
TYPE_SHOW = "2"
TYPE_EPISODE = "4"

_MEDIA_TYPE = {TYPE_MOVIE: "movie", TYPE_SHOW: "show", TYPE_EPISODE: "episode"}


def _num(value: Any) -> float | None:
    if value in (None, ""):
        return None
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def _int(value: Any) -> int | None:
    n = _num(value)
    return int(n) if n is not None else None


def _epoch(value: Any) -> datetime | None:
    n = _int(value)
    if not n or n <= 0:
        return None
    return datetime.fromtimestamp(n, tz=UTC)


class PlexAdapter(SourceAdapter):
    kind = SourceKind.PLEX

    def __init__(self, ctx: SourceContext) -> None:
        super().__init__(ctx)
        self._http = HttpSession(
            ctx,
            headers={
                "X-Plex-Token": ctx.secret,
                "X-Plex-Product": "qbitflow",
                "X-Plex-Client-Identifier": str(ctx.options.get("client_id", "qbitflow")),
                "Accept": "application/xml",
            },
        )

    async def aclose(self) -> None:
        await self._http.aclose()

    async def _xml(self, path: str, **params: Any) -> ElementTree.Element | None:
        response = await self._http.get(path, params=params or None)
        response.raise_for_status()
        try:
            return ElementTree.fromstring(response.content)
        except ElementTree.ParseError as exc:
            log.warning("plex.bad_xml", source=self.ctx.name, path=path, error=str(exc))
            return None

    async def _paged(self, path: str, **params: Any) -> list[ElementTree.Element]:
        """Walk a container endpoint until it stops returning rows."""
        out: list[ElementTree.Element] = []
        start = 0
        while True:
            response = await self._http.get(
                path,
                params=params or None,
                headers={
                    "X-Plex-Container-Start": str(start),
                    "X-Plex-Container-Size": str(PAGE_SIZE),
                },
            )
            response.raise_for_status()
            try:
                root = ElementTree.fromstring(response.content)
            except ElementTree.ParseError:
                break

            batch = [e for e in root if e.tag in {"Video", "Directory"}]
            out.extend(batch)

            total = _int(root.get("totalSize")) or _int(root.get("size")) or len(out)
            start += PAGE_SIZE
            if len(batch) < PAGE_SIZE or start >= total:
                break
        return out

    async def test(self) -> HealthResult:
        started = perf_counter()
        try:
            root = await self._xml("/identity")
            if root is None:
                raise RuntimeError("unexpected response from /identity")
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
            payload.media_items = await self._fetch_library()
            payload.watch_records = await self._fetch_history()
        except Exception as exc:  # noqa: BLE001 - isolation
            log.warning("plex.fetch.failed", source=self.ctx.name, error=str(exc))
            payload.error = f"{self.ctx.name}: {exc}"
        return payload

    async def _fetch_library(self) -> list[MediaItemRecord]:
        sections = await self._xml("/library/sections")
        if sections is None:
            return []

        items: list[MediaItemRecord] = []
        for directory in sections.findall("Directory"):
            section_type = directory.get("type")
            key = directory.get("key")
            if not key or section_type not in {"movie", "show"}:
                continue

            if section_type == "movie":
                for element in await self._paged(f"/library/sections/{key}/all"):
                    items.append(self._to_item(element))
            else:
                # Series rows carry ratings and genres; episode rows carry the
                # files. Both are wanted, and both come back in one request each.
                for element in await self._paged(f"/library/sections/{key}/all", type=TYPE_SHOW):
                    items.append(self._to_item(element))
                for element in await self._paged(
                    f"/library/sections/{key}/all", type=TYPE_EPISODE
                ):
                    items.append(self._to_item(element))
        return items

    def _to_item(self, element: ElementTree.Element) -> MediaItemRecord:
        media_type = _MEDIA_TYPE.get(element.get("type", ""), element.get("type", "") or "movie")
        files = [
            MediaFileRef(path=part.get("file", ""), size_bytes=_int(part.get("size")))
            for part in element.iter("Part")
            if part.get("file")
        ]
        return MediaItemRecord(
            source_id=self.ctx.source_id,
            source_name=self.ctx.name,
            source_item_id=element.get("ratingKey", ""),
            title=element.get("title", ""),
            media_type=media_type,
            year=_int(element.get("year")),
            rating=_num(element.get("rating")),
            # Kept separate rather than falling back one to the other: they are
            # different scales, and silently mixing them makes a rule threshold
            # mean different things for different items.
            audience_rating=_num(element.get("audienceRating")),
            user_rating=_num(element.get("userRating")),
            content_rating=element.get("contentRating"),
            genres=[g.get("tag", "") for g in element.findall("Genre") if g.get("tag")],
            duration_ms=_int(element.get("duration")),
            added_at=_epoch(element.get("addedAt")),
            series_title=element.get("grandparentTitle"),
            season=_int(element.get("parentIndex")),
            episode=_int(element.get("index")),
            files=files,
        )

    async def _fetch_history(self) -> list[WatchRecord]:
        """Aggregate view history per title.

        Episodes roll up to their series so "nobody has watched this show in 90
        days" behaves the way a user expects, rather than being true for every
        individual episode they skipped.
        """
        root = await self._xml("/status/sessions/history/all", sort="viewedAt:desc")
        if root is None:
            return []

        aggregate: dict[tuple[str, str], WatchRecord] = {}
        for entry in root.iter("Video"):
            raw_type = entry.get("type", "")
            if raw_type == "episode":
                title = entry.get("grandparentTitle") or entry.get("title", "")
                media_type = "show"
            else:
                title = entry.get("title", "")
                media_type = "movie"
            if not title:
                continue

            viewed = _epoch(entry.get("viewedAt"))
            key = (media_type, title)
            record = aggregate.get(key)
            if record is None:
                record = WatchRecord(
                    source_id=self.ctx.source_id,
                    source_name=self.ctx.name,
                    source_item_id=entry.get("ratingKey", ""),
                    title=title,
                    media_type=media_type,
                )
                aggregate[key] = record

            record.play_count += 1
            if viewed and (record.last_played_at is None or viewed > record.last_played_at):
                record.last_played_at = viewed

        return list(aggregate.values())
