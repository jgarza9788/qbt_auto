"""The provider interface and the normalized records every adapter produces.

Rules are written against these records, never against vendor JSON. Adding a new
service means writing one file that implements :class:`SourceAdapter` and returns
these types; nothing else in the application needs to change.

Every field here is optional in practice -- a service that does not expose
something returns ``None`` rather than a sentinel, so a rule can distinguish
"never watched" from "watched zero times".
"""

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime
from typing import Any

from qbitflow.domain import HealthResult, SourceKind


@dataclass(slots=True)
class TorrentRecord:
    source_id: int
    source_name: str
    hash: str
    name: str
    category: str = ""
    tags: list[str] = field(default_factory=list)
    save_path: str = ""
    content_path: str = ""
    state: str = ""
    size: int = 0
    total_size: int = 0
    downloaded: int = 0
    uploaded: int = 0
    amount_left: int = 0
    progress: float = 0.0
    ratio: float = 0.0
    download_limit: int = 0
    upload_limit: int = 0
    download_speed: int = 0
    upload_speed: int = 0
    num_seeds: int = 0
    num_leechs: int = 0
    num_complete: int = 0
    num_incomplete: int = 0
    priority: int = 0
    active_time_seconds: int = 0
    seeding_time_seconds: int = 0
    eta_seconds: int | None = None
    availability: float = 0.0
    tracker: str = ""
    added_on: datetime | None = None
    completion_on: datetime | None = None
    last_activity: datetime | None = None
    last_seen_complete: datetime | None = None
    force_start: bool = False
    auto_managed: bool = False
    super_seeding: bool = False


@dataclass(slots=True)
class TorrentFileRecord:
    source_id: int
    torrent_hash: str
    index: int
    path: str
    size: int = 0
    progress: float = 0.0
    priority: int = 0


@dataclass(slots=True)
class MediaFileRef:
    path: str
    size_bytes: int | None = None


@dataclass(slots=True)
class MediaItemRecord:
    """One movie, series or episode in a library."""

    source_id: int
    source_name: str
    source_item_id: str
    title: str
    #: One of "movie", "show", "episode".
    media_type: str
    year: int | None = None
    #: The library's own score, on whatever scale it uses (usually 0-10).
    rating: float | None = None
    audience_rating: float | None = None
    #: The signed-in user's own star rating, where the service exposes one.
    user_rating: float | None = None
    #: Age classification, e.g. "PG-13", "TV-MA".
    content_rating: str | None = None
    genres: list[str] = field(default_factory=list)
    duration_ms: int | None = None
    added_at: datetime | None = None
    series_title: str | None = None
    season: int | None = None
    episode: int | None = None
    files: list[MediaFileRef] = field(default_factory=list)


@dataclass(slots=True)
class WatchRecord:
    """Aggregate viewing activity for one media item from one source.

    ``window_counts`` carries per-period play counts keyed ``all``/``year``/
    ``month``/``week`` for the services that expose them (Tautulli, Jellystat).
    Services that only report a lifetime total leave it empty; a rule asking
    about a window over such a source gets no rows rather than a wrong answer.
    """

    source_id: int
    source_name: str
    source_item_id: str
    title: str
    media_type: str
    play_count: int = 0
    last_played_at: datetime | None = None
    window_counts: dict[str, int] = field(default_factory=dict)
    #: Total watch duration where reported, for "started but abandoned" rules.
    watched_seconds: int | None = None
    user: str | None = None


@dataclass(slots=True)
class StorageRecord:
    """Disk usage for one named path.

    ``available`` is false when the path does not exist or cannot be stat-ed --
    an unmounted network share is the common case, and it must not raise.
    Percentages are 0-100, not fractions.
    """

    name: str
    path: str
    available: bool = True
    error: str | None = None
    total_bytes: int = 0
    used_bytes: int = 0
    free_bytes: int = 0
    used_percent: float = 0.0
    free_percent: float = 0.0
    filesystem: str | None = None
    mountpoint: str | None = None
    #: Recursive size of the folder itself, when enabled. Scanned on a slower
    #: cadence than the rest because it walks the tree.
    folder_size_bytes: int | None = None
    folder_size_measured_at: datetime | None = None


@dataclass(slots=True)
class SourcePayload:
    """Everything one adapter produced in one fetch."""

    source_id: int
    source_name: str
    kind: SourceKind
    fetched_at: datetime
    torrents: list[TorrentRecord] = field(default_factory=list)
    torrent_files: list[TorrentFileRecord] = field(default_factory=list)
    media_items: list[MediaItemRecord] = field(default_factory=list)
    watch_records: list[WatchRecord] = field(default_factory=list)
    storage: list[StorageRecord] = field(default_factory=list)
    #: Set when the fetch partially or wholly failed. The cycle continues; the
    #: run records the error and the previous snapshot rows are simply absent.
    error: str | None = None

    @property
    def ok(self) -> bool:
        return self.error is None


@dataclass(slots=True)
class SourceContext:
    """Everything an adapter needs to talk to one configured instance."""

    source_id: int
    name: str
    kind: SourceKind
    base_url: str
    username: str = ""
    secret: str = ""
    timeout_seconds: int = 30
    verify_ssl: bool = True
    options: dict[str, Any] = field(default_factory=dict)
    #: Concurrency ceiling for this one host, so a small NAS is not flooded.
    max_concurrency: int = 8


class SourceAdapter(ABC):
    """One configured instance of one service.

    Adapters are constructed per source connection and cached for as long as that
    connection's settings are unchanged, so they may hold a live HTTP client and
    a login cookie.
    """

    kind: SourceKind

    def __init__(self, ctx: SourceContext) -> None:
        self.ctx = ctx

    @property
    def source_id(self) -> int:
        return self.ctx.source_id

    @abstractmethod
    async def test(self) -> HealthResult:
        """Verify credentials and reachability. Must never raise."""

    @abstractmethod
    async def fetch(self) -> SourcePayload:
        """Pull everything this source contributes to a snapshot.

        Must never raise: return a payload carrying ``error`` instead, so one
        dead service cannot fail the cycle for the others.
        """

    async def sample(self, limit: int = 10) -> dict[str, Any]:
        """A few real records, for the "does this adapter work?" button.

        The default implementation just truncates a full fetch; adapters with a
        cheaper way to get a handful of rows should override it.
        """
        payload = await self.fetch()
        return {
            "error": payload.error,
            "torrents": [t.name for t in payload.torrents[:limit]],
            "media_items": [m.title for m in payload.media_items[:limit]],
            "watch_records": [w.title for w in payload.watch_records[:limit]],
            "storage": [s.name for s in payload.storage[:limit]],
        }

    async def aclose(self) -> None:
        """Release any held connections."""
        return None
