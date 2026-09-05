"""Compiles a condition tree into one parameterized SQL query.

The output is always of the form::

    SELECT t.source_id, t.hash FROM torrents t [joins] WHERE <predicate>

One statement returns the whole matched set. There is no per-torrent Python loop
and no expression re-parsed per row.

**Every user-supplied value becomes a ``?`` parameter.** Nothing a user typed, and
nothing that came from a tracker, is ever concatenated into the SQL text. Field
keys are looked up in the registry and only their registered expressions are
emitted, so a field key cannot smuggle SQL in either.
"""

from __future__ import annotations

from dataclasses import dataclass, field

from qbitflow.conditions.registry import JOINS, FieldDef, FieldRegistry, get_registry
from qbitflow.conditions.tree import MAX_DEPTH, Condition, Group
from qbitflow.domain import ConditionLogic, ConditionOperator, FieldType

SELECT_PREFIX = "SELECT t.source_id, t.hash FROM torrents t"

#: Backslash, so a literal % or _ in a user's value is not a wildcard.
LIKE_ESCAPE = "\\"


class CompileError(ValueError):
    """A condition that cannot be turned into SQL."""


@dataclass(slots=True)
class CompiledQuery:
    sql: str
    params: list[object]
    #: Field keys the tree referenced, for "which datasets does this rule need".
    fields: set[str] = field(default_factory=set)
    #: Non-fatal problems worth showing in the editor.
    warnings: list[str] = field(default_factory=list)

    @property
    def preview(self) -> str:
        """The SQL with parameters inlined, for display only. Never executed."""
        parts = self.sql.split("?")
        out = [parts[0]]
        for value, tail in zip(self.params, parts[1:], strict=False):
            out.append(_render_literal(value))
            out.append(tail)
        return "".join(out)


def _render_literal(value: object) -> str:
    if value is None:
        return "NULL"
    if isinstance(value, bool):
        return "1" if value else "0"
    if isinstance(value, int | float):
        return str(value)
    return "'" + str(value).replace("'", "''") + "'"


def _escape_like(text: str) -> str:
    return (
        text.replace(LIKE_ESCAPE, LIKE_ESCAPE * 2)
        .replace("%", LIKE_ESCAPE + "%")
        .replace("_", LIKE_ESCAPE + "_")
    )


def _as_number(value: object, field_key: str) -> float:
    try:
        return float(value)  # type: ignore[arg-type]
    except (TypeError, ValueError) as exc:
        raise CompileError(f"{field_key}: {value!r} is not a number") from exc


def _as_list(value: object, field_key: str) -> list[object]:
    if isinstance(value, list | tuple | set):
        items = list(value)
    elif isinstance(value, str):
        items = [v.strip() for v in value.split(",")]
    else:
        items = [value]
    items = [i for i in items if i not in (None, "")]
    if not items:
        raise CompileError(f"{field_key}: needs at least one value")
    return items


