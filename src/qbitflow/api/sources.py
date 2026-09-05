"""Source connections, storage paths and path mappings.

Secrets go in and are never returned. The API reports whether a credential is
set, not what it is -- a settings page that echoes back the password it stores is
a settings page that leaks it to anything that can read the response.
"""

from __future__ import annotations

from typing import Any

from fastapi import APIRouter, HTTPException
from pydantic import BaseModel, Field
from sqlalchemy import select

from qbitflow.api.deps import StateDep
from qbitflow.db.base import session_scope
from qbitflow.db.models import PathMapping, SourceConnection, StoragePath
from qbitflow.domain import SourceKind
from qbitflow.sources.registry import env_name

router = APIRouter(prefix="/api", tags=["sources"])


class SourceIn(BaseModel):
    id: int | None = None
    name: str = Field(min_length=1, max_length=128)
    kind: SourceKind
    base_url: str = Field(min_length=1, max_length=512)
    username: str = ""
    #: Omitted or empty leaves the stored credential untouched.
    secret: str | None = None
    enabled: bool = True
    timeout_seconds: int = Field(default=30, ge=1, le=600)
    verify_ssl: bool = True
    options: dict[str, Any] = Field(default_factory=dict)


class StoragePathIn(BaseModel):
    id: int | None = None
    name: str = Field(min_length=1, max_length=128)
    path: str = Field(min_length=1, max_length=512)
    enabled: bool = True
    measure_folder_size: bool = False
    folder_scan_interval_minutes: int = Field(default=360, ge=5, le=10_080)


class PathMappingIn(BaseModel):
    id: int | None = None
    source_id: int | None = None
    from_prefix: str = Field(min_length=1, max_length=512)
    to_prefix: str = Field(min_length=1, max_length=512)
    order: int = 0


def _serialise_source(row: SourceConnection) -> dict[str, Any]:
    return {
        "id": row.id,
        "name": row.name,
        "kind": str(row.kind),
        "base_url": row.base_url,
        "username": row.username,
        # Whether a credential exists, never the credential.
        "has_secret": bool(row.secret_ciphertext),
        "enabled": row.enabled,
        "timeout_seconds": row.timeout_seconds,
        "verify_ssl": row.verify_ssl,
        "options": row.options or {},
        "env_prefix": f"QBITFLOW_SOURCE__{env_name(row.name)}__",
    }


@router.get("/sources")
async def list_sources(state: StateDep) -> list[dict[str, Any]]:
    async with state.session_factory() as session:
        rows = (
            await session.execute(select(SourceConnection).order_by(SourceConnection.name))
        ).scalars().all()
    return [_serialise_source(r) for r in rows]


@router.post("/sources")
async def save_source(payload: SourceIn, state: StateDep) -> dict[str, Any]:
    async with session_scope(state.session_factory) as session:
        row = await session.get(SourceConnection, payload.id) if payload.id else None
        if row is None:
            row = SourceConnection(name=payload.name, kind=payload.kind, base_url=payload.base_url)
            session.add(row)

        duplicate = (
            await session.execute(
                select(SourceConnection).where(
                    SourceConnection.name == payload.name,
                    SourceConnection.id != (payload.id or -1),
                )
            )
        ).scalar_one_or_none()
        if duplicate is not None:
            raise HTTPException(409, f"a source named '{payload.name}' already exists")

        row.name = payload.name
        row.kind = payload.kind
        row.base_url = payload.base_url.rstrip("/")
        row.username = payload.username
        row.enabled = payload.enabled
        row.timeout_seconds = payload.timeout_seconds
        row.verify_ssl = payload.verify_ssl
        row.options = payload.options

        if payload.secret:
            ciphertext, nonce = state.protector.protect(payload.secret)
            row.secret_ciphertext = ciphertext
            row.secret_nonce = nonce

        await session.flush()
        source_id = row.id

    await _reload(state)
    return {"id": source_id}


@router.delete("/sources/{source_id}")
async def delete_source(source_id: int, state: StateDep) -> dict[str, Any]:
    async with session_scope(state.session_factory) as session:
        row = await session.get(SourceConnection, source_id)
        if row is None:
            raise HTTPException(404, "no such source")
        await session.delete(row)
    await _reload(state)
    return {"deleted": source_id}


@router.post("/sources/{source_id}/test")
async def test_source(source_id: int, state: StateDep) -> dict[str, Any]:
    """Reachability and credentials, reported with latency either way."""
    if state.source_cache is None:
        raise HTTPException(503, "sources are not available")
    await state.source_cache.refresh_registry()
    health = await state.source_cache.test(source_id)
    return {"ok": health.ok, "latency_ms": health.latency_ms, "error": health.error}


