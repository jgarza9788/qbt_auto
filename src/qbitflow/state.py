"""Application state, built once at startup and hung off ``app.state``.

Everything with a lifetime longer than a request lives here: the database engine,
the settings store, the secret protector, the source cache, the snapshot holder
and the scheduler. Request handlers reach it through the dependencies in
:mod:`qbitflow.api.deps` rather than importing globals.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import TYPE_CHECKING, Any

from sqlalchemy.ext.asyncio import AsyncEngine, AsyncSession, async_sessionmaker

from qbitflow.config import AppConfig
from qbitflow.crypto import SecretProtector
from qbitflow.settings import SettingsStore

if TYPE_CHECKING:  # pragma: no cover - import cycle avoidance only
    from qbitflow.engine.runner import RuleRunner
    from qbitflow.engine.scheduler import RuleScheduler
    from qbitflow.snapshot.holder import SnapshotHolder
    from qbitflow.sources.registry import SourceCache


@dataclass(slots=True)
class AppState:
    config: AppConfig
    engine: AsyncEngine
    session_factory: async_sessionmaker[AsyncSession]
    settings: SettingsStore
    protector: SecretProtector

    # Populated as later phases wire themselves in; each is optional so a partly
    # configured app (no sources yet, scheduler disabled under test) still boots.
    source_cache: SourceCache | None = None
    snapshot: SnapshotHolder | None = None
    runner: RuleRunner | None = None
    scheduler: RuleScheduler | None = None

    #: Set when startup could not complete. Health reports degraded and the UI
    #: shows the reason rather than failing with an opaque 500 on every page.
    startup_error: str | None = None

    extras: dict[str, Any] = field(default_factory=dict)
