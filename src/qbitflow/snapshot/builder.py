"""Materialises source payloads into the in-memory snapshot database.

All the expensive, messy work happens here, once: path normalization, media
matching, mount attribution, watch aggregation, popularity ranking. What comes
out is a plain relational database that a compiled condition can query with a
single indexed SELECT.

The snapshot is built off to the side and published by :class:`SnapshotHolder`
only when complete, so a cycle reading the current snapshot never observes a
half-built one.
"""

from __future__ import annotations

import asyncio
import contextlib
import sqlite3
from dataclasses import dataclass, field
from datetime import UTC, datetime

import structlog

from qbitflow.snapshot import udf
from qbitflow.snapshot.matching import MediaCatalog
from qbitflow.snapshot.mediakey import match_key as build_match_key
from qbitflow.snapshot.mediakey import normalize_filename, normalize_title
from qbitflow.snapshot.schema import SCHEMA_VERSION, create_schema
from qbitflow.sources.base import SourcePayload
from qbitflow.sources.paths import PathMapper, parent_dir
from qbitflow.sources.storage import Mount, list_mounts, owning_mount

log = structlog.get_logger(__name__)

GB = 1024**3
SECONDS_PER_DAY = 86_400

#: qBittorrent states that mean "not running".
PAUSED_STATES = frozenset(
    {"pausedUP", "pausedDL", "stoppedUP", "stoppedDL", "stalledUP", "queuedUP", "queuedDL"}
)
_TRULY_PAUSED = frozenset({"pausedUP", "pausedDL", "stoppedUP", "stoppedDL"})


def _epoch(value: datetime | None) -> int | None:
    return int(value.timestamp()) if value else None


def _days_since(value: datetime | None, now: datetime) -> float | None:
    if value is None:
        return None
    return round((now - value).total_seconds() / SECONDS_PER_DAY, 6)


def _csv_wrap(values: list[str]) -> str:
    """Wrap a list as ',a,b,' so membership is a LIKE on ',a,'.

    Without the wrapping, a test for the tag "hd" also matches "hdr".
    """
    cleaned = [v.strip().lower() for v in values if v and v.strip()]
    return "," + ",".join(cleaned) + "," if cleaned else ","


@dataclass(slots=True)
class SnapshotStats:
    built_at: datetime
    duration_ms: int = 0
    torrents: int = 0
    torrent_files: int = 0
    media_items: int = 0
    media_files: int = 0
    watch_records: int = 0
    storage_paths: int = 0
    matched_torrents: int = 0
    source_errors: list[str] = field(default_factory=list)


class Snapshot:
    """One immutable materialised view of every configured source."""

    def __init__(self, conn: sqlite3.Connection, stats: SnapshotStats) -> None:
        self.conn = conn
        self.stats = stats
        self.schema_version = SCHEMA_VERSION

    def query(self, sql: str, params: list | tuple = ()) -> list[sqlite3.Row]:
        cursor = self.conn.execute(sql, params)
        try:
            return cursor.fetchall()
        finally:
            cursor.close()

    def close(self) -> None:
        # Teardown must not raise; a snapshot being replaced is already gone.
        with contextlib.suppress(sqlite3.Error):
            self.conn.close()


class SnapshotHolder:
    """Holds the current snapshot and swaps in replacements atomically."""

    def __init__(self) -> None:
        self._current: Snapshot | None = None
        self._lock = asyncio.Lock()

    @property
    def current(self) -> Snapshot | None:
        return self._current

    async def publish(self, snapshot: Snapshot) -> None:
        async with self._lock:
            previous, self._current = self._current, snapshot
        if previous is not None:
            # Closed after the swap: a cycle mid-query on the old connection
            # keeps a valid handle until it finishes.
            previous.close()

    async def close(self) -> None:
        async with self._lock:
            if self._current is not None:
                self._current.close()
                self._current = None


