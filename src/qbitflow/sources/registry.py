"""Adapter construction and the shared per-source fetch cache.

This is the layer that makes the "one fetch per source per cycle" guarantee real.
Fifty rules that all read torrents cause one call to qBittorrent, not fifty:

* Each source has a TTL. A payload younger than the TTL is returned as-is.
* Concurrent callers for the same stale source collapse into one fetch, via a
  per-source lock with a re-check after acquiring it. Without the re-check, every
  waiter would run its own fetch the moment the first one released the lock.

Adapters are cached too, since they hold live connections and login cookies, and
are disposed when their connection's settings change.
"""

from __future__ import annotations

import asyncio
import re
from collections.abc import Iterable
from datetime import UTC, datetime

import structlog
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker

from qbitflow.config import AppConfig
from qbitflow.crypto import SecretDecryptError, SecretProtector
from qbitflow.db.models import PathMapping, SourceConnection, StoragePath
from qbitflow.domain import HealthResult, SourceKind
from qbitflow.sources.base import SourceAdapter, SourceContext, SourcePayload
from qbitflow.sources.jellyfin import JellyfinAdapter
from qbitflow.sources.paths import PathMapper, PathRule
from qbitflow.sources.plex import PlexAdapter
from qbitflow.sources.qbt import QbtAdapter
from qbitflow.sources.rest_history import JellyglanceAdapter, JellystatAdapter
from qbitflow.sources.storage import StorageAdapter, StoragePathSpec
from qbitflow.sources.tautulli import TautulliAdapter

log = structlog.get_logger(__name__)

ADAPTERS: dict[SourceKind, type[SourceAdapter]] = {
    SourceKind.QBITTORRENT: QbtAdapter,
    SourceKind.PLEX: PlexAdapter,
    SourceKind.JELLYFIN: JellyfinAdapter,
    SourceKind.TAUTULLI: TautulliAdapter,
    SourceKind.JELLYSTAT: JellystatAdapter,
    SourceKind.JELLYGLANCE: JellyglanceAdapter,
}

#: Synthetic id for the single storage adapter, which has no connection row.
STORAGE_SOURCE_ID = 0

_ENV_SAFE = re.compile(r"[^A-Z0-9]+")


def env_name(source_name: str) -> str:
    return _ENV_SAFE.sub("_", source_name.upper()).strip("_")


def _env_override(source_name: str, field: str) -> str | None:
    """Environment beats the database, and is never written back.

    This is what lets a Docker user keep credentials entirely out of the mounted
    volume: set them in compose, leave the field blank in the UI.
    """
    import os

    key = f"QBITFLOW_SOURCE__{env_name(source_name)}__{field.upper()}"
    value = os.environ.get(key)
    return value if value and value.strip() else None


class SourceResolver:
    """Turns a database row into a ready-to-use :class:`SourceContext`."""

    def __init__(self, protector: SecretProtector, config: AppConfig) -> None:
        self._protector = protector
        self._config = config

    def context(self, row: SourceConnection, *, max_concurrency: int) -> SourceContext:
        secret = ""
        if row.secret_ciphertext and row.secret_nonce:
            try:
                secret = self._protector.unprotect(row.secret_ciphertext, row.secret_nonce)
            except SecretDecryptError as exc:
                # Not fatal: the connection test will report it clearly, and an
                # env override may make the stored value irrelevant anyway.
                log.warning("source.secret_undecryptable", source=row.name, error=str(exc))

        return SourceContext(
            source_id=row.id,
            name=row.name,
            kind=SourceKind(row.kind),
            base_url=_env_override(row.name, "base_url") or row.base_url,
            username=_env_override(row.name, "username") or row.username,
            secret=_env_override(row.name, "secret") or secret,
            timeout_seconds=row.timeout_seconds,
            verify_ssl=row.verify_ssl,
            options=dict(row.options or {}),
            max_concurrency=max_concurrency,
        )


