"""Persistent schema: config, rules, and run history.

Deliberately flat. The condition tree is one JSON column rather than three
normalised tables -- nothing ever queries into it, and the whole tree is replaced
on every save, so normalising it buys nothing and costs a manual delete walk over
a self-referencing foreign key.
"""

from __future__ import annotations

from datetime import datetime
from typing import Any

from sqlalchemy import (
    JSON,
    Boolean,
    ForeignKey,
    Index,
    Integer,
    LargeBinary,
    String,
    Text,
    UniqueConstraint,
)
from sqlalchemy import (
    Enum as SAEnum,
)
from sqlalchemy.orm import Mapped, mapped_column, relationship

from qbitflow.db.base import Base
from qbitflow.db.types import UtcTimestamp, utcnow
from qbitflow.domain import ConditionMode, RunStatus, RunTrigger, SourceKind


def _enum(enum_cls: type) -> SAEnum:
    """Store a StrEnum as its value, with a CHECK constraint.

    ``native_enum=False`` keeps the column readable text rather than an opaque
    integer, and ``values_callable`` stores the lowercase values rather than the
    Python member names -- so the database says "succeeded", not "SUCCEEDED",
    and reading it back gives the enum rather than a bare string.
    """
    return SAEnum(
        enum_cls,
        native_enum=False,
        length=32,
        values_callable=lambda e: [m.value for m in e],
    )

# --------------------------------------------------------------------------- #
# Auth
# --------------------------------------------------------------------------- #


class User(Base):
    __tablename__ = "users"

    id: Mapped[int] = mapped_column(primary_key=True)
    username: Mapped[str] = mapped_column(String(64), unique=True, nullable=False)
    password_hash: Mapped[str] = mapped_column(Text, nullable=False)
    is_admin: Mapped[bool] = mapped_column(Boolean, default=True, nullable=False)
    created_at: Mapped[datetime] = mapped_column(UtcTimestamp, default=utcnow, nullable=False)
    updated_at: Mapped[datetime] = mapped_column(
        UtcTimestamp, default=utcnow, onupdate=utcnow, nullable=False
    )


class Session(Base):
    """Server-side sessions.

    The cookie carries an opaque random token; only its hash is stored, so a
    database leak does not hand over live sessions.
    """

    __tablename__ = "sessions"

    id: Mapped[int] = mapped_column(primary_key=True)
    token_hash: Mapped[str] = mapped_column(String(64), unique=True, nullable=False)
    user_id: Mapped[int] = mapped_column(
        ForeignKey("users.id", ondelete="CASCADE"), nullable=False, index=True
    )
    created_at: Mapped[datetime] = mapped_column(UtcTimestamp, default=utcnow, nullable=False)
    expires_at: Mapped[datetime] = mapped_column(UtcTimestamp, nullable=False, index=True)
    last_seen_at: Mapped[datetime] = mapped_column(UtcTimestamp, default=utcnow, nullable=False)
    user_agent: Mapped[str | None] = mapped_column(String(256))


# --------------------------------------------------------------------------- #
# Settings
# --------------------------------------------------------------------------- #


class AppSetting(Base):
    """Tier-2 key/value settings, edited in the UI.

    Values are stored as text; :mod:`qbitflow.settings` owns parsing, defaulting
    and clamping so no caller sees a raw string.
    """

    __tablename__ = "app_settings"

    key: Mapped[str] = mapped_column(String(64), primary_key=True)
    value: Mapped[str] = mapped_column(Text, nullable=False)
    updated_at: Mapped[datetime] = mapped_column(
        UtcTimestamp, default=utcnow, onupdate=utcnow, nullable=False
    )


# --------------------------------------------------------------------------- #
# Sources
# --------------------------------------------------------------------------- #


class SourceConnection(Base):
    """A named instance of a networked data source."""

    __tablename__ = "source_connections"

    id: Mapped[int] = mapped_column(primary_key=True)
    name: Mapped[str] = mapped_column(String(128), unique=True, nullable=False)
    kind: Mapped[SourceKind] = mapped_column(_enum(SourceKind), nullable=False, index=True)
    base_url: Mapped[str] = mapped_column(String(512), nullable=False)
    username: Mapped[str] = mapped_column(String(128), default="", nullable=False)
    secret_ciphertext: Mapped[bytes | None] = mapped_column(LargeBinary)
    secret_nonce: Mapped[bytes | None] = mapped_column(LargeBinary)
    enabled: Mapped[bool] = mapped_column(Boolean, default=True, nullable=False)
    timeout_seconds: Mapped[int] = mapped_column(Integer, default=30, nullable=False)
    verify_ssl: Mapped[bool] = mapped_column(Boolean, default=True, nullable=False)
    options: Mapped[dict[str, Any]] = mapped_column(JSON, default=dict, nullable=False)
    created_at: Mapped[datetime] = mapped_column(UtcTimestamp, default=utcnow, nullable=False)
    updated_at: Mapped[datetime] = mapped_column(
        UtcTimestamp, default=utcnow, onupdate=utcnow, nullable=False
    )

    path_mappings: Mapped[list[PathMapping]] = relationship(
        back_populates="source", cascade="all, delete-orphan", lazy="selectin"
    )


