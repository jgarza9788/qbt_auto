"""qBittorrent adapter -- both a data source and the target of every action.

Written directly against the WebUI API rather than wrapping a synchronous
client library, so the whole path stays async and mutations can be batched.

Two details the API demands and that are easy to get wrong:

* Login needs a ``Referer`` header matching the base URL, or qBittorrent's CSRF
  check rejects it with 403 regardless of the credentials.
* The session cookie expires without warning. Every call re-authenticates once on
  a 403 and retries, guarded by a lock so a burst of concurrent calls produces
  one login rather than one per caller.
"""

from __future__ import annotations

import asyncio
from collections.abc import Iterable, Sequence
from datetime import UTC, datetime
from time import perf_counter
from typing import Any

import httpx
import structlog

from qbitflow.domain import HealthResult, SourceKind
from qbitflow.sources.base import (
    SourceAdapter,
    SourceContext,
    SourcePayload,
    TorrentFileRecord,
    TorrentRecord,
)
from qbitflow.sources.http import HttpSession

log = structlog.get_logger(__name__)

API = "/api/v2"


def _epoch(value: Any) -> datetime | None:
    """qBittorrent uses 0 and -1 for "never", which must not become 1970."""
    try:
        seconds = int(value)
    except (TypeError, ValueError):
        return None
    if seconds <= 0:
        return None
    return datetime.fromtimestamp(seconds, tz=UTC)


def _limit(value: Any) -> int:
    """Normalise "unlimited" to 0.

    qBittorrent reports -1 in some versions and 0 in others for the same state;
    leaving both in place would make the idempotency check write a limit that is
    already effectively set.
    """
    try:
        n = int(value)
    except (TypeError, ValueError):
        return 0
    return 0 if n < 0 else n


def _tags(value: Any) -> list[str]:
    if not value:
        return []
    return [t.strip() for t in str(value).split(",") if t.strip()]


