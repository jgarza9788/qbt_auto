"""Action handlers: what a rule does to the torrents it matched.

Three design decisions carry most of the weight here.

**Outcomes are five-valued.** ``SKIPPED`` (already in the desired state) and
``NOT_APPLICABLE`` (the rule did not match) are different facts, and collapsing
them makes a run summary meaningless -- you could no longer tell "nothing needed
doing" from "nothing matched".

**Match is tri-state.** ``True`` fires the action, ``False`` fires the *reverse*
for bidirectional handlers such as ``tag.sync``, and ``None`` means the condition
could not be evaluated, so nothing happens at all.

**Handlers return intents, not calls.** A handler decides whether a torrent needs
an operation; the runner groups the resulting intents and sends one batched
request per group. Applying a tag to 500 torrents is a handful of requests rather
than 500.
"""

from __future__ import annotations

import sqlite3
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from typing import Any, ClassVar

import structlog
from pydantic import BaseModel

from qbitflow.domain import ActionOutcome

log = structlog.get_logger(__name__)


class TorrentState:
    """Current state of one torrent, as the snapshot recorded it.

    Handlers compare against this to decide whether an action is needed at all.
    That check runs *before* the dry-run check, so a dry run reports SKIPPED for
    torrents that are already correct rather than falsely promising a change.
    """

    __slots__ = ("_row",)

    def __init__(self, row: sqlite3.Row) -> None:
        self._row = row

    def __getitem__(self, key: str) -> Any:
        return self._row[key]

    def get(self, key: str, default: Any = None) -> Any:
        try:
            return self._row[key]
        except (IndexError, KeyError):
            return default

    @property
    def hash(self) -> str:
        return str(self._row["hash"])

    @property
    def source_id(self) -> int:
        return int(self._row["source_id"])

    @property
    def name(self) -> str:
        return str(self._row["name"])

    @property
    def category(self) -> str:
        return str(self._row["category"] or "")

    @property
    def tags(self) -> list[str]:
        """The comma-wrapped column unwrapped back into a list."""
        raw = str(self._row["tags"] or ",")
        return [t for t in raw.strip(",").split(",") if t]

    @property
    def save_path(self) -> str:
        return str(self._row["save_path"] or "")

    @property
    def upload_limit(self) -> int:
        return int(self._row["upload_limit"] or 0)

    @property
    def download_limit(self) -> int:
        return int(self._row["download_limit"] or 0)

    @property
    def force_start(self) -> bool:
        return bool(self._row["force_start"])

    @property
    def is_paused(self) -> bool:
        return bool(self._row["is_paused"])


@dataclass(frozen=True, slots=True)
class ActionIntent:
    """One qBittorrent operation, waiting to be batched with its peers.

    ``op`` and ``args`` together form the batch key: every torrent needing
    ``add_tags`` with the same tag goes out in one request.
    """

    op: str
    args: tuple[Any, ...]
    source_id: int
    torrent_hash: str

    @property
    def batch_key(self) -> tuple[int, str, tuple[Any, ...]]:
        return (self.source_id, self.op, self.args)


@dataclass(slots=True)
class ActionDecision:
    """What a handler concluded for one torrent.

    An ``intent`` means "this still needs doing"; the outcome becomes final only
    once the runner has flushed it. A decision without an intent is terminal.
    """

    outcome: ActionOutcome
    intent: ActionIntent | None = None
    message: str = ""


@dataclass(slots=True)
class ActionContext:
    rule_id: int | None
    rule_name: str
    #: True fired, False fired the reverse, None means evaluation failed.
    match: bool | None
    torrent: TorrentState
    #: Parameters with placeholders already substituted by the runner, so no
    #: handler has to think about templating.
    params: dict[str, Any]
    dry_run: bool
    #: Only set for the handlers that cannot be expressed as a batched intent.
    client: Any = None
    extras: dict[str, Any] = field(default_factory=dict)


class ActionHandler(ABC):
    """One action type. Adding one means adding a file and a decorator."""

    #: Registry key, in noun.verb form.
    type: ClassVar[str]
    display_name: ClassVar[str]
    description: ClassVar[str] = ""
    #: Params model. Drives the UI form, the API schema and server-side
    #: validation from a single declaration.
    params_model: ClassVar[type[BaseModel]]
    #: True when the handler also acts on torrents the rule did *not* match --
    #: how tag.sync removes a tag it previously added.
    bidirectional: ClassVar[bool] = False
    #: True when the handler performs I/O itself instead of returning an intent.
    direct: ClassVar[bool] = False
    #: Set for handlers gated behind an explicit opt-in.
    requires_opt_in: ClassVar[str | None] = None

    @abstractmethod
    def decide(self, ctx: ActionContext) -> ActionDecision:
        """Decide what this torrent needs. Must not perform I/O."""

    async def perform(self, ctx: ActionContext) -> ActionDecision:
        """Carry out a non-batchable action. Only called when ``direct`` is set."""
        raise NotImplementedError

    def inverse_predicate(self, params: dict[str, Any]) -> tuple[str, list[Any]] | None:
        """SQL identifying torrents that may need the action *undone*.

        Bidirectional handlers use this so the reverse pass is a targeted query
        rather than a walk over every torrent in the client. For ``tag.sync``,
        only torrents that already carry the tag can possibly need it removed.
        """
        return None

    def validate_params(self, raw: dict[str, Any]) -> BaseModel:
        return self.params_model.model_validate(raw or {})

    def schema(self) -> dict[str, Any]:
        return {
            "type": self.type,
            "display_name": self.display_name,
            "description": self.description,
            "bidirectional": self.bidirectional,
            "requires_opt_in": self.requires_opt_in,
            "params": self.params_model.model_json_schema(),
        }


_REGISTRY: dict[str, ActionHandler] = {}


def register(handler_cls: type[ActionHandler]) -> type[ActionHandler]:
    """Explicit registration.

    Deliberately not an import-time scan of the package: "why is my handler not
    loading" is a miserable question to answer when registration is implicit.
    """
    instance = handler_cls()
    if instance.type in _REGISTRY:
        raise RuntimeError(f"duplicate action type '{instance.type}'")
    _REGISTRY[instance.type] = instance
    return handler_cls


def get_handler(action_type: str) -> ActionHandler | None:
    return _REGISTRY.get(action_type)


def all_handlers() -> dict[str, ActionHandler]:
    return dict(_REGISTRY)


def action_schemas() -> list[dict[str, Any]]:
    """Everything the UI needs to render every action's form."""
    return [h.schema() for h in sorted(_REGISTRY.values(), key=lambda h: h.type)]
