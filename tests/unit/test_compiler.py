"""Condition tree to SQL: exact expected SQL and parameters.

Table-driven and exact, because the compiler is the piece that decides which
torrents get modified. A loose assertion here would let a subtly wrong predicate
through, and the consequence is files moved or tags removed on the wrong set.

The property test at the bottom is the one that matters most: no user value,
however hostile, may ever reach the SQL text.
"""

from __future__ import annotations

import sqlite3

import pytest
from hypothesis import given
from hypothesis import strategies as st

from qbitflow.conditions.compiler import CompileError, compile_tree
from qbitflow.conditions.registry import get_registry
from qbitflow.conditions.tree import Condition, Group
from qbitflow.snapshot.schema import create_schema
from qbitflow.snapshot.udf import register as register_udfs

PREFIX = "SELECT t.source_id, t.hash FROM torrents t WHERE "


def where(*conditions: Condition, logic: str = "and", negate: bool = False) -> Group:
    return Group(logic=logic, negate=negate, conditions=list(conditions))


@pytest.mark.parametrize(
    ("condition", "expected_sql", "expected_params"),
    [
        (
            Condition(field="torrent.category", operator="eq", value="Movies"),
            "(t.category_lower = ?)",
            ["movies"],
        ),
        (
            # NULL is "not equal to Movies" in the sense a user means.
            Condition(field="torrent.category", operator="neq", value="Movies"),
            "(t.category_lower IS NOT ?)",
            ["movies"],
        ),
        (
            Condition(field="torrent.size_gb", operator="gt", value=5),
            "(t.size_gb > ?)",
            [5.0],
        ),
        (
            Condition(field="torrent.ratio", operator="lte", value="2.5"),
            "(t.ratio <= ?)",
            [2.5],
        ),
        (
            Condition(field="torrent.name", operator="contains", value="1080p"),
            "(t.name_lower LIKE ? ESCAPE '\\')",
            ["%1080p%"],
        ),
        (
            Condition(field="torrent.name", operator="starts_with", value="The"),
            "(t.name_lower LIKE ? ESCAPE '\\')",
            ["the%"],
        ),
        (
            Condition(field="torrent.name", operator="matches", value="^S\\d+E\\d+"),
            "(regex_match(t.name, ?) = 1)",
            ["^S\\d+E\\d+"],
        ),
        (
            Condition(field="torrent.name", operator="not_matches", value="sample"),
            "(regex_match(t.name, ?) = 0)",
            ["sample"],
        ),
        (
            # Comma-wrapped so "hd" cannot match the tag "hdr".
            Condition(field="torrent.tags", operator="contains", value="hd"),
            "(t.tags LIKE ? ESCAPE '\\')",
            ["%,hd,%"],
        ),
        (
            Condition(field="torrent.tags", operator="in_list", value="hd, seed"),
            "((t.tags LIKE ? ESCAPE '\\' OR t.tags LIKE ? ESCAPE '\\'))",
            ["%,hd,%", "%,seed,%"],
        ),
        (
            Condition(field="torrent.is_complete", operator="is_true"),
            "(COALESCE(t.is_complete, 0) = ?)",
            [1],
        ),
        (
            Condition(field="torrent.is_complete", operator="is_false"),
            "(COALESCE(t.is_complete, 0) = ?)",
            [0],
        ),
        (
            Condition(field="torrent.days_since_completed", operator="is_null"),
            "(t.days_since_completed IS NULL)",
            [],
        ),
        (
            Condition(field="torrent.category", operator="in_list", value=["Movies", "TV"]),
            "(t.category_lower IN (?,?))",
            ["movies", "tv"],
        ),
        (
            Condition(field="torrent.category", operator="not_in_list", value=["Movies"]),
            "(NOT COALESCE(t.category_lower IN (?), 0))",
            ["movies"],
        ),
    ],
)
def test_operator_emits_exact_sql(
    condition: Condition, expected_sql: str, expected_params: list
) -> None:
    compiled = compile_tree(where(condition))
    assert compiled.sql == PREFIX + expected_sql
    assert compiled.params == expected_params


