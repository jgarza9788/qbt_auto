"""Disk usage for user-named paths, plus the mount table rules are attributed against.

Two things live here:

* :class:`StorageAdapter` reports total/used/free for each path the user named, so
  a rule can say ``storage["downloads"].used_percent > 85``.
* :func:`list_mounts` enumerates real filesystems, which the snapshot builder uses
  to attribute each torrent to the mount its save path sits on. That attribution
  is what makes a disk-pressure rule portable -- without it every rule has to
  hard-code a mount name and stops working on anyone else's setup.

Nothing here raises. An unmounted network share is an ordinary Tuesday for a
self-hosted box, and it must degrade to "unavailable", not abort the cycle.
"""

from __future__ import annotations

import asyncio
import os
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import PurePath
from time import perf_counter

import psutil
import structlog

from qbitflow.domain import HealthResult, SourceKind
from qbitflow.sources.base import SourceAdapter, SourceContext, SourcePayload, StorageRecord

log = structlog.get_logger(__name__)

#: Kernel bookkeeping, not storage anyone writes media to. Dev7 had this filter
#: written and commented out; without it a Linux host reports dozens of /snap
#: loop devices as if they were drives.
PSEUDO_FILESYSTEMS = frozenset(
    {
        "autofs",
        "binfmt_misc",
        "bpf",
        "cgroup",
        "cgroup2",
        "configfs",
        "debugfs",
        "devpts",
        "devtmpfs",
        "efivarfs",
        "fuse.gvfsd-fuse",
        "fuse.portal",
        "fusectl",
        "hugetlbfs",
        "mqueue",
        "overlay",
        "proc",
        "pstore",
        "ramfs",
        "securityfs",
        "squashfs",
        "sysfs",
        "tmpfs",
        "tracefs",
    }
)


@dataclass(slots=True)
class StoragePathSpec:
    name: str
    path: str
    measure_folder_size: bool = False
    folder_scan_interval_minutes: int = 360


@dataclass(slots=True)
class Mount:
    mountpoint: str
    device: str
    fstype: str


def list_mounts(*, include_pseudo: bool = False) -> list[Mount]:
    """Real filesystems, longest mountpoint first.

    The ordering matters: ``/mnt/media`` must win over ``/`` when attributing a
    path that lives under both.
    """
    mounts: list[Mount] = []
    try:
        partitions = psutil.disk_partitions(all=True)
    except Exception:  # noqa: BLE001 - some containers deny this; degrade quietly
        log.debug("storage.partitions_unavailable")
        return []

    for part in partitions:
        fstype = (part.fstype or "").lower()
        if not include_pseudo and (fstype in PSEUDO_FILESYSTEMS or not fstype):
            continue
        mounts.append(Mount(mountpoint=part.mountpoint, device=part.device, fstype=part.fstype))

    mounts.sort(key=lambda m: len(m.mountpoint), reverse=True)
    return mounts


def owning_mount(path: str, mounts: list[Mount]) -> Mount | None:
    """The mount a path lives on, given a list already sorted longest-first."""
    if not path:
        return None
    try:
        # Absolute, because a relative path shares no prefix with any mountpoint.
        target = PurePath(os.path.abspath(os.path.normpath(path)))
    except (ValueError, TypeError):
        return None
    for mount in mounts:
        try:
            mount_path = PurePath(os.path.normpath(mount.mountpoint))
        except (ValueError, TypeError):
            continue
        if target == mount_path or mount_path in target.parents:
            return mount
    return None


def _directory_size(path: str) -> int:
    """Recursive size, skipping what we cannot read.

    ``os.scandir`` avoids a stat syscall per entry, which matters on a spinning
    disk holding a large library. Symlinks are not followed -- a link back up the
    tree would otherwise loop forever.
    """
    total = 0
    stack = [path]
    while stack:
        current = stack.pop()
        try:
            with os.scandir(current) as entries:
                for entry in entries:
                    try:
                        if entry.is_symlink():
                            continue
                        if entry.is_dir(follow_symlinks=False):
                            stack.append(entry.path)
                        else:
                            total += entry.stat(follow_symlinks=False).st_size
                    except OSError:
                        continue
        except OSError:
            continue
    return total


class StorageAdapter(SourceAdapter):
    """Reports every configured storage path.

    Unlike the networked adapters there is only ever one of these -- the "named
    instances" the spec asks for are the named paths, not separate services.
    """

    kind = SourceKind.STORAGE

    def __init__(self, specs: list[StoragePathSpec], ctx: SourceContext | None = None) -> None:
        super().__init__(
            ctx
            or SourceContext(
                source_id=0,
                name="storage",
                kind=SourceKind.STORAGE,
                base_url="",
            )
        )
        self.specs = specs
        # Folder walks are expensive, so their results outlive a single cycle.
        self._folder_cache: dict[str, tuple[int, datetime]] = {}

    async def test(self) -> HealthResult:
        started = perf_counter()
        missing = [s.name for s in self.specs if not os.path.exists(s.path)]
        elapsed = int((perf_counter() - started) * 1000)
        if missing:
            return HealthResult.unhealthy(
                "not present in this container: " + ", ".join(missing), elapsed
            )
        return HealthResult.healthy(elapsed)

    async def fetch(self) -> SourcePayload:
        payload = SourcePayload(
            source_id=self.ctx.source_id,
            source_name=self.ctx.name,
            kind=self.kind,
            fetched_at=datetime.now(UTC),
        )
        mounts = await asyncio.to_thread(list_mounts)
        payload.storage = [await self._measure(spec, mounts) for spec in self.specs]
        return payload

    async def _measure(self, spec: StoragePathSpec, mounts: list[Mount]) -> StorageRecord:
        record = StorageRecord(name=spec.name, path=spec.path)

        if not await asyncio.to_thread(os.path.exists, spec.path):
            record.available = False
            record.error = "path does not exist in this container"
            return record

        try:
            usage = await asyncio.to_thread(psutil.disk_usage, spec.path)
        except OSError as exc:
            record.available = False
            record.error = str(exc)
            return record

        record.total_bytes = usage.total
        record.used_bytes = usage.used
        record.free_bytes = usage.free
        if usage.total > 0:
            # 0-100, and the field is named for what it holds. Both predecessors
            # returned a 0..1 fraction from a field called PercentUsed.
            record.used_percent = round(usage.used / usage.total * 100, 4)
            record.free_percent = round(usage.free / usage.total * 100, 4)

        mount = owning_mount(spec.path, mounts)
        if mount is not None:
            record.mountpoint = mount.mountpoint
            record.filesystem = mount.fstype

        if spec.measure_folder_size:
            size, measured = await self._folder_size(spec)
            record.folder_size_bytes = size
            record.folder_size_measured_at = measured

        return record

    async def _folder_size(self, spec: StoragePathSpec) -> tuple[int | None, datetime | None]:
        now = datetime.now(UTC)
        cached = self._folder_cache.get(spec.name)
        if cached is not None:
            size, measured = cached
            age_minutes = (now - measured).total_seconds() / 60
            if age_minutes < spec.folder_scan_interval_minutes:
                return size, measured

        try:
            size = await asyncio.to_thread(_directory_size, spec.path)
        except Exception as exc:  # noqa: BLE001 - a failed walk keeps the stale value
            log.warning("storage.folder_scan_failed", name=spec.name, error=str(exc))
            return cached if cached else (None, None)

        self._folder_cache[spec.name] = (size, now)
        return size, now
