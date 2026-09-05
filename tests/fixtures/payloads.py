"""Builders for source payloads, so tests describe data rather than assemble it."""

from __future__ import annotations

from datetime import UTC, datetime, timedelta

from qbitflow.domain import SourceKind
from qbitflow.sources.base import (
    MediaFileRef,
    MediaItemRecord,
    SourcePayload,
    StorageRecord,
    TorrentFileRecord,
    TorrentRecord,
    WatchRecord,
)

NOW = datetime(2026, 6, 1, 12, 0, 0, tzinfo=UTC)


def days_ago(days: float, *, now: datetime = NOW) -> datetime:
    return now - timedelta(days=days)


def torrent(
    name: str,
    *,
    hash: str | None = None,  # noqa: A002 - mirrors the field name
    source_id: int = 1,
    source_name: str = "main-qbt",
    category: str = "",
    tags: list[str] | None = None,
    save_path: str = "/downloads",
    content_path: str | None = None,
    size: int = 5 * 1024**3,
    ratio: float = 1.0,
    progress: float = 1.0,
    state: str = "uploading",
    added_days_ago: float | None = 30,
    completed_days_ago: float | None = 29,
    activity_days_ago: float | None = 1,
    upload_limit: int = 0,
    download_limit: int = 0,
    **extra: object,
) -> TorrentRecord:
    digest = hash or f"{abs(builtin_hash(name)):040x}"[:40]
    return TorrentRecord(
        source_id=source_id,
        source_name=source_name,
        hash=digest,
        name=name,
        category=category,
        tags=tags or [],
        save_path=save_path,
        content_path=content_path if content_path is not None else f"{save_path}/{name}",
        state=state,
        size=size,
        total_size=size,
        progress=progress,
        ratio=ratio,
        upload_limit=upload_limit,
        download_limit=download_limit,
        added_on=days_ago(added_days_ago) if added_days_ago is not None else None,
        completion_on=days_ago(completed_days_ago) if completed_days_ago is not None else None,
        last_activity=days_ago(activity_days_ago) if activity_days_ago is not None else None,
        **extra,  # type: ignore[arg-type]
    )


builtin_hash = hash


def media_item(
    title: str,
    *,
    media_type: str = "movie",
    year: int | None = None,
    source_id: int = 2,
    source_name: str = "plex",
    files: list[str] | None = None,
    file_size: int | None = None,
    **extra: object,
) -> MediaItemRecord:
    return MediaItemRecord(
        source_id=source_id,
        source_name=source_name,
        source_item_id=f"{source_name}-{title}",
        title=title,
        media_type=media_type,
        year=year,
        files=[MediaFileRef(path=p, size_bytes=file_size) for p in (files or [])],
        **extra,  # type: ignore[arg-type]
    )


def watch(
    title: str,
    *,
    media_type: str = "movie",
    play_count: int = 1,
    last_played_days_ago: float | None = 10,
    source_id: int = 2,
    source_name: str = "plex",
    windows: dict[str, int] | None = None,
) -> WatchRecord:
    return WatchRecord(
        source_id=source_id,
        source_name=source_name,
        source_item_id=f"{source_name}-{title}",
        title=title,
        media_type=media_type,
        play_count=play_count,
        last_played_at=days_ago(last_played_days_ago) if last_played_days_ago is not None else None,
        window_counts=windows or {},
    )


def storage(
    name: str,
    *,
    path: str | None = None,
    total_gb: float = 1000,
    used_percent: float = 50.0,
    available: bool = True,
    error: str | None = None,
) -> StorageRecord:
    total = int(total_gb * 1024**3)
    used = int(total * used_percent / 100)
    return StorageRecord(
        name=name,
        path=path or f"/mnt/{name}",
        available=available,
        error=error,
        total_bytes=total if available else 0,
        used_bytes=used if available else 0,
        free_bytes=(total - used) if available else 0,
        used_percent=used_percent if available else 0.0,
        free_percent=(100 - used_percent) if available else 0.0,
    )


def qbt_payload(
    torrents: list[TorrentRecord],
    files: list[TorrentFileRecord] | None = None,
    *,
    source_id: int = 1,
    name: str = "main-qbt",
    error: str | None = None,
) -> SourcePayload:
    # A payload comes from exactly one source, so stamp its identity onto every
    # record rather than making each test repeat it.
    for record in torrents:
        record.source_id = source_id
        record.source_name = name
    for file_record in files or []:
        file_record.source_id = source_id
    return SourcePayload(
        source_id=source_id,
        source_name=name,
        kind=SourceKind.QBITTORRENT,
        fetched_at=NOW,
        torrents=torrents,
        torrent_files=files or [],
        error=error,
    )


def media_payload(
    items: list[MediaItemRecord],
    watches: list[WatchRecord] | None = None,
    *,
    source_id: int = 2,
    name: str = "plex",
    kind: SourceKind = SourceKind.PLEX,
) -> SourcePayload:
    return SourcePayload(
        source_id=source_id,
        source_name=name,
        kind=kind,
        fetched_at=NOW,
        media_items=items,
        watch_records=watches or [],
    )


def storage_payload(records: list[StorageRecord]) -> SourcePayload:
    return SourcePayload(
        source_id=0,
        source_name="storage",
        kind=SourceKind.STORAGE,
        fetched_at=NOW,
        storage=records,
    )