class QbtClient:
    """Low-level WebUI client. Owns login state for one instance."""

    def __init__(self, ctx: SourceContext) -> None:
        self.ctx = ctx
        self._http = HttpSession(ctx, headers={"Referer": ctx.base_url.rstrip("/")})
        self._login_lock = asyncio.Lock()
        self._logged_in = False
        self._api_version: str | None = None

    async def aclose(self) -> None:
        await self._http.aclose()

    # -- auth ---------------------------------------------------------------

    async def _login(self) -> None:
        response = await self._http.post(
            f"{API}/auth/login",
            data={"username": self.ctx.username, "password": self.ctx.secret},
        )
        body = response.text.strip()
        if response.status_code != 200 or body.lower() == "fails.":
            raise PermissionError(
                f"{self.ctx.name}: qBittorrent rejected the credentials "
                f"(HTTP {response.status_code})"
            )
        self._logged_in = True

    async def ensure_login(self, *, force: bool = False) -> None:
        if self._logged_in and not force:
            return
        async with self._login_lock:
            # Re-check: while waiting for the lock another caller may have
            # logged in already, and a second login would invalidate its cookie.
            if self._logged_in and not force:
                return
            self._logged_in = False
            await self._login()

    async def call(
        self,
        method: str,
        path: str,
        *,
        expect_json: bool = True,
        **kwargs: Any,
    ) -> Any:
        await self.ensure_login()
        response = await self._http.request(method, f"{API}{path}", **kwargs)

        if response.status_code == 403:
            await self.ensure_login(force=True)
            response = await self._http.request(method, f"{API}{path}", **kwargs)

        response.raise_for_status()
        if not expect_json:
            return response
        if not response.content:
            return None
        try:
            return response.json()
        except ValueError:
            return response.text

    # -- reads --------------------------------------------------------------

    async def api_version(self) -> str:
        if self._api_version is None:
            resp = await self.call("GET", "/app/webapiVersion", expect_json=False)
            self._api_version = resp.text.strip()
        return self._api_version

    async def app_version(self) -> str:
        resp = await self.call("GET", "/app/version", expect_json=False)
        return resp.text.strip()

    async def torrents(self) -> list[dict[str, Any]]:
        data = await self.call("GET", "/torrents/info")
        return data if isinstance(data, list) else []

    async def files(self, torrent_hash: str) -> list[dict[str, Any]]:
        data = await self.call("GET", "/torrents/files", params={"hash": torrent_hash})
        return data if isinstance(data, list) else []

    async def export(self, torrent_hash: str) -> bytes:
        resp = await self.call(
            "GET", "/torrents/export", params={"hash": torrent_hash}, expect_json=False
        )
        return resp.content

    # -- writes (all batched: qBittorrent takes pipe-delimited hash lists) ---

    async def _mutate(self, path: str, hashes: Sequence[str], **data: Any) -> None:
        if not hashes:
            return
        payload = {"hashes": "|".join(hashes), **data}
        await self.call("POST", path, data=payload, expect_json=False)

    async def add_tags(self, hashes: Sequence[str], tags: Iterable[str]) -> None:
        await self._mutate("/torrents/addTags", hashes, tags=",".join(tags))

    async def remove_tags(self, hashes: Sequence[str], tags: Iterable[str]) -> None:
        await self._mutate("/torrents/removeTags", hashes, tags=",".join(tags))

    async def set_category(self, hashes: Sequence[str], category: str) -> None:
        await self._mutate("/torrents/setCategory", hashes, category=category)

    async def set_auto_management(self, hashes: Sequence[str], enable: bool) -> None:
        await self._mutate(
            "/torrents/setAutoManagement", hashes, enable="true" if enable else "false"
        )

    async def set_location(self, hashes: Sequence[str], location: str) -> None:
        await self._mutate("/torrents/setLocation", hashes, location=location)

    async def set_upload_limit(self, hashes: Sequence[str], bytes_per_second: int) -> None:
        await self._mutate("/torrents/setUploadLimit", hashes, limit=bytes_per_second)

    async def set_download_limit(self, hashes: Sequence[str], bytes_per_second: int) -> None:
        await self._mutate("/torrents/setDownloadLimit", hashes, limit=bytes_per_second)

    async def set_force_start(self, hashes: Sequence[str], on: bool) -> None:
        await self._mutate("/torrents/setForceStart", hashes, value="true" if on else "false")

    async def stop(self, hashes: Sequence[str]) -> None:
        """qBittorrent 5.0 renamed pause to stop; 4.x only knows pause."""
        await self._dual_endpoint(hashes, "/torrents/stop", "/torrents/pause")

    async def start(self, hashes: Sequence[str]) -> None:
        await self._dual_endpoint(hashes, "/torrents/start", "/torrents/resume")

    async def _dual_endpoint(
        self, hashes: Sequence[str], modern: str, legacy: str
    ) -> None:
        try:
            await self._mutate(modern, hashes)
        except httpx.HTTPStatusError as exc:
            if exc.response.status_code != 404:
                raise
            await self._mutate(legacy, hashes)