@router.get("/sources/{source_id}/sample")
async def sample_source(source_id: int, state: StateDep, limit: int = 10) -> dict[str, Any]:
    """A handful of real records, so a user can see whether an adapter works.

    Particularly useful for the sources whose endpoints are configurable -- it
    turns "why is nothing matching" into a visible answer.
    """
    if state.source_cache is None:
        raise HTTPException(503, "sources are not available")
    await state.source_cache.refresh_registry()
    adapter = state.source_cache.adapter(source_id)
    if adapter is None:
        raise HTTPException(404, "no such source, or it is disabled")
    return await adapter.sample(limit=limit)


# --------------------------------------------------------------------------- #
# Storage paths
# --------------------------------------------------------------------------- #


@router.get("/storage")
async def list_storage(state: StateDep) -> list[dict[str, Any]]:
    async with state.session_factory() as session:
        rows = (
            await session.execute(select(StoragePath).order_by(StoragePath.name))
        ).scalars().all()

    snapshot = state.snapshot.current if state.snapshot else None
    live: dict[str, Any] = {}
    if snapshot is not None:
        for row in snapshot.query("SELECT * FROM storage_paths"):
            live[str(row["name_key"])] = {
                "available": bool(row["available"]),
                "error": row["error"],
                "total_gb": row["total_gb"],
                "used_gb": row["used_gb"],
                "free_gb": row["free_gb"],
                "used_percent": row["used_percent"],
                "folder_size_gb": row["folder_size_gb"],
            }

    return [
        {
            "id": r.id,
            "name": r.name,
            "path": r.path,
            "enabled": r.enabled,
            "measure_folder_size": r.measure_folder_size,
            "folder_scan_interval_minutes": r.folder_scan_interval_minutes,
            "live": live.get(r.name.strip().lower()),
        }
        for r in rows
    ]


@router.post("/storage")
async def save_storage(payload: StoragePathIn, state: StateDep) -> dict[str, Any]:
    async with session_scope(state.session_factory) as session:
        row = await session.get(StoragePath, payload.id) if payload.id else None
        if row is None:
            row = StoragePath(name=payload.name, path=payload.path)
            session.add(row)
        row.name = payload.name
        row.path = payload.path
        row.enabled = payload.enabled
        row.measure_folder_size = payload.measure_folder_size
        row.folder_scan_interval_minutes = payload.folder_scan_interval_minutes
        await session.flush()
        storage_id = row.id
    await _reload(state)
    return {"id": storage_id}


@router.delete("/storage/{storage_id}")
async def delete_storage(storage_id: int, state: StateDep) -> dict[str, Any]:
    async with session_scope(state.session_factory) as session:
        row = await session.get(StoragePath, storage_id)
        if row is None:
            raise HTTPException(404, "no such storage path")
        await session.delete(row)
    await _reload(state)
    return {"deleted": storage_id}


# --------------------------------------------------------------------------- #
# Path mappings
# --------------------------------------------------------------------------- #


@router.get("/path-mappings")
async def list_mappings(state: StateDep) -> list[dict[str, Any]]:
    async with state.session_factory() as session:
        rows = (
            await session.execute(select(PathMapping).order_by(PathMapping.order))
        ).scalars().all()
    return [
        {
            "id": r.id,
            "source_id": r.source_id,
            "from_prefix": r.from_prefix,
            "to_prefix": r.to_prefix,
            "order": r.order,
        }
        for r in rows
    ]


@router.post("/path-mappings")
async def save_mapping(payload: PathMappingIn, state: StateDep) -> dict[str, Any]:
    async with session_scope(state.session_factory) as session:
        row = await session.get(PathMapping, payload.id) if payload.id else None
        if row is None:
            row = PathMapping(from_prefix=payload.from_prefix, to_prefix=payload.to_prefix)
            session.add(row)
        row.source_id = payload.source_id
        row.from_prefix = payload.from_prefix
        row.to_prefix = payload.to_prefix
        row.order = payload.order
        await session.flush()
        mapping_id = row.id
    await _reload(state)
    return {"id": mapping_id}


@router.delete("/path-mappings/{mapping_id}")
async def delete_mapping(mapping_id: int, state: StateDep) -> dict[str, Any]:
    async with session_scope(state.session_factory) as session:
        row = await session.get(PathMapping, mapping_id)
        if row is None:
            raise HTTPException(404, "no such mapping")
        await session.delete(row)
    await _reload(state)
    return {"deleted": mapping_id}


async def _reload(state: StateDep) -> None:
    """Drop cached adapters and payloads so a config change takes effect now."""
    if state.source_cache is not None:
        state.source_cache.invalidate_all()
        await state.source_cache.refresh_registry()
