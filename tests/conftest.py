"""Shared fixtures.

Tests run against a real SQLite file with the real schema, swapping only the
things that reach the network. Mocking the database would hide exactly the
class of bug -- constraint violations, cascade behaviour, transaction scope --
that integration tests exist to catch.
"""

from __future__ import annotations

from collections.abc import AsyncIterator, Iterator
from pathlib import Path

import pytest
import pytest_asyncio

from qbitflow.config import AppConfig
from qbitflow.crypto import SecretProtector, generate_key
from qbitflow.db.base import Base, create_engine, create_session_factory
from qbitflow.domain import SourceKind
from qbitflow.engine.runner import RuleRunner
from qbitflow.settings import SettingsStore
from qbitflow.snapshot.builder import SnapshotHolder
from qbitflow.sources.base import SourcePayload
from qbitflow.state import AppState
from tests.fixtures import payloads as fx
from tests.fixtures.fakes import FakeAdapter, FakeQbtClient, FakeSourceCache


@pytest.fixture
def config(tmp_path: Path) -> Iterator[AppConfig]:
    yield AppConfig(
        data_dir=tmp_path / "data",
        exports_dir=tmp_path / "exports",
        secret_key=generate_key(),
        testing=True,
        enable_script_action=False,
    )


@pytest_asyncio.fixture
async def state(config: AppConfig) -> AsyncIterator[AppState]:
    config.data_dir.mkdir(parents=True, exist_ok=True)
    engine = create_engine(config.resolved_db_path)
    # create_all rather than Alembic: migrations are exercised by their own test,
    # and every other test wants a schema in milliseconds.
    async with engine.begin() as conn:
        await conn.run_sync(Base.metadata.create_all)

    factory = create_session_factory(engine)
    app_state = AppState(
        config=config,
        engine=engine,
        session_factory=factory,
        settings=SettingsStore(factory),
        protector=SecretProtector(
            __import__("base64").urlsafe_b64decode(config.secret_key)
        ),
        snapshot=SnapshotHolder(),
    )
    try:
        yield app_state
    finally:
        if app_state.snapshot is not None:
            await app_state.snapshot.close()
        await engine.dispose()


@pytest.fixture
def qbt_client() -> FakeQbtClient:
    return FakeQbtClient()


@pytest.fixture
def source_cache(qbt_client: FakeQbtClient) -> FakeSourceCache:
    """One qBittorrent instance with no torrents; tests supply their own."""
    return FakeSourceCache(
        {
            1: FakeAdapter(
                source_id=1,
                kind=SourceKind.QBITTORRENT,
                payload=fx.qbt_payload([]),
                client=qbt_client,
            )
        }
    )


@pytest_asyncio.fixture
async def runner(state: AppState, source_cache: FakeSourceCache) -> RuleRunner:
    state.source_cache = source_cache  # type: ignore[assignment]
    return RuleRunner(state)


def load_torrents(
    cache: FakeSourceCache, client: FakeQbtClient, payload: SourcePayload
) -> None:
    """Point both the cache and the fake client at the same set of torrents.

    The client's copy is what mutations act on; re-fetching after a run then
    reflects those mutations, which is how idempotency gets tested.
    """
    cache.set_payload(payload.source_id, payload)
    client.torrents = {
        t.hash: {
            "tags": list(t.tags),
            "category": t.category,
            "save_path": t.save_path,
            "upload_limit": t.upload_limit,
            "download_limit": t.download_limit,
            "force_start": t.force_start,
            "state": t.state,
        }
        for t in payload.torrents
    }


def apply_client_state(payload: SourcePayload, client: FakeQbtClient) -> SourcePayload:
    """Rebuild a payload from the fake client's current state.

    Simulates the next cycle re-reading qBittorrent after a run changed things.
    """
    for torrent in payload.torrents:
        current = client.torrents.get(torrent.hash)
        if not current:
            continue
        torrent.tags = list(current.get("tags", torrent.tags))
        torrent.category = current.get("category", torrent.category)
        torrent.save_path = current.get("save_path", torrent.save_path)
        torrent.upload_limit = current.get("upload_limit", torrent.upload_limit)
        torrent.download_limit = current.get("download_limit", torrent.download_limit)
        torrent.force_start = current.get("force_start", torrent.force_start)
        torrent.state = current.get("state", torrent.state)
    return payload