class ConditionCompiler:
    def __init__(self, registry: FieldRegistry | None = None) -> None:
        self.registry = registry or get_registry()

    def compile(self, tree: Group, *, source_ids: list[int] | None = None) -> CompiledQuery:
        if tree.depth() > MAX_DEPTH:
            raise CompileError(f"condition is nested more than {MAX_DEPTH} levels deep")

        params: list[object] = []
        joins: set[str] = set()
        warnings: list[str] = []

        predicate = self._group(tree, params, joins, warnings)

        where = [predicate]
        if source_ids:
            # Instance targeting is part of the query rather than a post-filter,
            # so a rule aimed at one client never even considers the others.
            placeholders = ",".join("?" * len(source_ids))
            where.append(f"t.source_id IN ({placeholders})")
            params.extend(source_ids)

        join_sql = "".join(
            f" {JOINS[name]}" for name in JOINS if name in joins
        )
        sql = f"{SELECT_PREFIX}{join_sql} WHERE {' AND '.join(where)}"

        # An expression written twice emits two placeholders but is bound once,
        # which SQLite reports far from the cause. Catch it at the source.
        if sql.count("?") != len(params):
            raise CompileError(
                f"internal: emitted {sql.count('?')} placeholders for {len(params)} "
                "parameters; a field expression was repeated"
            )

        return CompiledQuery(
            sql=sql, params=params, fields=tree.walk_fields(), warnings=warnings
        )

    # -- tree walking -------------------------------------------------------

    def _group(
        self,
        group: Group,
        params: list[object],
        joins: set[str],
        warnings: list[str],
    ) -> str:
        parts: list[str] = []
        for condition in group.conditions:
            parts.append(self._condition(condition, params, joins))
        for child in group.groups:
            if child.is_empty:
                continue
            parts.append(self._group(child, params, joins, warnings))

        if not parts:
            # An empty group matches everything, which is what a brand-new rule
            # in the editor should preview as.
            return "1=1"

        joiner = " AND " if group.logic is ConditionLogic.AND else " OR "
        predicate = "(" + joiner.join(parts) + ")"
        return f"NOT {predicate}" if group.negate else predicate

    def _condition(
        self, condition: Condition, params: list[object], joins: set[str]
    ) -> str:
        definition = self.registry.get(condition.field)
        if definition is None:
            raise CompileError(f"unknown field '{condition.field}'")
        joins.update(definition.joins)

        expr = definition.comparison_expr
        if definition.takes_qualifier:
            if not condition.qualifier:
                raise CompileError(
                    f"{definition.key} needs a {definition.qualifier_label.lower()}"
                )
            # The qualifier sits inside the field's expression, so its parameter
            # must be appended before any value parameter.
            params.append(str(condition.qualifier).strip().lower())

        return self._operator(definition, expr, condition, params)

    def _operator(  # noqa: C901 - a flat dispatch over operators reads better than a table
        self,
        definition: FieldDef,
        expr: str,
        condition: Condition,
        params: list[object],
    ) -> str:
        op = condition.operator
        key = definition.key
        is_string = definition.type is FieldType.STRING
        is_list = definition.type is FieldType.LIST

        match op:
            case ConditionOperator.IS_NULL:
                return f"{definition.expr} IS NULL"
            case ConditionOperator.IS_NOT_NULL:
                return f"{definition.expr} IS NOT NULL"
            case ConditionOperator.IS_TRUE:
                params.append(1)
                return f"COALESCE({definition.expr}, 0) = ?"
            case ConditionOperator.IS_FALSE:
                params.append(0)
                return f"COALESCE({definition.expr}, 0) = ?"

            case ConditionOperator.EQ | ConditionOperator.NEQ:
                value = self._scalar(definition, condition.value, key)
                params.append(value)
                if op is ConditionOperator.EQ:
                    return f"{expr} = ?"
                # SQLite's IS NOT treats NULL as unequal, which is what a user
                # means by "not Movies", and keeps the expression to one
                # occurrence -- a repeated expression would emit a second
                # placeholder for a qualified field's argument.
                return f"{expr} IS NOT ?"

            case (
                ConditionOperator.GT
                | ConditionOperator.GTE
                | ConditionOperator.LT
                | ConditionOperator.LTE
            ):
                params.append(_as_number(condition.value, key))
                symbol = {
                    ConditionOperator.GT: ">",
                    ConditionOperator.GTE: ">=",
                    ConditionOperator.LT: "<",
                    ConditionOperator.LTE: "<=",
                }[op]
                return f"{definition.expr} {symbol} ?"

            case ConditionOperator.CONTAINS | ConditionOperator.NOT_CONTAINS:
                pattern = self._contains_pattern(definition, condition.value, key)
                params.append(pattern)
                if op is ConditionOperator.CONTAINS:
                    return f"{expr} LIKE ? ESCAPE '{LIKE_ESCAPE}'"
                # COALESCE rather than an OR of two expressions: NULL does not
                # contain the value, and the expression stays written once.
                return f"NOT COALESCE({expr} LIKE ? ESCAPE '{LIKE_ESCAPE}', 0)"

            case ConditionOperator.STARTS_WITH | ConditionOperator.ENDS_WITH:
                text = _escape_like(str(condition.value or "").lower())
                params.append(f"{text}%" if op is ConditionOperator.STARTS_WITH else f"%{text}")
                return f"{expr} LIKE ? ESCAPE '{LIKE_ESCAPE}'"

            case ConditionOperator.MATCHES | ConditionOperator.NOT_MATCHES:
                params.append(str(condition.value or ""))
                wanted = 1 if op is ConditionOperator.MATCHES else 0
                return f"regex_match({definition.expr}, ?) = {wanted}"

            case ConditionOperator.IN_LIST | ConditionOperator.NOT_IN_LIST:
                items = _as_list(condition.value, key)
                if is_list:
                    # Membership in a comma-wrapped column: any token matching.
                    clauses = []
                    for item in items:
                        params.append(f"%,{_escape_like(str(item).strip().lower())},%")
                        clauses.append(f"{expr} LIKE ? ESCAPE '{LIKE_ESCAPE}'")
                    joined = " OR ".join(clauses)
                    return f"({joined})" if op is ConditionOperator.IN_LIST else f"NOT ({joined})"

                for item in items:
                    params.append(
                        str(item).lower() if is_string else _as_number(item, key)
                    )
                placeholders = ",".join("?" * len(items))
                if op is ConditionOperator.IN_LIST:
                    return f"{expr} IN ({placeholders})"
                return f"NOT COALESCE({expr} IN ({placeholders}), 0)"

            case ConditionOperator.OLDER_THAN_DAYS | ConditionOperator.NEWER_THAN_DAYS:
                params.append(_as_number(condition.value, key))
                symbol = ">" if op is ConditionOperator.OLDER_THAN_DAYS else "<="
                # Works whether the field already holds days or holds an epoch.
                source = (
                    definition.expr
                    if definition.type is FieldType.NUMBER
                    else f"days_since({definition.expr})"
                )
                return f"{source} {symbol} ?"

        raise CompileError(f"{key}: operator '{op}' is not supported")

    def _scalar(self, definition: FieldDef, value: object, key: str) -> object:
        if definition.type is FieldType.NUMBER:
            return _as_number(value, key)
        if definition.type is FieldType.BOOL:
            return 1 if value in (True, 1, "1", "true", "True", "yes", "on") else 0
        # String comparison is case-insensitive, against a pre-lowered column
        # where one exists so the index still applies.
        return str(value if value is not None else "").lower()

    def _contains_pattern(self, definition: FieldDef, value: object, key: str) -> str:
        text = _escape_like(str(value if value is not None else "").strip().lower())
        if not text:
            raise CompileError(f"{key}: needs a value to search for")
        if definition.is_token_list:
            # The column is stored as ",a,b," so this cannot match a longer tag
            # that merely contains the one asked for.
            return f"%,{text},%"
        return f"%{text}%"


def compile_tree(
    tree: Group, *, source_ids: list[int] | None = None, registry: FieldRegistry | None = None
) -> CompiledQuery:
    return ConditionCompiler(registry).compile(tree, source_ids=source_ids)
