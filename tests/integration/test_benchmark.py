"""Scale check: 10,000 torrents evaluated by 50 rules.

The whole design rests on one claim -- that conditions compile to set-based SQL
and are therefore effectively free compared with a per-torrent Python loop. This
suite is what keeps that claim honest. If someone later reintroduces a row-by-row
UDF into the hot path, or drops an index from the snapshot schema, the budget
below is what tells them.

The budgets are deliberately loose -- they must pass on a Raspberry Pi and on a
loaded CI runner -- but they are orders of magnitude tighter than a Python loop
over the same data would manage, so a regression to row-by-row evaluation cannot
slip through them.
"""

from __future__ import annotations

import asyncio
import time

import pytest

from qbitflow.conditions.compiler import compile_tree
from qbitflow.conditions.execute import ConditionSpec, evaluate
from qbitflow.conditions.registry import get_registry
from qbitflow.conditions.tree import Condition, Group, parse_tree
from qbitflow.domain import ConditionOperator, FieldType
from qbitflow.snapshot.builder import SnapshotBuilder
from tests.fixtures import payloads as fx

TORRENT_COUNT = 10_000
MEDIA_COUNT = 2_000
RULE_COUNT = 50

#: Every rule in one cycle, evaluated against one snapshot.
EVALUATION_BUDGET_SECONDS = 1.0
#: A single rule, which is what the editor's "test against current torrents"
#: preview runs on every debounce.
SINGLE_RULE_BUDGET_SECONDS = 0.1
#: Building the snapshot is per-cycle rather than per-rule, and it does the
#: media matching for all 10,000 torrents, so it gets a much larger allowance.
BUILD_BUDGET_SECONDS = 60.0

CATEGORIES = ["Movies", "TV", "Bulk", "Anime", ""]
QUALITIES = ["1080p.BluRay.x264", "2160p.WEB-DL.x265.HDR", "720p.HDTV.x264"]
TAG_POOL = ["seeding", "unwatched-90d", "keep", "disk-pressure", "archive"]


def _torrents() -> list:
    out = []
    for index in range(TORRENT_COUNT):
        year = 1990 + (index % 35)
        quality = QUALITIES[index % len(QUALITIES)]
        category = CATEGORIES[index % len(CATEGORIES)]
        out.append(
            fx.torrent(
                f"Some.Title.{index % MEDIA_COUNT}.{year}.{quality}-GROUP",
                hash=f"{index:040x}",
                category=category,
                tags=TAG_POOL[: index % (len(TAG_POOL) + 1)],
                save_path=f"/downloads/{category or 'misc'}",
                size=(index % 40 + 1) * 1024**3,
                ratio=(index % 50) / 10.0,
                progress=1.0 if index % 7 else 0.4,
                state="uploading" if index % 3 else "pausedUP",
                added_days_ago=index % 900,
                completed_days_ago=index % 900,
                activity_days_ago=index % 60,
            )
        )
    return out


def _media() -> list:
    return [
        fx.media_item(
            f"Some Title {index}",
            media_type="movie" if index % 2 else "episode",
            year=1990 + (index % 35),
            files=[
                f"/data/library/Some.Title.{index}.{1990 + index % 35}"
                ".1080p.BluRay.x264-GROUP.mkv"
            ],
        )
        for index in range(MEDIA_COUNT)
    ]


def _watches() -> list:
    return [
        fx.watch(
            f"Some Title {index}",
            media_type="movie" if index % 2 else "episode",
            play_count=index % 9,
            last_played_days_ago=(index % 400) if index % 5 else None,
        )
        for index in range(MEDIA_COUNT)
    ]


def _rule_trees() -> list[dict]:
    """Fifty rules spanning every join and every operator family.

    Roughly a third touch the media join, a third the watch aggregate and a
    third the torrent table alone, which is the shape a real installation ends
    up with. Every one also carries a nested OR group, because a rule set of
    fifty flat single-condition rules would not exercise the compiler.
    """
    trees: list[dict] = []
    for index in range(RULE_COUNT):
        bucket = index % 10
        if bucket == 0:
            conditions = [
                {"field": "torrent.ratio", "operator": "gte", "value": 1.0 + index / 10},
                {"field": "torrent.is_complete", "operator": "is_true"},
            ]
        elif bucket == 1:
            conditions = [
                {"field": "torrent.size_gb", "operator": "gt", "value": index % 30},
                {"field": "torrent.category", "operator": "eq", "value": CATEGORIES[index % 5]},
            ]
        elif bucket == 2:
            conditions = [
                {"field": "torrent.name", "operator": "contains", "value": "2160p"},
                {"field": "torrent.days_since_added", "operator": "gt", "value": 30},
            ]
        elif bucket == 3:
            conditions = [
                {"field": "media.media_type", "operator": "eq", "value": "movie"},
                {"field": "torrent.match_confidence", "operator": "gte", "value": 0.7},
            ]
        elif bucket == 4:
            conditions = [
                {"field": "watch.min_days_since_played", "operator": "gt", "value": 90},
                {"field": "torrent.is_complete", "operator": "is_true"},
            ]
        elif bucket == 5:
            conditions = [
                {"field": "torrent.tags", "operator": "contains", "value": "seeding"},
                {"field": "torrent.seeding_time_days", "operator": "gte", "value": 1},
            ]
        elif bucket == 6:
            conditions = [
                {"field": "media.year", "operator": "lt", "value": 2000 + index % 20},
                {"field": "media.is_matched", "operator": "is_true"},
            ]
        elif bucket == 7:
            conditions = [
                {"field": "torrent.save_path", "operator": "starts_with", "value": "/downloads"},
                {"field": "torrent.num_seeds", "operator": "lte", "value": 5},
            ]
        elif bucket == 8:
            conditions = [
                {"field": "watch.plays_any_source", "operator": "eq", "value": 0},
                {"field": "torrent.days_since_added", "operator": "gt", "value": 180},
            ]
        else:
            conditions = [
                {
                    "field": "torrent.state",
                    "operator": "in_list",
                    "value": ["uploading", "seeding"],
                },
                {"field": "torrent.ratio", "operator": "lt", "value": 10.0},
            ]

        trees.append(
            {
                "logic": "and" if index % 2 else "or",
                "negate": False,
                "conditions": conditions,
                "groups": [
                    {
                        "logic": "or",
                        "negate": False,
                        "conditions": [
                            {"field": "torrent.progress", "operator": "gte", "value": 0.99},
                            {"field": "torrent.is_paused", "operator": "is_false"},
                        ],
                        "groups": [],
                    }
                ],
            }
        )
    return trees