class QbtAdapter(SourceAdapter):
    kind = SourceKind.QBITTORRENT

    def __init__(self, ctx: SourceContext) -> None:
        super().__init__(ctx)
        self.client = QbtClient(ctx)

    async def aclose(self) -> None:
        await self.client.aclose()

    async def test(self) -> HealthResult:
        started = perf_counter()
        try:
            version = await self.client.app_version()
        except Exception as exc:  # noqa: BLE001 - reported, never raised
            return HealthResult.unhealthy(str(exc), int((perf_counter() - started) * 1000))
        elapsed = int((perf_counter() - started) * 1000)
        log.debug("qbt.test.ok", source=self.ctx.name, version=version)
        return HealthResult.healthy(elapsed)

    async def fetch(self) -> SourcePayload:
        payload = SourcePayload(
            source_id=self.ctx.source_id,
            source_name=self.ctx.name,
            kind=self.kind,
            fetched_at=datetime.now(UTC),
        )
        try:
            raw = await self.client.torrents()
        except Exception as exc:  # noqa: BLE001 - isolation: one dead instance is not fatal
            log.warning("qbt.fetch.failed", source=self.ctx.name, error=str(exc))
            payload.error = f"{self.ctx.name}: {exc}"
            return payload

        payload.torrents = [self._to_record(t) for t in raw]

        # One request per torrent, so opt-in. Worth it when torrents are season
        # packs whose individual episode files need matching to a library.
        if self.ctx.options.get("fetch_files"):
            payload.torrent_files = await self._fetch_files(payload.torrents)

        return payload

    async def _fetch_files(self, torrents: list[TorrentRecord]) -> list[TorrentFileRecord]:
        gate = asyncio.Semaphore(self.ctx.max_concurrency)
        results: list[TorrentFileRecord] = []

        async def one(torrent: TorrentRecord) -> list[TorrentFileRecord]:
            async with gate:
                try:
                    entries = await self.client.files(torrent.hash)
                except Exception as exc:  # noqa: BLE001 - skip this torrent's files only
                    log.debug(
                        "qbt.files.failed",
                        source=self.ctx.name,
                        hash=torrent.hash,
                        error=str(exc),
                    )
                    return []
            return [
                TorrentFileRecord(
                    source_id=self.ctx.source_id,
                    torrent_hash=torrent.hash,
                    index=int(entry.get("index", idx)),
                    path=str(entry.get("name", "")),
                    size=int(entry.get("size", 0) or 0),
                    progress=float(entry.get("progress", 0.0) or 0.0),
                    priority=int(entry.get("priority", 0) or 0),
                )
                for idx, entry in enumerate(entries)
            ]

        for chunk in await asyncio.gather(*(one(t) for t in torrents)):
            results.extend(chunk)
        return results

    def _to_record(self, t: dict[str, Any]) -> TorrentRecord:
        return TorrentRecord(
            source_id=self.ctx.source_id,
            source_name=self.ctx.name,
            hash=str(t.get("hash", "")),
            name=str(t.get("name", "")),
            category=str(t.get("category", "") or ""),
            tags=_tags(t.get("tags")),
            save_path=str(t.get("save_path", "") or ""),
            content_path=str(t.get("content_path", "") or ""),
            state=str(t.get("state", "") or ""),
            size=int(t.get("size", 0) or 0),
            total_size=int(t.get("total_size", t.get("size", 0)) or 0),
            downloaded=int(t.get("downloaded", 0) or 0),
            uploaded=int(t.get("uploaded", 0) or 0),
            amount_left=int(t.get("amount_left", 0) or 0),
            progress=float(t.get("progress", 0.0) or 0.0),
            ratio=float(t.get("ratio", 0.0) or 0.0),
            download_limit=_limit(t.get("dl_limit")),
            upload_limit=_limit(t.get("up_limit")),
            download_speed=int(t.get("dlspeed", 0) or 0),
            upload_speed=int(t.get("upspeed", 0) or 0),
            num_seeds=int(t.get("num_seeds", 0) or 0),
            num_leechs=int(t.get("num_leechs", 0) or 0),
            num_complete=int(t.get("num_complete", 0) or 0),
            num_incomplete=int(t.get("num_incomplete", 0) or 0),
            priority=int(t.get("priority", 0) or 0),
            active_time_seconds=int(t.get("time_active", 0) or 0),
            seeding_time_seconds=int(t.get("seeding_time", 0) or 0),
            eta_seconds=int(t.get("eta")) if t.get("eta") not in (None, 8640000) else None,
            availability=float(t.get("availability", 0.0) or 0.0),
            tracker=str(t.get("tracker", "") or ""),
            added_on=_epoch(t.get("added_on")),
            completion_on=_epoch(t.get("completion_on")),
            last_activity=_epoch(t.get("last_activity")),
            last_seen_complete=_epoch(t.get("seen_complete")),
            force_start=bool(t.get("force_start", False)),
            auto_managed=bool(t.get("auto_tmm", False)),
            super_seeding=bool(t.get("super_seeding", False)),
        )