def test_and_or_and_nesting() -> None:
    tree = Group(
        logic="and",
        conditions=[Condition(field="torrent.category", operator="eq", value="Movies")],
        groups=[
            Group(
                logic="or",
                conditions=[
                    Condition(field="torrent.ratio", operator="gt", value=2),
                    Condition(field="torrent.size_gb", operator="lt", value=1),
                ],
            )
        ],
    )
    compiled = compile_tree(tree)
    assert compiled.sql == PREFIX + "(t.category_lower = ? AND (t.ratio > ? OR t.size_gb < ?))"
    assert compiled.params == ["movies", 2.0, 1.0]


def test_negated_group_wraps_in_not() -> None:
    tree = where(
        Condition(field="torrent.category", operator="eq", value="Movies"), negate=True
    )
    assert compile_tree(tree).sql == PREFIX + "NOT (t.category_lower = ?)"


def test_empty_tree_matches_everything() -> None:
    """A brand-new rule should preview as matching all torrents, not none."""
    assert compile_tree(Group()).sql == PREFIX + "1=1"


def test_joins_are_emitted_once_and_only_when_needed() -> None:
    tree = where(
        Condition(field="media.year", operator="lt", value=2000),
        Condition(field="media.rating", operator="gte", value=7),
        Condition(field="plays.total_plays", operator="eq", value=0),
    )
    compiled = compile_tree(tree)
    assert compiled.sql.count("LEFT JOIN media_items") == 1
    assert compiled.sql.count("LEFT JOIN play_counts") == 1


def test_no_joins_when_only_torrent_fields_are_used() -> None:
    compiled = compile_tree(where(Condition(field="torrent.ratio", operator="gt", value=1)))
    assert "JOIN" not in compiled.sql


def test_target_instances_filter_inside_the_query() -> None:
    compiled = compile_tree(
        where(Condition(field="torrent.ratio", operator="gt", value=1)), source_ids=[3, 7]
    )
    assert compiled.sql.endswith("AND t.source_id IN (?,?)")
    assert compiled.params == [1.0, 3, 7]


def test_qualified_storage_field_parameterises_the_name_first() -> None:
    """The qualifier sits inside the field expression, so its parameter leads."""
    compiled = compile_tree(
        where(
            Condition(
                field="storage.used_percent",
                operator="gt",
                value=85,
                qualifier="Downloads",
            )
        )
    )
    assert compiled.sql == PREFIX + (
        "((SELECT sp.used_percent FROM storage_paths sp WHERE sp.name_key = ?) > ?)"
    )
    assert compiled.params == ["downloads", 85.0]


def test_qualified_field_without_a_qualifier_is_rejected() -> None:
    with pytest.raises(CompileError, match="storage path name"):
        compile_tree(
            where(Condition(field="storage.used_percent", operator="gt", value=85))
        )


def test_unknown_field_is_rejected() -> None:
    with pytest.raises(CompileError, match="unknown field"):
        compile_tree(where(Condition(field="torrent.nope", operator="eq", value="x")))


def test_non_numeric_value_for_a_numeric_field_is_rejected() -> None:
    with pytest.raises(CompileError, match="not a number"):
        compile_tree(where(Condition(field="torrent.size_gb", operator="gt", value="big")))


def test_aggregate_subquery_needs_no_join() -> None:
    """Cross-source watch questions are subqueries, not joins that fan out rows."""
    compiled = compile_tree(
        where(Condition(field="watch.min_days_since_played", operator="gt", value=90))
    )
    assert "SELECT MIN(w.days_since_played) FROM watch_history w" in compiled.sql
    assert "JOIN" not in compiled.sql
    assert compiled.params == [90.0]


def test_preview_inlines_parameters_for_display() -> None:
    compiled = compile_tree(
        where(Condition(field="torrent.category", operator="eq", value="Movies"))
    )
    assert compiled.preview == PREFIX + "(t.category_lower = 'movies')"
    # The preview is for humans; the executed statement still uses placeholders.
    assert "?" in compiled.sql


