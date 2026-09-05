"""Tier-1 configuration: process and infrastructure settings.

Read from environment variables (and an optional .env), never from the database.
These are the knobs an operator sets in docker-compose before the app can talk to
its own storage -- database location, bind address, log level, key material.

Tier-2 settings (engine cadence, dry-run, parallelism) live in the ``app_settings``
table and are edited in the UI; see :mod:`qbitflow.settings`.
"""

from __future__ import annotations

from functools import lru_cache
from pathlib import Path
from typing import Literal

from pydantic import Field, field_validator
from pydantic_settings import BaseSettings, SettingsConfigDict


class AppConfig(BaseSettings):
    model_config = SettingsConfigDict(
        env_prefix="QBITFLOW_",
        env_file=".env",
        env_file_encoding="utf-8",
        extra="ignore",
    )

    # --- storage -----------------------------------------------------------
    data_dir: Path = Field(default=Path("data"))
    db_path: Path | None = Field(default=None, description="Defaults to <data_dir>/qbitflow.db")
    secrets_key_dir: Path | None = Field(default=None, description="Defaults to <data_dir>/keys")
    secret_key: str | None = Field(
        default=None,
        description="Base64 urlsafe 32-byte key for encrypting stored credentials. "
        "When unset, a key file is generated under secrets_key_dir.",
    )
    exports_dir: Path = Field(default=Path("exports"))

    # --- server ------------------------------------------------------------
    host: str = "0.0.0.0"  # noqa: S104 - a self-hosted container binds all interfaces
    port: int = 8080
    allowed_hosts: list[str] = Field(default_factory=lambda: ["*"])
    behind_proxy: bool = False
    session_ttl_hours: int = 24 * 14

    # --- behaviour ---------------------------------------------------------
    log_level: Literal["DEBUG", "INFO", "WARNING", "ERROR"] = "INFO"
    log_json: bool = False
    testing: bool = Field(
        default=False,
        description="Suppresses scheduler startup so tests never race the engine.",
    )
    enable_script_action: bool = Field(
        default=False,
        description="Enables the script.run action, which executes commands on this host.",
    )
    timezone: str = "UTC"

    @field_validator("allowed_hosts", mode="before")
    @classmethod
    def _split_hosts(cls, v: object) -> object:
        if isinstance(v, str):
            return [h.strip() for h in v.split(",") if h.strip()]
        return v

    @property
    def resolved_db_path(self) -> Path:
        return self.db_path or (self.data_dir / "qbitflow.db")

    @property
    def resolved_key_dir(self) -> Path:
        return self.secrets_key_dir or (self.data_dir / "keys")

    @property
    def database_url(self) -> str:
        return f"sqlite+aiosqlite:///{self.resolved_db_path.as_posix()}"

    @property
    def sync_database_url(self) -> str:
        """Alembic and other synchronous tooling."""
        return f"sqlite:///{self.resolved_db_path.as_posix()}"


@lru_cache(maxsize=1)
def get_config() -> AppConfig:
    return AppConfig()


def reset_config_cache() -> None:
    """Tests mutate the environment between cases; let them clear the cache."""
    get_config.cache_clear()
