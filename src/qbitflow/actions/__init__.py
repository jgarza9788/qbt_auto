"""Action handlers.

Importing this package registers every handler; the registry is explicit rather
than an import-time scan, so a missing handler is a missing line rather than a
mystery.
"""

from qbitflow.actions import handlers  # noqa: F401 - imported for registration
from qbitflow.actions.base import (
    ActionContext,
    ActionDecision,
    ActionHandler,
    ActionIntent,
    TorrentState,
    action_schemas,
    all_handlers,
    get_handler,
    register,
)

__all__ = [
    "ActionContext",
    "ActionDecision",
    "ActionHandler",
    "ActionIntent",
    "TorrentState",
    "action_schemas",
    "all_handlers",
    "get_handler",
    "register",
]
