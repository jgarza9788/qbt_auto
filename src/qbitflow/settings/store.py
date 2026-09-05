"""Reads and writes the ``app_settings`` key/value table.

Everything the engine needs comes back in one query as a single frozen
:class:`EngineSettings`. Callers never see a raw string, and never assemble the
settings object themselves.
"""

from __future__ import annotations

import json
from typing import Any

import structlog
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker

from qbitflow.db.base import session_scope
from qbitflow.db.models import AppSetting
from qbitflow.domain import ParallelismTier
from qbitflow.settings.engine import FALLBACK_SETTINGS, EngineSettings, SettingKey

log = structlog.get_logger(__name__)

_BOOL_KEYS = {
    SettingKey.ENGINE_ENABLED,
    SettingKey.DRY_RUN,
    SettingKey.STOP_ON_FIRST_MATCH,
    SettingKey.SETUP_COMPLETE,
}
_INT_KEYS = {
    SettingKey.RUN_RETENTION,
    SettingKey.PER_HOST_CONNECTION_CAP,
    SettingKey.ACTION_BATCH_SIZE,
}
_JSON_KEYS = {SettingKey.SOURCE_TTL_OVERRIDES}


def _decode(key: str, raw: str) -> Any:
    if key in _BOOL_KEYS:
        return raw.strip().lower() in {"1", "true", "yes", "on"}
    if key in _INT_KEYS:
        try:
            return int(raw)
        except ValueError:
            return None
    if key in _JSON_KEYS:
        try:
            return json.loads(raw)
        except json.JSONDecodeError:
            log.warning("settings.bad_json", key=key)
            return None
    return raw


def _encode(value: Any) -> str:
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, dict | list):
        return json.dumps(value, separators=(",", ":"))
    return str(value)


class SettingsStore:
    def __init__(self, session_factory: async_sessionmaker[AsyncSession]) -> None:
        self._factory = session_factory

    async def get_all(self) -> dict[str, Any]:
        async with self._factory() as session:
            rows = (await session.execute(select(AppSetting))).scalars().all()
        return {r.key: _decode(r.key, r.value) for r in rows}

    async def get_engine_settings(self) -> EngineSettings:
        """One query, clamped, frozen.

        Falls back to safe defaults rather than raising: a dashboard that cannot
        reach the database should still render and say so.
        """
        try:
            raw = await self.get_all()
        except Exception:  # noqa: BLE001 - degraded mode is better than a 500 here
            log.exception("settings.read_failed")
            return FALLBACK_SETTINGS

        values: dict[str, Any] = {}
        for key in (
            SettingKey.ENGINE_ENABLED,
            SettingKey.DRY_RUN,
            SettingKey.STOP_ON_FIRST_MATCH,
            SettingKey.SOURCE_TTL_OVERRIDES,
            SettingKey.RUN_RETENTION,
            SettingKey.PER_HOST_CONNECTION_CAP,
            SettingKey.ACTION_BATCH_SIZE,
            SettingKey.TIMEZONE,
        ):
            if raw.get(key) is not None:
                values[str(key)] = raw[key]

        parallelism = raw.get(SettingKey.PARALLELISM)
        if parallelism in set(ParallelismTier):
            values["parallelism"] = ParallelismTier(parallelism)

        try:
            return EngineSettings.clamped(**values)
        except Exception:  # noqa: BLE001 - a corrupt row must not brick the app
            log.exception("settings.invalid", values=values)
            return FALLBACK_SETTINGS

    async def get(self, key: str, default: Any = None) -> Any:
        async with self._factory() as session:
            row = await session.get(AppSetting, key)
        if row is None:
            return default
        return _decode(key, row.value)

    async def set(self, key: str, value: Any) -> None:
        await self.set_many({key: value})

    async def set_many(self, values: dict[str, Any]) -> None:
        async with session_scope(self._factory) as session:
            for key, value in values.items():
                row = await session.get(AppSetting, key)
                if row is None:
                    session.add(AppSetting(key=key, value=_encode(value)))
                else:
                    row.value = _encode(value)

    async def save_engine_settings(self, settings: EngineSettings) -> EngineSettings:
        """Clamp, persist, and return what was actually stored.

        Returning the clamped value lets the UI show the user that their 2-second
        TTL became 15 rather than silently pretending it took.
        """
        clamped = EngineSettings.clamped(**settings.model_dump())
        await self.set_many(
            {
                SettingKey.ENGINE_ENABLED: clamped.engine_enabled,
                SettingKey.DRY_RUN: clamped.dry_run,
                SettingKey.PARALLELISM: str(clamped.parallelism),
                SettingKey.STOP_ON_FIRST_MATCH: clamped.stop_on_first_match,
                SettingKey.SOURCE_TTL_OVERRIDES: clamped.source_ttl_overrides,
                SettingKey.RUN_RETENTION: clamped.run_retention,
                SettingKey.PER_HOST_CONNECTION_CAP: clamped.per_host_connection_cap,
                SettingKey.ACTION_BATCH_SIZE: clamped.action_batch_size,
                SettingKey.TIMEZONE: clamped.timezone,
            }
        )
        return clamped