def _specs() -> list[ConditionSpec]:
    return [ConditionSpec(tree=parse_tree(tree)) for tree in _rule_trees()]


@pytest.fixture(scope="module")
def big_snapshot():
    payloads = [
        fx.qbt_payload(_torrents()),
        fx.media_payload(_media(), _watches()),
        fx.storage_payload([fx.storage("downloads"), fx.storage("cold", used_percent=91.0)]),
    ]

    started = time.perf_counter()
    snapshot = asyncio.run(SnapshotBuilder().build(payloads, now=fx.NOW))
    elapsed = time.perf_counter() - started

    assert snapshot.stats.torrents == TORRENT_COUNT
    assert elapsed < BUILD_BUDGET_SECONDS, (
        f"building a {TORRENT_COUNT}-torrent snapshot took {elapsed:.2f}s, "
        f"budget {BUILD_BUDGET_SECONDS}s"
    )
    yield snapshot
    snapshot.close()


def test_fifty_rules_over_ten_thousand_torrents(big_snapshot) -> None:
    specs = _specs()

    started = time.perf_counter()
    results = [evaluate(big_snapshot, spec) for spec in specs]
    elapsed = time.perf_counter() - started

    failures = [result.error for result in results if not result.ok]
    assert not failures, f"rules failed to evaluate: {failures[:3]}"
    # A benchmark that matches nothing proves nothing about the query plan.
    assert sum(len(result.matched) for result in results) > 0
    assert not any(result.truncated for result in results)

    assert elapsed < EVALUATION_BUDGET_SECONDS, (
        f"{RULE_COUNT} rules over {TORRENT_COUNT} torrents took {elapsed:.3f}s, "
        f"budget {EVALUATION_BUDGET_SECONDS}s"
    )


def test_a_single_rule_is_fast_enough_for_the_live_preview(big_snapshot) -> None:
    # Bucket 4: the watch aggregate joined to the media match -- the most
    # expensive shape the builder can produce.
    spec = _specs()[4]

    evaluate(big_snapshot, spec)  # warm the cache, as a real editor session would
    started = time.perf_counter()
    result = evaluate(big_snapshot, spec)
    elapsed = time.perf_counter() - started

    assert result.ok
    assert elapsed < SINGLE_RULE_BUDGET_SECONDS, (
        f"one rule took {elapsed:.3f}s, budget {SINGLE_RULE_BUDGET_SECONDS}s"
    )


def _probe_value(field_type: FieldType) -> object:
    if field_type is FieldType.NUMBER:
        return 1
    if field_type is FieldType.LIST:
        return "seeding"
    return "sample"


def test_every_advertised_field_still_answers_at_full_scale(big_snapshot) -> None:
    """The field panel must not advertise a field that falls over on real data.

    The unit suite proves each field *compiles*; this proves each one also
    *executes* against a full-size snapshot inside the per-cycle budget. A field
    backed by an unindexed correlated subquery passes the former and fails here.
    """
    registry = get_registry()
    slowest: tuple[str, float] = ("", 0.0)

    started = time.perf_counter()
    for key, definition in registry.fields.items():
        operator = registry.operators_for(definition.type)[0]
        # is_true / is_false take no operand; every other first-listed operator does.
        needs_value = operator not in {
            ConditionOperator.IS_TRUE,
            ConditionOperator.IS_FALSE,
            ConditionOperator.IS_NULL,
            ConditionOperator.IS_NOT_NULL,
        }
        compiled = compile_tree(
            Group(
                conditions=[
                    Condition(
                        field=key,
                        operator=operator,
                        value=_probe_value(definition.type) if needs_value else None,
                        qualifier="downloads" if definition.takes_qualifier else None,
                    )
                ]
            )
        )
        field_started = time.perf_counter()
        big_snapshot.query(compiled.sql, compiled.params)
        field_elapsed = time.perf_counter() - field_started
        if field_elapsed > slowest[1]:
            slowest = (key, field_elapsed)
    elapsed = time.perf_counter() - started

    assert elapsed < len(registry.fields) * SINGLE_RULE_BUDGET_SECONDS, (
        f"querying all {len(registry.fields)} fields took {elapsed:.3f}s; "
        f"slowest was {slowest[0]} at {slowest[1]:.3f}s"
    )