class PathMapping(Base):
    """Rewrites a path prefix so paths from different services line up.

    Plex may report ``/data/movies/Film.mkv`` for the file qBittorrent calls
    ``/downloads/movies/Film.mkv``. Without this, matching only works when every
    container happens to bind-mount the media root identically.
    """

    __tablename__ = "path_mappings"

    id: Mapped[int] = mapped_column(primary_key=True)
    source_id: Mapped[int | None] = mapped_column(
        ForeignKey("source_connections.id", ondelete="CASCADE"), index=True
    )
    from_prefix: Mapped[str] = mapped_column(String(512), nullable=False)
    to_prefix: Mapped[str] = mapped_column(String(512), nullable=False)
    order: Mapped[int] = mapped_column(Integer, default=0, nullable=False)

    source: Mapped[SourceConnection | None] = relationship(back_populates="path_mappings")


class StoragePath(Base):
    """A named volume, drive or folder to report disk usage for.

    Kept separate from :class:`SourceConnection` because none of that table's
    columns apply -- there is no URL, no credential and no TLS to verify.
    """

    __tablename__ = "storage_paths"

    id: Mapped[int] = mapped_column(primary_key=True)
    name: Mapped[str] = mapped_column(String(128), unique=True, nullable=False)
    path: Mapped[str] = mapped_column(String(512), nullable=False)
    enabled: Mapped[bool] = mapped_column(Boolean, default=True, nullable=False)
    #: Recursive folder sizing walks the tree, so it runs on its own slower cadence.
    measure_folder_size: Mapped[bool] = mapped_column(Boolean, default=False, nullable=False)
    folder_scan_interval_minutes: Mapped[int] = mapped_column(Integer, default=360, nullable=False)
    created_at: Mapped[datetime] = mapped_column(UtcTimestamp, default=utcnow, nullable=False)
    updated_at: Mapped[datetime] = mapped_column(
        UtcTimestamp, default=utcnow, onupdate=utcnow, nullable=False
    )


# --------------------------------------------------------------------------- #
# Rules
# --------------------------------------------------------------------------- #


class Rule(Base):
    __tablename__ = "rules"

    id: Mapped[int] = mapped_column(primary_key=True)
    name: Mapped[str] = mapped_column(String(128), nullable=False)
    description: Mapped[str] = mapped_column(Text, default="", nullable=False)
    enabled: Mapped[bool] = mapped_column(Boolean, default=True, nullable=False)
    #: Position in the single global ordered list; lower runs first.
    order: Mapped[int] = mapped_column(Integer, default=0, nullable=False, index=True)

    condition_mode: Mapped[ConditionMode] = mapped_column(
        _enum(ConditionMode), default=ConditionMode.BUILDER, nullable=False
    )
    #: The structured tree the visual builder edits. Compiled to SQL, never executed directly.
    condition_tree: Mapped[dict[str, Any] | None] = mapped_column(JSON)
    #: Advanced mode: a user-written WHERE clause or full SELECT over the snapshot schema.
    raw_sql: Mapped[str | None] = mapped_column(Text)

    compiled_sql: Mapped[str | None] = mapped_column(Text)
    compiled_params: Mapped[list[Any] | None] = mapped_column(JSON)
    compile_valid: Mapped[bool] = mapped_column(Boolean, default=False, nullable=False)
    compile_error: Mapped[str | None] = mapped_column(Text)
    compiled_at: Mapped[datetime | None] = mapped_column(UtcTimestamp)

    #: qBittorrent source ids this rule acts on. Empty means every enabled instance.
    target_source_ids: Mapped[list[int]] = mapped_column(JSON, default=list, nullable=False)

    cron: Mapped[str] = mapped_column(String(128), default="*/15 * * * *", nullable=False)
    timezone: Mapped[str | None] = mapped_column(String(64))
    #: Per-rule override of the global dry-run switch. Null inherits the global value.
    dry_run: Mapped[bool | None] = mapped_column(Boolean)
    #: Null inherits the global stop-on-first-match setting.
    stop_on_match: Mapped[bool | None] = mapped_column(Boolean)
    cooldown_seconds: Mapped[int | None] = mapped_column(Integer)

    created_at: Mapped[datetime] = mapped_column(UtcTimestamp, default=utcnow, nullable=False)
    updated_at: Mapped[datetime] = mapped_column(
        UtcTimestamp, default=utcnow, onupdate=utcnow, nullable=False
    )
    last_run_at: Mapped[datetime | None] = mapped_column(UtcTimestamp)
    last_matched_count: Mapped[int | None] = mapped_column(Integer)

    actions: Mapped[list[RuleAction]] = relationship(
        back_populates="rule",
        cascade="all, delete-orphan",
        order_by="RuleAction.order",
        lazy="selectin",
    )


