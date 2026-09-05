"""The stored condition tree.

This is what the visual builder edits and what gets persisted. It is never
executed directly -- :mod:`qbitflow.conditions.compiler` turns it into
parameterized SQL. Keeping the tree as data rather than as a string is what makes
the builder, the SQL preview and the stored rule three views of one thing instead
of three things that can disagree.
"""

from __future__ import annotations

from typing import Any

from pydantic import BaseModel, ConfigDict, Field

from qbitflow.domain import ConditionLogic, ConditionOperator

#: Guard against a tree deep enough to blow the compiler's recursion or produce
#: SQL SQLite refuses to parse.
MAX_DEPTH = 10


class Condition(BaseModel):
    model_config = ConfigDict(extra="forbid")

    field: str
    operator: ConditionOperator
    #: A scalar, or a list for the IN operators. Ignored by the valueless operators.
    value: Any = None
    #: The argument for fields that take one, e.g. which storage path to read.
    qualifier: str | None = None


class Group(BaseModel):
    model_config = ConfigDict(extra="forbid")

    logic: ConditionLogic = ConditionLogic.AND
    #: Wraps the whole group in NOT. This is how the builder offers NOT without
    #: a separate node type.
    negate: bool = False
    conditions: list[Condition] = Field(default_factory=list)
    groups: list[Group] = Field(default_factory=list)

    @property
    def is_empty(self) -> bool:
        return not self.conditions and all(g.is_empty for g in self.groups)

    def depth(self) -> int:
        return 1 + max((g.depth() for g in self.groups), default=0)

    def walk_fields(self) -> set[str]:
        """Every field key the tree references.

        Used to warn about a rule pointing at a field that no longer exists, and
        to work out which datasets a rule actually needs.
        """
        keys = {c.field for c in self.conditions}
        for group in self.groups:
            keys |= group.walk_fields()
        return keys


Group.model_rebuild()


def parse_tree(data: dict[str, Any] | None) -> Group:
    """A missing or empty tree matches everything, the same as ``WHERE 1``."""
    if not data:
        return Group()
    return Group.model_validate(data)