class SnapshotBuilder:
    def __init__(self, path_mapper: PathMapper | None = None) -> None:
        self.mapper = path_mapper or PathMapper()

    async def build(
        self, payloads: list[SourcePayload], *, now: datetime | None = None
    ) -> Snapshot:
        # "Now" is a parameter rather than a call inside the derivation so that
        # every days_since_* column in one snapshot is measured from the same
        # instant, and so tests can pin it.
        started = datetime.now(UTC)
        reference = now or started
        # The whole build is CPU-and-sqlite work with no awaits inside; running
        # it in a thread keeps a large library from stalling the event loop and
        # the web UI along with it.
        snapshot = await asyncio.to_thread(self._build_sync, payloads, reference)
        snapshot.stats.duration_ms = int(
            (datetime.now(UTC) - started).total_seconds() * 1000
        )
        log.info(
            "snapshot.built",
            torrents=snapshot.stats.torrents,
            media=snapshot.stats.media_items,
            matched=snapshot.stats.matched_torrents,
            storage=snapshot.stats.storage_paths,
            ms=snapshot.stats.duration_ms,
            errors=len(snapshot.stats.source_errors),
        )
        return snapshot

    # -- construction -------------------------------------------------------

    def _build_sync(self, payloads: list[SourcePayload], now: datetime) -> Snapshot:
        conn = sqlite3.connect(":memory:", check_same_thread=False)
        conn.row_factory = sqlite3.Row
        conn.execute("PRAGMA journal_mode=OFF")
        conn.execute("PRAGMA synchronous=OFF")
        conn.execute("PRAGMA temp_store=MEMORY")
        create_schema(conn)
        udf.register(conn)

        stats = SnapshotStats(built_at=now)
        for payload in payloads:
            if payload.error:
                stats.source_errors.append(payload.error)

        self._ingest_source_status(conn, payloads, now)
        self._ingest_storage(conn, payloads, stats)
        catalog = self._ingest_media(conn, payloads, stats, now)
        self._ingest_watch(conn, payloads, stats, now)
        self._roll_up_watch(conn, now)
        self._ingest_torrents(conn, payloads, catalog, stats, now)

        conn.commit()
        # Query planner statistics: with a 10k-row torrents table and several
        # indexes, the difference between a scan and a seek is the whole budget.
        conn.execute("ANALYZE")
        conn.commit()
        return Snapshot(conn, stats)

    def _ingest_source_status(
        self, conn: sqlite3.Connection, payloads: list[SourcePayload], now: datetime
    ) -> None:
        conn.executemany(
            "INSERT INTO source_status "
            "(source_id, source_name, kind, fetched_at, ok, error, row_count) "
            "VALUES (?, ?, ?, ?, ?, ?, ?)",
            [
                (
                    p.source_id,
                    p.source_name,
                    str(p.kind),
                    int(p.fetched_at.timestamp()),
                    0 if p.error else 1,
                    p.error,
                    len(p.torrents) + len(p.media_items) + len(p.watch_records) + len(p.storage),
                )
                for p in payloads
            ],
        )

    def _ingest_storage(
        self, conn: sqlite3.Connection, payloads: list[SourcePayload], stats: SnapshotStats
    ) -> None:
        rows = []
        for payload in payloads:
            for record in payload.storage:
                rows.append(
                    (
                        record.name,
                        record.name.strip().lower(),
                        record.path,
                        self.mapper.key(record.path),
                        1 if record.available else 0,
                        record.error,
                        record.total_bytes,
                        record.used_bytes,
                        record.free_bytes,
                        round(record.total_bytes / GB, 4),
                        round(record.used_bytes / GB, 4),
                        round(record.free_bytes / GB, 4),
                        record.used_percent,
                        record.free_percent,
                        record.filesystem,
                        record.mountpoint,
                        record.folder_size_bytes,
                        round(record.folder_size_bytes / GB, 4)
                        if record.folder_size_bytes
                        else None,
                    )
                )
        if rows:
            conn.executemany(
                "INSERT OR REPLACE INTO storage_paths (name, name_key, path, path_key, available,"
                " error, total_bytes, used_bytes, free_bytes, total_gb, used_gb, free_gb,"
                " used_percent, free_percent, filesystem, mountpoint, folder_size_bytes,"
                " folder_size_gb) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                rows,
            )
        stats.storage_paths = len(rows)

    def _ingest_media(
        self,
        conn: sqlite3.Connection,
        payloads: list[SourcePayload],
        stats: SnapshotStats,
        now: datetime,
    ) -> MediaCatalog:
        """Merge library items across sources on their match key."""
        merged: dict[str, dict] = {}
        files_by_key: dict[str, list[tuple[str, int | None]]] = {}

        for payload in payloads:
            for item in payload.media_items:
                key = build_match_key(item.media_type, item.title, item.year)
                entry = merged.get(key)
                if entry is None:
                    entry = merged[key] = {
                        "match_key": key,
                        "title": item.title,
                        "title_key": normalize_title(item.title),
                        "media_type": item.media_type,
                        "year": item.year,
                        "rating": item.rating,
                        "audience_rating": item.audience_rating,
                        "user_rating": item.user_rating,
                        "content_rating": item.content_rating,
                        "genres": {g.lower() for g in item.genres},
                        "duration_ms": item.duration_ms,
                        "added_at": item.added_at,
                        "series_title": item.series_title,
                        "season": item.season,
                        "episode": item.episode,
                        "sources": {item.source_name},
                    }
                else:
                    # First non-null wins for scalars; a second source filling a
                    # gap is useful, overwriting a good value with a null is not.
                    for attr in (
                        "rating",
                        "audience_rating",
                        "user_rating",
                        "content_rating",
                        "duration_ms",
                        "added_at",
                        "series_title",
                        "season",
                        "episode",
                    ):
                        if entry[attr] is None:
                            entry[attr] = getattr(item, attr)
                    entry["genres"].update(g.lower() for g in item.genres)
                    entry["sources"].add(item.source_name)

                for ref in item.files:
                    files_by_key.setdefault(key, []).append((ref.path, ref.size_bytes))

        catalog = MediaCatalog()
        item_rows = []
        file_rows = []

        for media_item_id, (key, entry) in enumerate(merged.items(), start=1):
            item_rows.append(
                (
                    media_item_id,
                    key,
                    entry["title"],
                    entry["title_key"],
                    entry["media_type"],
                    entry["year"],
                    entry["rating"],
                    entry["audience_rating"],
                    entry["user_rating"],
                    entry["content_rating"],
                    _csv_wrap(sorted(entry["genres"])),
                    entry["duration_ms"],
                    round(entry["duration_ms"] / 60_000, 3) if entry["duration_ms"] else None,
                    _epoch(entry["added_at"]),
                    _days_since(entry["added_at"], now),
                    entry["series_title"],
                    entry["season"],
                    entry["episode"],
                    _csv_wrap(sorted(entry["sources"])),
                )
            )
            catalog.add_item(
                media_item_id,
                title=entry["title"],
                title_key=entry["title_key"],
                year=entry["year"],
                media_type=entry["media_type"],
            )

            for path, size in files_by_key.get(key, []):
                mapped = self.mapper.apply(path)
                media_key = normalize_filename(mapped)
                parent_key = normalize_filename(parent_dir(mapped))
                file_rows.append(
                    (
                        media_item_id,
                        mapped,
                        self.mapper.key(path),
                        media_key,
                        parent_key,
                        size,
                    )
                )
                catalog.add_file(
                    media_item_id, media_key=media_key, parent_key=parent_key, size=size
                )

        if item_rows:
            conn.executemany(
                "INSERT INTO media_items (id, match_key, title, title_key, media_type, year,"
                " rating, audience_rating, user_rating, content_rating, genres, duration_ms,"
                " duration_minutes, added_at, days_since_added, series_title, season, episode,"
                " source_names) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                item_rows,
            )
        if file_rows:
            conn.executemany(
                "INSERT INTO media_files (media_item_id, path, path_key, media_key, parent_key,"
                " size_bytes) VALUES (?,?,?,?,?,?)",
                file_rows,
            )

        catalog.seal()
        stats.media_items = len(item_rows)
        stats.media_files = len(file_rows)
        return catalog

    def _ingest_watch(
        self,
        conn: sqlite3.Connection,
        payloads: list[SourcePayload],
        stats: SnapshotStats,
        now: datetime,
    ) -> None:
        """Attach watch records to library items by normalized title and type."""
        lookup: dict[tuple[str, str], int] = {}
        for row in conn.execute("SELECT id, media_type, title_key FROM media_items"):
            lookup[(row["media_type"], row["title_key"])] = row["id"]

        rows = []
        for payload in payloads:
            for record in payload.watch_records:
                title_key = normalize_title(record.title)
                media_item_id = lookup.get((record.media_type, title_key))
                if media_item_id is None and record.media_type == "show":
                    # A history entry rolled up to a series may only exist in the
                    # library as its episodes.
                    media_item_id = lookup.get(("episode", title_key))
                rows.append(
                    (
                        record.source_id,
                        record.source_name,
                        media_item_id,
                        record.source_item_id,
                        record.title,
                        title_key,
                        record.media_type,
                        record.play_count,
                        _epoch(record.last_played_at),
                        _days_since(record.last_played_at, now),
                        record.watched_seconds,
                        record.window_counts.get("week"),
                        record.window_counts.get("month"),
                        record.window_counts.get("year"),
                        record.window_counts.get("all"),
                    )
                )

        if rows:
            conn.executemany(
                "INSERT INTO watch_history (source_id, source_name, media_item_id,"
                " source_item_id, title, title_key, media_type, play_count, last_played_at,"
                " days_since_played, watched_seconds, plays_week, plays_month, plays_year,"
                " plays_all) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                rows,
            )
        stats.watch_records = len(rows)

    def _roll_up_watch(self, conn: sqlite3.Connection, now: datetime) -> None:
        """Aggregate history into play_counts, then rank popularity per media type."""
        conn.execute(
            """
            INSERT INTO play_counts (media_item_id, total_plays, source_count, last_played_at,
                                     plays_week, plays_month, plays_year, plays_all)
            SELECT media_item_id,
                   SUM(play_count),
                   COUNT(DISTINCT source_id),
                   MAX(last_played_at),
                   COALESCE(SUM(plays_week), 0),
                   COALESCE(SUM(plays_month), 0),
                   COALESCE(SUM(plays_year), 0),
                   COALESCE(SUM(plays_all), 0)
            FROM watch_history
            WHERE media_item_id IS NOT NULL
            GROUP BY media_item_id
            """
        )
        now_epoch = int(now.timestamp())
        conn.execute(
            "UPDATE play_counts SET days_since_played = "
            "  CASE WHEN last_played_at IS NULL THEN NULL "
            f"      ELSE ({now_epoch} - last_played_at) / {float(SECONDS_PER_DAY)} END"
        )
        conn.execute(
            """
            UPDATE media_items SET
                watch_total = COALESCE(
                    (SELECT total_plays FROM play_counts p WHERE p.media_item_id = media_items.id),
                    0),
                last_watched_at = (
                    SELECT last_played_at FROM play_counts p
                    WHERE p.media_item_id = media_items.id),
                days_since_last_watched = (
                    SELECT days_since_played FROM play_counts p
                    WHERE p.media_item_id = media_items.id)
            """
        )

        # Popularity is ranked within a media type, not globally: "unpopular for
        # a film" and "unpopular for a TV episode" are different bars, and a
        # single ranking would make every episode look unloved next to films.
        for (media_type,) in conn.execute("SELECT DISTINCT media_type FROM media_items"):
            rows = conn.execute(
                "SELECT id, watch_total FROM media_items WHERE media_type = ? ORDER BY watch_total",
                (media_type,),
            ).fetchall()
            count = len(rows)
            if count == 0:
                continue
            if count == 1:
                conn.execute(
                    "UPDATE media_items SET watch_popularity = 1.0 WHERE id = ?", (rows[0]["id"],)
                )
                continue
            updates = [
                (round(index / (count - 1), 6), row["id"]) for index, row in enumerate(rows)
            ]
            conn.executemany(
                "UPDATE media_items SET watch_popularity = ? WHERE id = ?", updates
            )

    def _ingest_torrents(
        self,
        conn: sqlite3.Connection,
        payloads: list[SourcePayload],
        catalog: MediaCatalog,
        stats: SnapshotStats,
        now: datetime,
    ) -> None:
        mounts = list_mounts()
        usage_cache: dict[str, tuple[int, int, float, float]] = {}

        files_by_torrent: dict[tuple[int, str], list[str]] = {}
        file_rows = []
        for payload in payloads:
            for record in payload.torrent_files:
                mapped = self.mapper.apply(record.path)
                media_key = normalize_filename(mapped)
                files_by_torrent.setdefault((record.source_id, record.torrent_hash), []).append(
                    media_key
                )
                file_rows.append(
                    (
                        record.source_id,
                        record.torrent_hash,
                        record.index,
                        mapped,
                        self.mapper.key(record.path),
                        media_key,
                        record.size,
                        round(record.size / GB, 6),
                        record.progress,
                        record.priority,
                    )
                )

        torrent_rows = []
        matched = 0
        for payload in payloads:
            for t in payload.torrents:
                save_path = self.mapper.apply(t.save_path)
                content_path = self.mapper.apply(t.content_path)
                mount = owning_mount(save_path, mounts)
                usage = self._mount_usage(mount, usage_cache)

                match = catalog.match(
                    name=t.name,
                    content_path=content_path,
                    save_path=save_path,
                    size=t.size,
                    file_media_keys=files_by_torrent.get((t.source_id, t.hash), []),
                )
                if match is not None:
                    matched += 1

                torrent_rows.append(
                    (
                        t.source_id,
                        t.source_name,
                        t.hash,
                        t.name,
                        t.name.lower(),
                        t.category,
                        t.category.lower(),
                        _csv_wrap(t.tags),
                        len(t.tags),
                        save_path,
                        self.mapper.key(t.save_path),
                        content_path,
                        self.mapper.key(t.content_path),
                        normalize_filename(content_path or t.name),
                        t.state,
                        1 if t.progress >= 1.0 else 0,
                        1 if t.state in _TRULY_PAUSED else 0,
                        t.size,
                        round(t.size / GB, 6),
                        t.total_size,
                        t.downloaded,
                        t.uploaded,
                        round(t.uploaded / GB, 6),
                        t.amount_left,
                        t.progress,
                        t.ratio,
                        t.download_limit,
                        t.upload_limit,
                        t.download_speed,
                        t.upload_speed,
                        t.num_seeds,
                        t.num_leechs,
                        t.num_complete,
                        t.num_incomplete,
                        t.priority,
                        t.active_time_seconds,
                        round(t.active_time_seconds / SECONDS_PER_DAY, 6),
                        t.seeding_time_seconds,
                        round(t.seeding_time_seconds / SECONDS_PER_DAY, 6),
                        t.eta_seconds,
                        t.availability,
                        t.tracker,
                        _epoch(t.added_on),
                        _epoch(t.completion_on),
                        _epoch(t.last_activity),
                        _epoch(t.last_seen_complete),
                        _days_since(t.added_on, now),
                        _days_since(t.completion_on, now),
                        _days_since(t.last_activity, now),
                        1 if t.force_start else 0,
                        1 if t.auto_managed else 0,
                        1 if t.super_seeding else 0,
                        mount.mountpoint if mount else None,
                        usage[0] if usage else None,
                        usage[1] if usage else None,
                        usage[2] if usage else None,
                        usage[3] if usage else None,
                        match.media_item_id if match else None,
                        match.confidence if match else None,
                        match.strategy if match else None,
                    )
                )

        if torrent_rows:
            conn.executemany(
                "INSERT OR REPLACE INTO torrents (source_id, source_name, hash, name, name_lower,"
                " category, category_lower, tags, tag_count, save_path, save_path_key,"
                " content_path, content_path_key, media_key, state, is_complete, is_paused, size,"
                " size_gb, total_size, downloaded, uploaded, uploaded_gb, amount_left, progress,"
                " ratio, download_limit, upload_limit, download_speed, upload_speed, num_seeds,"
                " num_leechs, num_complete, num_incomplete, priority, active_time_seconds,"
                " active_time_days, seeding_time_seconds, seeding_time_days, eta_seconds,"
                " availability, tracker, added_on, completion_on, last_activity,"
                " last_seen_complete, days_since_added, days_since_completed, days_since_activity,"
                " force_start, auto_managed, super_seeding, mount, mount_total_bytes,"
                " mount_free_bytes, mount_used_percent, mount_free_percent, media_item_id,"
                " match_confidence, match_strategy)"
                " VALUES (" + ",".join("?" * 60) + ")",
                torrent_rows,
            )
        if file_rows:
            conn.executemany(
                "INSERT INTO torrent_files (source_id, torrent_hash, idx, path, path_key,"
                " media_key, size, size_gb, progress, priority) VALUES (?,?,?,?,?,?,?,?,?,?)",
                file_rows,
            )

        stats.torrents = len(torrent_rows)
        stats.torrent_files = len(file_rows)
        stats.matched_torrents = matched

    @staticmethod
    def _mount_usage(
        mount: Mount | None, cache: dict[str, tuple[int, int, float, float]]
    ) -> tuple[int, int, float, float] | None:
        """Usage for a mount, measured once per distinct mountpoint per build."""
        if mount is None:
            return None
        cached = cache.get(mount.mountpoint)
        if cached is not None:
            return cached
        try:
            import psutil

            usage = psutil.disk_usage(mount.mountpoint)
        except Exception:  # noqa: BLE001 - an unreadable mount is simply unknown
            return None
        used_pct = round(usage.used / usage.total * 100, 4) if usage.total else 0.0
        free_pct = round(usage.free / usage.total * 100, 4) if usage.total else 0.0
        result = (usage.total, usage.free, used_pct, free_pct)
        cache[mount.mountpoint] = result
        return result