class RuleAction(Base):
    """One action in a rule's ordered action list.

    A rule may carry several -- "tag it and move it" should not require two rules
    with duplicated conditions.
    """

    __tablename__ = "rule_actions"

    id: Mapped[int] = mapped_column(primary_key=True)
    rule_id: Mapped[int] = mapped_column(
        ForeignKey("rules.id", ondelete="CASCADE"), nullable=False, index=True
    )
    order: Mapped[int] = mapped_column(Integer, default=0, nullable=False)
    type: Mapped[str] = mapped_column(String(64), nullable=False)
    params: Mapped[dict[str, Any]] = mapped_column(JSON, default=dict, nullable=False)

    rule: Mapped[Rule] = relationship(back_populates="actions")


# --------------------------------------------------------------------------- #
# Run history
# --------------------------------------------------------------------------- #


class RunHistory(Base):
    __tablename__ = "run_history"

    id: Mapped[int] = mapped_column(primary_key=True)
    trigger: Mapped[RunTrigger] = mapped_column(_enum(RunTrigger), nullable=False)
    #: Null for a run covering every enabled rule.
    rule_id: Mapped[int | None] = mapped_column(Integer)
    dry_run: Mapped[bool] = mapped_column(Boolean, default=False, nullable=False)
    status: Mapped[RunStatus] = mapped_column(_enum(RunStatus), nullable=False)

    started_at: Mapped[datetime] = mapped_column(
        UtcTimestamp, default=utcnow, nullable=False, index=True
    )
    finished_at: Mapped[datetime | None] = mapped_column(UtcTimestamp)
    duration_ms: Mapped[int | None] = mapped_column(Integer)

    torrents_evaluated: Mapped[int] = mapped_column(Integer, default=0, nullable=False)
    rules_evaluated: Mapped[int] = mapped_column(Integer, default=0, nullable=False)
    torrents_matched: Mapped[int] = mapped_column(Integer, default=0, nullable=False)
    actions_applied: Mapped[int] = mapped_column(Integer, default=0, nullable=False)
    actions_would_apply: Mapped[int] = mapped_column(Integer, default=0, nullable=False)
    actions_skipped: Mapped[int] = mapped_column(Integer, default=0, nullable=False)
    error_count: Mapped[int] = mapped_column(Integer, default=0, nullable=False)
    summary: Mapped[dict[str, Any] | None] = mapped_column(JSON)

    rule_results: Mapped[list[RunRuleResult]] = relationship(
        back_populates="run", cascade="all, delete-orphan", lazy="selectin"
    )


class RunRuleResult(Base):
    __tablename__ = "run_rule_results"

    id: Mapped[int] = mapped_column(primary_key=True)
    run_id: Mapped[int] = mapped_column(
        ForeignKey("run_history.id", ondelete="CASCADE"), nullable=False, index=True
    )
    rule_id: Mapped[int | None] = mapped_column(Integer)
    #: Denormalised so history stays readable after the rule is deleted.
    rule_name: Mapped[str] = mapped_column(String(128), nullable=False)

    matched_count: Mapped[int] = mapped_column(Integer, default=0, nullable=False)
    actions_applied: Mapped[int] = mapped_column(Integer, default=0, nullable=False)
    actions_would_apply: Mapped[int] = mapped_column(Integer, default=0, nullable=False)
    actions_skipped: Mapped[int] = mapped_column(Integer, default=0, nullable=False)
    error_count: Mapped[int] = mapped_column(Integer, default=0, nullable=False)
    duration_ms: Mapped[int | None] = mapped_column(Integer)
    error: Mapped[str | None] = mapped_column(Text)

    run: Mapped[RunHistory] = relationship(back_populates="rule_results")


class RunLogEntry(Base):
    __tablename__ = "run_log_entries"
    __table_args__ = (Index("ix_run_log_entries_run_seq", "run_id", "seq"),)

    id: Mapped[int] = mapped_column(primary_key=True)
    run_id: Mapped[int] = mapped_column(
        ForeignKey("run_history.id", ondelete="CASCADE"), nullable=False
    )
    #: Monotonic within a run, so a live SSE tail can de-duplicate against replay.
    seq: Mapped[int] = mapped_column(Integer, nullable=False)
    timestamp: Mapped[datetime] = mapped_column(UtcTimestamp, nullable=False)
    level: Mapped[str] = mapped_column(String(16), nullable=False)
    message: Mapped[str] = mapped_column(Text, nullable=False)
    torrent_hash: Mapped[str | None] = mapped_column(String(40))
    rule_id: Mapped[int | None] = mapped_column(Integer)


class ScriptRunMarker(Base):
    """Durable "already ran" marker for ``script.run``.

    Scripts are the one action that is not idempotent by nature, so the marker is
    stored in the database as well as on disk -- a wiped run directory should not
    re-trigger every script for every torrent.
    """

    __tablename__ = "script_run_markers"
    __table_args__ = (UniqueConstraint("rule_id", "torrent_hash", name="uq_script_marker"),)

    id: Mapped[int] = mapped_column(primary_key=True)
    rule_id: Mapped[int] = mapped_column(Integer, nullable=False)
    torrent_hash: Mapped[str] = mapped_column(String(40), nullable=False)
    run_dir: Mapped[str] = mapped_column(String(512), nullable=False)
    created_at: Mapped[datetime] = mapped_column(UtcTimestamp, default=utcnow, nullable=False)
