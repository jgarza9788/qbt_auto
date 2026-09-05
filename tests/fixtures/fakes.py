"""Hand-written fakes at the interface boundary.

Deliberately real objects rather than mocks: they hold state, so a test can
assert that running a rule twice is a no-op the second time, which is the whole
point of the idempotency design.
"""

from __future__ import annotations

from collections.abc import Iterable, Sequence
from dataclasses import dataclass, field
from typing import Any

from qbitflow.domain import HealthResult, SourceKind
from qbitflow.sources.base import SourcePayload
from qbitflow.sources.paths import PathMapper


@dataclass
class RecordedCall:
    op: str
    hashes: list[str]
    args: tuple[Any, ...]


class FakeQbtClient:
    """Records every call and mutates its own view of torrent state.

    Mutating matters: the snapshot is rebuilt from these torrents, so a second
    run sees the effect of the first and must decide there is nothing to do.
    """

    def __init__(self, torrents: dict[str, dict[str, Any]] | None = None) -> None:
        self.torrents = torrents or {}
        self.calls: list[RecordedCall] = []
        self.fail_on: set[str] = set()
        self.exports: dict[str, bytes] = {}

    def _record(self, op: str, hashes: Sequence[str], *args: Any) -> None:
        if op in self.fail_on:
            raise RuntimeError(f"simulated failure for {op}")
        self.calls.append(RecordedCall(op=op, hashes=list(hashes), args=args))

    def calls_for(self, op: str) -> list[RecordedCall]:
        return [c for c in self.calls if c.op == op]

    @property
    def request_count(self) -> int:
        return len(self.calls)

    # -- mutations ----------------------------------------------------------

    async def add_tags(self, hashes: Sequence[str], tags: Iterable[str]) -> None:
        tags = list(tags)
        self._record("add_tags", hashes, tuple(tags))
        for digest in hashes:
            current = self.torrents.get(digest, {}).setdefault("tags", [])
            for tag in tags:
                if tag not in current:
                    current.append(tag)

    async def remove_tags(self, hashes: Sequence[str], tags: Iterable[str]) -> None:
        tags = list(tags)
        self._record("remove_tags", hashes, tuple(tags))
        for digest in hashes:
            current = self.torrents.get(digest, {}).get("tags", [])
            for tag in tags:
                if tag in current:
                    current.remove(tag)

    async def set_category(self, hashes: Sequence[str], category: str) -> None:
        self._record("set_category", hashes, category)
        for digest in hashes:
            self.torrents.setdefault(digest, {})["category"] = category

    async def set_auto_management(self, hashes: Sequence[str], enable: bool) -> None:
        self._record("set_auto_management", hashes, enable)
        for digest in hashes:
            self.torrents.setdefault(digest, {})["auto_managed"] = enable

    async def set_location(self, hashes: Sequence[str], location: str) -> None:
        self._record("set_location", hashes, location)
        for digest in hashes:
            self.torrents.setdefault(digest, {})["save_path"] = location

    async def set_upload_limit(self, hashes: Sequence[str], limit: int) -> None:
        self._record("set_upload_limit", hashes, limit)
        for digest in hashes:
            self.torrents.setdefault(digest, {})["upload_limit"] = limit

    async def set_download_limit(self, hashes: Sequence[str], limit: int) -> None:
        self._record("set_download_limit", hashes, limit)
        for digest in hashes:
            self.torrents.setdefault(digest, {})["download_limit"] = limit

    async def set_force_start(self, hashes: Sequence[str], on: bool) -> None:
        self._record("set_force_start", hashes, on)
        for digest in hashes:
            self.torrents.setdefault(digest, {})["force_start"] = on

    async def start(self, hashes: Sequence[str]) -> None:
        self._record("start", hashes)
        for digest in hashes:
            self.torrents.setdefault(digest, {})["state"] = "uploading"

    async def stop(self, hashes: Sequence[str]) -> None:
        self._record("stop", hashes)
        for digest in hashes:
            self.torrents.setdefault(digest, {})["state"] = "pausedUP"

    async def export(self, torrent_hash: str) -> bytes:
        self._record("export", [torrent_hash])
        return self.exports.get(torrent_hash, b"d8:announce0:e")


@dataclass
class FakeAdapter:
    source_id: int
    kind: SourceKind
    payload: SourcePayload
    client: FakeQbtClient | None = None
    health: HealthResult = field(default_factory=lambda: HealthResult.healthy(1))

    async def test(self) -> HealthResult:
        return self.health

    async def fetch(self) -> SourcePayload:
        return self.payload

    async def aclose(self) -> None:
        return None


class FakeSourceCache:
    """Stands in for the real cache, returning fixed payloads."""

    def __init__(
        self,
        adapters: dict[int, FakeAdapter] | None = None,
        mapper: PathMapper | None = None,
    ) -> None:
        self._adapters = adapters or {}
        self._mapper = mapper or PathMapper()
        self.refresh_count = 0
        self.fetch_count = 0

    def set_payload(self, source_id: int, payload: SourcePayload) -> None:
        self._adapters[source_id].payload = payload

    async def refresh_registry(self, *, max_concurrency: int = 8) -> None:
        self.refresh_count += 1

    def adapters(self) -> dict[int, FakeAdapter]:
        return dict(self._adapters)

    def adapter(self, source_id: int) -> FakeAdapter | None:
        return self._adapters.get(source_id)

    def invalidate(self, source_id: int) -> None:
        return None

    def invalidate_all(self) -> None:
        return None

    async def path_mapper(self, source_id: int | None = None) -> PathMapper:
        return self._mapper

    async def get_many(
        self, source_ids, ttl_for: dict[int, int], *, workers: int
    ) -> list[SourcePayload]:
        self.fetch_count += 1
        return [self._adapters[sid].payload for sid in source_ids if sid in self._adapters]

    async def get(self, source_id: int, ttl_seconds: int) -> SourcePayload | None:
        adapter = self._adapters.get(source_id)
        return adapter.payload if adapter else None

    async def test(self, source_id: int) -> HealthResult:
        adapter = self._adapters.get(source_id)
        return adapter.health if adapter else HealthResult.unhealthy("not configured", 0)

    async def aclose(self) -> None:
        return None