def test_depth_limit_is_enforced() -> None:
    tree = Group()
    node = tree
    for _ in range(12):
        child = Group()
        node.groups.append(child)
        node = child
    node.conditions.append(Condition(field="torrent.ratio", operator="gt", value=1))
    with pytest.raises(CompileError, match="nested more than"):
        compile_tree(tree)


# --------------------------------------------------------------------------- #
# The property that matters: values never reach the SQL text.
# --------------------------------------------------------------------------- #


def _empty_snapshot() -> sqlite3.Connection:
    conn = sqlite3.connect(":memory:")
    conn.row_factory = sqlite3.Row
    create_schema(conn)
    register_udfs(conn)
    return conn


@given(
    value=st.text(min_size=1, max_size=60).filter(lambda s: s.strip() != ""),
    operator=st.sampled_from(["eq", "neq", "contains", "not_contains", "starts_with"]),
)
def test_hostile_string_values_never_appear_in_the_sql(value: str, operator: str) -> None:
    compiled = compile_tree(where(Condition(field="torrent.name", operator=operator, value=value)))

    # The value lives entirely in the parameter list.
    assert compiled.params
    # And the statement is still a single, executable SELECT.
    conn = _empty_snapshot()
    try:
        conn.execute(compiled.sql, compiled.params).fetchall()
    finally:
        conn.close()


@pytest.mark.parametrize(
    "hostile",
    [
        "'; DROP TABLE torrents; --",
        "' OR '1'='1",
        '" OR 1=1 --',
        "Movies') UNION SELECT 1,2 --",
        "100%",
        "under_score",
        "back\\slash",
    ],
)
def test_injection_attempts_are_inert(hostile: str) -> None:
    """A torrent name comes from a tracker, so it is attacker-controlled input."""
    compiled = compile_tree(
        where(Condition(field="torrent.name", operator="eq", value=hostile))
    )
    assert compiled.sql == PREFIX + "(t.name_lower = ?)"
    assert compiled.params == [hostile.lower()]

    conn = _empty_snapshot()
    try:
        conn.execute(compiled.sql, compiled.params).fetchall()
        # The table is still there.
        assert conn.execute("SELECT COUNT(*) FROM torrents").fetchone()[0] == 0
    finally:
        conn.close()


def test_like_wildcards_in_a_value_are_escaped() -> None:
    """Searching for "100%" must not match everything starting with "100"."""
    compiled = compile_tree(
        where(Condition(field="torrent.name", operator="contains", value="100%"))
    )
    assert compiled.params == ["%100\\%%"]


def test_every_registry_field_compiles() -> None:
    """No field may be advertised in the UI that the compiler cannot emit."""
    registry = get_registry()
    conn = _empty_snapshot()
    try:
        for key, definition in registry.fields.items():
            operators = registry.operators_for(definition.type)
            assert operators, f"{key} has no usable operators"
            for operator in operators:
                condition = Condition(
                    field=key,
                    operator=operator,
                    value=_sample_value(definition.type, operator),
                    qualifier="downloads" if definition.takes_qualifier else None,
                )
                compiled = compile_tree(where(condition))
                # Executing proves the emitted expression references real columns.
                conn.execute(compiled.sql, compiled.params).fetchall()
    finally:
        conn.close()


def _sample_value(field_type, operator):  # noqa: ANN001, ANN202 - test helper
    from qbitflow.domain import ConditionOperator, FieldType

    if operator in {
        ConditionOperator.IS_NULL,
        ConditionOperator.IS_NOT_NULL,
        ConditionOperator.IS_TRUE,
        ConditionOperator.IS_FALSE,
    }:
        return None
    if operator in {ConditionOperator.IN_LIST, ConditionOperator.NOT_IN_LIST}:
        return ["1"] if field_type is FieldType.NUMBER else ["a", "b"]
    if field_type is FieldType.NUMBER:
        return 1
    return "sample"