class SourceCache:
    """Caches adapters and their most recent payloads."""

    def __init__(
        self,
        session_factory: async_sessionmaker[AsyncSession],
        resolver: SourceResolver,
    ) -> None:
        self._factory = session_factory
        self._resolver = resolver
        self._adapters: dict[int, SourceAdapter] = {}
        self._signatures: dict[int, tuple] = {}
        self._payloads: dict[int, SourcePayload] = {}
        self._locks: dict[int, asyncio.Lock] = {}
        self._registry_lock = asyncio.Lock()

    # -- adapter lifecycle --------------------------------------------------

    def _lock_for(self, source_id: int) -> asyncio.Lock:
        lock = self._locks.get(source_id)
        if lock is None:
            lock = self._locks[source_id] = asyncio.Lock()
        return lock

    async def _load_rows(self) -> list[SourceConnection]:
        async with self._factory() as session:
            result = await session.execute(
                select(SourceConnection).where(SourceConnection.enabled.is_(True))
            )
            return list(result.scalars().all())

    async def _storage_specs(self) -> list[StoragePathSpec]:
        async with self._factory() as session:
            result = await session.execute(
                select(StoragePath).where(StoragePath.enabled.is_(True))
            )
            rows = list(result.scalars().all())
        return [
            StoragePathSpec(
                name=r.name,
                path=r.path,
                measure_folder_size=r.measure_folder_size,
                folder_scan_interval_minutes=r.folder_scan_interval_minutes,
            )
            for r in rows
        ]

    async def path_mapper(self, source_id: int | None = None) -> PathMapper:
        """Global mappings plus this source's own, the source's taking precedence."""
        async with self._factory() as session:
            result = await session.execute(select(PathMapping))
            rows = list(result.scalars().all())
        applicable = [
            r
            for r in rows
            if r.source_id is None or (source_id is not None and r.source_id == source_id)
        ]
        applicable.sort(key=lambda r: (r.source_id is None, r.order))
        return PathMapper([PathRule(r.from_prefix, r.to_prefix) for r in applicable])

    async def refresh_registry(self, *, max_concurrency: int = 8) -> None:
        """Rebuild adapters whose configuration changed, disposing the old ones."""
        async with self._registry_lock:
            rows = await self._load_rows()
            seen: set[int] = set()

            for row in rows:
                kind = SourceKind(row.kind)
                adapter_cls = ADAPTERS.get(kind)
                if adapter_cls is None:
                    log.warning("source.unsupported_kind", source=row.name, kind=str(kind))
                    continue

                ctx = self._resolver.context(row, max_concurrency=max_concurrency)
                signature = (
                    ctx.base_url,
                    ctx.username,
                    ctx.secret,
                    ctx.timeout_seconds,
                    ctx.verify_ssl,
                    tuple(sorted(ctx.options.items())),
                    max_concurrency,
                )
                seen.add(row.id)

                if self._signatures.get(row.id) == signature:
                    continue

                await self._dispose(row.id)
                self._adapters[row.id] = adapter_cls(ctx)
                self._signatures[row.id] = signature
                self._payloads.pop(row.id, None)

            # Storage is rebuilt every time: its "configuration" is the set of
            # named paths, and rebuilding is free -- there is no connection to drop.
            specs = await self._storage_specs()
            if specs:
                await self._dispose(STORAGE_SOURCE_ID)
                self._adapters[STORAGE_SOURCE_ID] = StorageAdapter(specs)
                seen.add(STORAGE_SOURCE_ID)
            elif STORAGE_SOURCE_ID in self._adapters:
                await self._dispose(STORAGE_SOURCE_ID)

            for stale in set(self._adapters) - seen:
                await self._dispose(stale)

    async def _dispose(self, source_id: int) -> None:
        adapter = self._adapters.pop(source_id, None)
        self._signatures.pop(source_id, None)
        self._payloads.pop(source_id, None)
        if adapter is not None:
            try:
                await adapter.aclose()
            except Exception:  # noqa: BLE001 - teardown must not raise
                log.debug("source.close_failed", source_id=source_id)

    def adapters(self) -> dict[int, SourceAdapter]:
        return dict(self._adapters)

    def adapter(self, source_id: int) -> SourceAdapter | None:
        return self._adapters.get(source_id)

    def invalidate(self, source_id: int) -> None:
        self._payloads.pop(source_id, None)

    def invalidate_all(self) -> None:
        self._payloads.clear()

    # -- fetching -----------------------------------------------------------

    async def get(self, source_id: int, ttl_seconds: int) -> SourcePayload | None:
        adapter = self._adapters.get(source_id)
        if adapter is None:
            return None

        cached = self._payloads.get(source_id)
        if cached is not None and self._is_fresh(cached, ttl_seconds):
            return cached

        async with self._lock_for(source_id):
            # Re-check: another caller may have refreshed this source while we
            # waited, and fetching again would defeat the whole point.
            cached = self._payloads.get(source_id)
            if cached is not None and self._is_fresh(cached, ttl_seconds):
                return cached

            payload = await adapter.fetch()
            self._payloads[source_id] = payload
            if payload.error:
                log.warning("source.fetch_error", source=adapter.ctx.name, error=payload.error)
            return payload

    @staticmethod
    def _is_fresh(payload: SourcePayload, ttl_seconds: int) -> bool:
        age = (datetime.now(UTC) - payload.fetched_at).total_seconds()
        return age < ttl_seconds

    async def get_many(
        self, source_ids: Iterable[int], ttl_for: dict[int, int], *, workers: int
    ) -> list[SourcePayload]:
        """Fetch several sources concurrently, isolating failures.

        A source that raises rather than returning an error payload -- a bug in an
        adapter, not a dead service -- is still contained here.
        """
        gate = asyncio.Semaphore(max(1, workers))

        async def one(source_id: int) -> SourcePayload | None:
            async with gate:
                try:
                    return await self.get(source_id, ttl_for.get(source_id, 300))
                except Exception as exc:  # noqa: BLE001 - isolation is the point
                    adapter = self._adapters.get(source_id)
                    name = adapter.ctx.name if adapter else str(source_id)
                    log.exception("source.fetch_raised", source=name)
                    return SourcePayload(
                        source_id=source_id,
                        source_name=name,
                        kind=adapter.kind if adapter else SourceKind.QBITTORRENT,
                        fetched_at=datetime.now(UTC),
                        error=f"{name}: {exc}",
                    )

        results = await asyncio.gather(*(one(sid) for sid in source_ids))
        return [r for r in results if r is not None]

    async def test(self, source_id: int) -> HealthResult:
        adapter = self._adapters.get(source_id)
        if adapter is None:
            return HealthResult.unhealthy("source is not configured or not enabled", 0)
        try:
            return await adapter.test()
        except Exception as exc:  # noqa: BLE001 - test must always answer
            return HealthResult.unhealthy(str(exc), 0)

    async def aclose(self) -> None:
        for source_id in list(self._adapters):
            await self._dispose(source_id)
