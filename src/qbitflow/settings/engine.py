"""The engine's settings, as one immutable clamped object.

Read in a single query and handed around whole, so the scheduler, the runner, the
dashboard, the settings page and the JSON API can never disagree about what the
engine is currently configured to do.

Clamping happens on both read and write. A hand-edited form or a stale row must
not be able to push the engine below a floor that exists to protect the services
it talks to.
"""

from __future__ import annotations

from enum import StrEnum
from typing import Any

from pydantic import BaseModel, ConfigDict, Field

from qbitflow.domain import PARALLELISM_WORKERS, ParallelismTier, SourceKind

#: The spec's floor. A rule that fires more often than this hammers every service
#: it reads without any plausible benefit -- torrent state does not change that fast.
MIN_RULE_INTERVAL_SECONDS = 300

MIN_SOURCE_TTL_SECONDS = 15
MAX_SOURCE_TTL_SECONDS = 86_400

#: How long a fetch from each kind of source stays fresh. qBittorrent is cheap and
#: changes constantly; a Plex library scan is expensive and changes slowly.
DEFAULT_SOURCE_TTL_SECONDS: dict[SourceKind, int] = {
    SourceKind.QBITTORRENT: 60,
    SourceKind.STORAGE: 120,
    SourceKind.TAUTULLI: 1_800,
    SourceKind.JELLYSTAT: 1_800,
    SourceKind.JELLYGLANCE: 1_800,
    SourceKind.PLEX: 21_600,
    SourceKind.JELLYFIN: 21_600,
}


class SettingKey(StrEnum):
    ENGINE_ENABLED = "engine_enabled"
    DRY_RUN = "dry_run"
    PARALLELISM = "parallelism"
    STOP_ON_FIRST_MATCH = "stop_on_first_match"
    SOURCE_TTL_OVERRIDES = "source_ttl_overrides"
    RUN_RETENTION = "run_retention"
    PER_HOST_CONNECTION_CAP = "per_host_connection_cap"
    ACTION_BATCH_SIZE = "action_batch_size"
    TIMEZONE = "timezone"
    # Auth and setup state, kept here so first-run detection is one query.
    SETUP_COMPLETE = "setup_complete"


def _clamp(value: int, low: int, high: int) -> int:
    return max(low, min(high, value))


class EngineSettings(BaseModel):
    """Frozen so a caller cannot mutate a shared instance mid-cycle."""

    model_config = ConfigDict(frozen=True)

    #: Global kill switch. When false the scheduler stops starting cycles; a cycle
    #: already in flight is allowed to finish rather than being torn up mid-action.
    engine_enabled: bool = True

    #: Safe by default. A tool that rewrites categories and moves files across a
    #: media library should be inert until the user explicitly opts in.
    dry_run: bool = True

    parallelism: ParallelismTier = ParallelismTier.MEDIUM
    stop_on_first_match: bool = False

    source_ttl_overrides: dict[str, int] = Field(default_factory=dict)

    run_retention: int = Field(default=200, ge=10, le=10_000)

    #: Ceiling on concurrent requests to any single host, applied on top of the
    #: parallelism tier. A NAS or a Pi running qBittorrent has one WebUI thread.
    per_host_connection_cap: int = Field(default=8, ge=1, le=64)

    #: qBittorrent takes pipe-delimited hash lists; past a few hundred the URL
    #: gets unwieldy and some reverse proxies start refusing it.
    action_batch_size: int = Field(default=100, ge=1, le=500)

    timezone: str = "UTC"

    @property
    def workers(self) -> int:
        return PARALLELISM_WORKERS[self.parallelism]

    def ttl_for(self, kind: SourceKind) -> int:
        override = self.source_ttl_overrides.get(str(kind))
        if override is not None:
            return _clamp(int(override), MIN_SOURCE_TTL_SECONDS, MAX_SOURCE_TTL_SECONDS)
        return DEFAULT_SOURCE_TTL_SECONDS.get(kind, 300)

    @classmethod
    def clamped(cls, **values: Any) -> EngineSettings:
        """Build from loose values, correcting anything out of range.

        Used on both the read and the write path.
        """
        ttls = values.get("source_ttl_overrides") or {}
        if isinstance(ttls, dict):
            values["source_ttl_overrides"] = {
                str(k): _clamp(int(v), MIN_SOURCE_TTL_SECONDS, MAX_SOURCE_TTL_SECONDS)
                for k, v in ttls.items()
                if str(k) in set(SourceKind)
            }
        for key, low, high in (
            ("run_retention", 10, 10_000),
            ("per_host_connection_cap", 1, 64),
            ("action_batch_size", 1, 500),
        ):
            if values.get(key) is not None:
                values[key] = _clamp(int(values[key]), low, high)
        return cls(**values)


#: Returned when the database is unreachable, so a degraded app still reports a
#: coherent -- and safe -- configuration rather than crashing the dashboard.
FALLBACK_SETTINGS = EngineSettings()
