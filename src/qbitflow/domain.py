"""Shared enums and small value types.

Every enum here is a ``StrEnum`` so it persists as readable text in SQLite and
survives reordering. Integer-backed enums make a database that can only be
interpreted by the code that wrote it.
"""

from __future__ import annotations

from dataclasses import dataclass
from enum import StrEnum


class SourceKind(StrEnum):
    QBITTORRENT = "qbittorrent"
    PLEX = "plex"
    JELLYFIN = "jellyfin"
    TAUTULLI = "tautulli"
    JELLYSTAT = "jellystat"
    JELLYGLANCE = "jellyglance"
    STORAGE = "storage"


#: Sources that can be the target of an action, as opposed to only supplying data.
ACTION_TARGET_KINDS = frozenset({SourceKind.QBITTORRENT})


class ActionOutcome(StrEnum):
    """What a handler actually did.

    The distinction that matters: ``SKIPPED`` means the torrent was already in the
    desired state, while ``NOT_APPLICABLE`` means the rule did not match. Collapsing
    them makes a run summary meaningless.
    """

    APPLIED = "applied"
    WOULD_APPLY = "would_apply"
    SKIPPED = "skipped"
    NOT_APPLICABLE = "not_applicable"
    ERROR = "error"


class RunStatus(StrEnum):
    RUNNING = "running"
    SUCCEEDED = "succeeded"
    FAILED = "failed"
    CANCELLED = "cancelled"


class RunTrigger(StrEnum):
    SCHEDULE = "schedule"
    MANUAL = "manual"
    API = "api"
    STARTUP = "startup"


class FieldType(StrEnum):
    NUMBER = "number"
    STRING = "string"
    BOOL = "bool"
    DATETIME = "datetime"
    DURATION = "duration"
    LIST = "list"


class ConditionLogic(StrEnum):
    AND = "and"
    OR = "or"


class ConditionMode(StrEnum):
    BUILDER = "builder"
    RAW_SQL = "raw_sql"


class ConditionOperator(StrEnum):
    EQ = "eq"
    NEQ = "neq"
    GT = "gt"
    GTE = "gte"
    LT = "lt"
    LTE = "lte"
    CONTAINS = "contains"
    NOT_CONTAINS = "not_contains"
    STARTS_WITH = "starts_with"
    ENDS_WITH = "ends_with"
    MATCHES = "matches"
    NOT_MATCHES = "not_matches"
    IN_LIST = "in_list"
    NOT_IN_LIST = "not_in_list"
    IS_TRUE = "is_true"
    IS_FALSE = "is_false"
    IS_NULL = "is_null"
    IS_NOT_NULL = "is_not_null"
    OLDER_THAN_DAYS = "older_than_days"
    NEWER_THAN_DAYS = "newer_than_days"


#: Operators that take no value at all -- the UI hides the value box for these.
VALUELESS_OPERATORS = frozenset(
    {
        ConditionOperator.IS_TRUE,
        ConditionOperator.IS_FALSE,
        ConditionOperator.IS_NULL,
        ConditionOperator.IS_NOT_NULL,
    }
)


class ParallelismTier(StrEnum):
    LOW = "low"
    MEDIUM = "medium"
    HIGH = "high"
    VERY_HIGH = "very_high"


#: Documented in the README and shown in the Settings UI. A Raspberry Pi should
#: stay on LOW; VERY_HIGH is for a real server with a fast qBittorrent instance.
PARALLELISM_WORKERS: dict[ParallelismTier, int] = {
    ParallelismTier.LOW: 2,
    ParallelismTier.MEDIUM: 4,
    ParallelismTier.HIGH: 8,
    ParallelismTier.VERY_HIGH: 16,
}


@dataclass(frozen=True, slots=True)
class HealthResult:
    """Outcome of a connection test. Latency is captured on failure too --
    a slow failure and an instant refusal are different problems."""

    ok: bool
    latency_ms: int
    error: str | None = None

    @classmethod
    def healthy(cls, latency_ms: int) -> HealthResult:
        return cls(ok=True, latency_ms=latency_ms)

    @classmethod
    def unhealthy(cls, error: str, latency_ms: int) -> HealthResult:
        return cls(ok=False, latency_ms=latency_ms, error=error)
