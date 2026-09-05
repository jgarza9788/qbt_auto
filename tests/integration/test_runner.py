"""End-to-end rule execution against a real database and a fake qBittorrent."""

from __future__ import annotations

import pytest

from qbitflow.db.base import session_scope
from qbitflow.db.models import Rule, RuleAction, RunHistory, RunRuleResult
from qbitflow.domain import RunStatus, RunTrigger
from qbitflow.engine.runner import RuleRunner
from qbitflow.settings import EngineSettings
from qbitflow.state import AppState
from tests.conftest import apply_client_state, load_torrents
from tests.fixtures import payloads as fx
from tests.fixtures.fakes import FakeQbtClient, FakeSourceCache


async def make_rule(
    state: AppState,
    *,
    name: str = "test rule",
    tree: dict | None = None,
    actions: list[tuple[str, dict]] | None = None,
    enabled: bool = True,
    order: int = 0,
    stop_on_match: bool | None = None,
    cooldown_seconds: int | None = None,
) -> int:
    async with session_scope(state.session_factory) as session:
        rule = Rule(
            name=name,
            enabled=enabled,
            order=order,
            condition_tree=tree,
            stop_on_match=stop_on_match,
            cooldown_seconds=cooldown_seconds,
            target_source_ids=[],
        )
        for index, (action_type, params) in enumerate(actions or []):
            rule.actions.append(RuleAction(order=index, type=action_type, params=params))
        session.add(rule)
        await session.flush()
        return rule.id


def tree(field: str, operator: str, value: object) -> dict:
    return {
        "logic": "and",
        "conditions": [{"field": field, "operator": operator, "value": value}],
        "groups": [],
    }


@pytest.fixture(autouse=True)
async def live_mode(state: AppState) -> None:
    """Dry-run is the shipped default; most of these tests want real actions."""
    await state.settings.save_engine_settings(EngineSettings(dry_run=False))


async def test_matched_torrents_get_tagged(
    runner: RuleRunner, state: AppState, source_cache: FakeSourceCache, qbt_client: FakeQbtClient
) -> None:
    payload = fx.qbt_payload(
        [
            fx.torrent("Big", size=20 * 1024**3),
            fx.torrent("Small", size=1 * 1024**3),
        ]
    )
    load_torrents(source_cache, qbt_client, payload)
    await make_rule(
        state,
        tree=tree("torrent.size_gb", "gt", 10),
        actions=[("tag.add", {"tag": "large"})],
    )

    result = await runner.run(trigger=RunTrigger.MANUAL)

    assert result.status is RunStatus.SUCCEEDED
    assert result.torrents_matched == 1
    assert result.actions_applied == 1
    call = qbt_client.calls_for("add_tags")[0]
    assert call.args == (("large",),)
    assert len(call.hashes) == 1


async def test_actions_are_batched_into_one_request(
    runner: RuleRunner, state: AppState, source_cache: FakeSourceCache, qbt_client: FakeQbtClient
) -> None:
    """Tagging many torrents must not become one request per torrent."""
    payload = fx.qbt_payload([fx.torrent(f"Torrent {i}", ratio=5.0) for i in range(50)])
    load_torrents(source_cache, qbt_client, payload)
    await make_rule(
        state,
        tree=tree("torrent.ratio", "gt", 1),
        actions=[("tag.add", {"tag": "seeded"})],
    )

    result = await runner.run()

    assert result.actions_applied == 50
    assert len(qbt_client.calls_for("add_tags")) == 1
    assert len(qbt_client.calls_for("add_tags")[0].hashes) == 50


async def test_batches_respect_the_configured_size(
    runner: RuleRunner, state: AppState, source_cache: FakeSourceCache, qbt_client: FakeQbtClient
) -> None:
    await state.settings.save_engine_settings(EngineSettings(dry_run=False, action_batch_size=10))
    payload = fx.qbt_payload([fx.torrent(f"T{i}", ratio=5.0) for i in range(25)])
    load_torrents(source_cache, qbt_client, payload)
    await make_rule(
        state, tree=tree("torrent.ratio", "gt", 1), actions=[("tag.add", {"tag": "x"})]
    )

    await runner.run()

    calls = qbt_client.calls_for("add_tags")
    assert [len(c.hashes) for c in calls] == [10, 10, 5]


async def test_a_second_run_changes_nothing(
    runner: RuleRunner, state: AppState, source_cache: FakeSourceCache, qbt_client: FakeQbtClient
) -> None:
    """Idempotency: the tag is already there, so nothing is sent."""
    payload = fx.qbt_payload([fx.torrent("Big", size=20 * 1024**3)])
    load_torrents(source_cache, qbt_client, payload)
    await make_rule(
        state,
        tree=tree("torrent.size_gb", "gt", 10),
        actions=[("tag.add", {"tag": "large"})],
    )

    first = await runner.run(force_refresh=True)
    assert first.actions_applied == 1

    # Next cycle re-reads qBittorrent, which now has the tag.
    source_cache.set_payload(1, apply_client_state(payload, qbt_client))
    second = await runner.run(force_refresh=True)

    assert second.actions_applied == 0
    assert second.actions_skipped == 1
    assert len(qbt_client.calls_for("add_tags")) == 1


async def test_dry_run_reports_would_apply_and_sends_nothing(
    runner: RuleRunner, state: AppState, source_cache: FakeSourceCache, qbt_client: FakeQbtClient
) -> None:
    payload = fx.qbt_payload([fx.torrent("Big", size=20 * 1024**3)])
    load_torrents(source_cache, qbt_client, payload)
    await make_rule(
        state,
        tree=tree("torrent.size_gb", "gt", 10),
        actions=[("tag.add", {"tag": "large"})],
    )

    result = await runner.run(dry_run_override=True)

    assert result.actions_would_apply == 1
    assert result.actions_applied == 0
    assert qbt_client.request_count == 0


async def test_dry_run_reports_skipped_for_already_correct_torrents(
    runner: RuleRunner, state: AppState, source_cache: FakeSourceCache, qbt_client: FakeQbtClient
) -> None:
    """The idempotency check runs before the dry-run check.

    Otherwise a dry run promises changes that a live run would not actually make.
    """
    payload = fx.qbt_payload([fx.torrent("Big", size=20 * 1024**3, tags=["large"])])
    load_torrents(source_cache, qbt_client, payload)
    await make_rule(
        state,
        tree=tree("torrent.size_gb", "gt", 10),
        actions=[("tag.add", {"tag": "large"})],
    )

    result = await runner.run(dry_run_override=True)

    assert result.actions_would_apply == 0
    assert result.actions_skipped == 1


async def test_tag_sync_removes_the_tag_when_a_torrent_stops_matching(
    runner: RuleRunner, state: AppState, source_cache: FakeSourceCache, qbt_client: FakeQbtClient
) -> None:
    """The bidirectional case: match is False, so the tag comes off."""
    payload = fx.qbt_payload(
        [
            fx.torrent("StillBig", size=20 * 1024**3, tags=["large"]),
            fx.torrent("NowSmall", size=1 * 1024**3, tags=["large"]),
            fx.torrent("Untouched", size=1 * 1024**3),
        ]
    )
    load_torrents(source_cache, qbt_client, payload)
    await make_rule(
        state,
        tree=tree("torrent.size_gb", "gt", 10),
        actions=[("tag.sync", {"tag": "large"})],
    )

    result = await runner.run()

    removals = qbt_client.calls_for("remove_tags")
    assert len(removals) == 1
    assert len(removals[0].hashes) == 1
    # The already-correct match and the never-tagged torrent are both untouched.
    assert result.actions_applied == 1
    assert not qbt_client.calls_for("add_tags")


async def test_tag_sync_only_examines_torrents_that_carry_the_tag(
    runner: RuleRunner, state: AppState, source_cache: FakeSourceCache, qbt_client: FakeQbtClient
) -> None:
    """The reverse pass is a targeted query, not a walk over every torrent."""
    payload = fx.qbt_payload(
        [fx.torrent(f"T{i}", size=1 * 1024**3) for i in range(200)]
        + [fx.torrent("Tagged", size=1 * 1024**3, tags=["large"])]
    )
    load_torrents(source_cache, qbt_client, payload)
    await make_rule(
        state,
        tree=tree("torrent.size_gb", "gt", 10),
        actions=[("tag.sync", {"tag": "large"})],
    )

    result = await runner.run()

    # One removal, and no busywork over the 200 torrents that never had the tag.
    assert result.actions_applied == 1
    assert result.actions_skipped == 0


async def test_category_set_also_enables_auto_management(
    runner: RuleRunner, state: AppState, source_cache: FakeSourceCache, qbt_client: FakeQbtClient
) -> None:
    payload = fx.qbt_payload([fx.torrent("Film", category="")])
    load_torrents(source_cache, qbt_client, payload)
    await make_rule(
        state,
        tree=tree("torrent.ratio", "gte", 0),
        actions=[("category.set", {"category": "Movies"})],
    )

    await runner.run()

    assert qbt_client.calls_for("set_category")[0].args == ("Movies",)
    # Without this, qBittorrent sets the label but leaves the files where they are.
    assert qbt_client.calls_for("set_auto_management")[0].args == (True,)


async def test_move_disables_auto_management_first(
    runner: RuleRunner,
    state: AppState,
    source_cache: FakeSourceCache,
    qbt_client: FakeQbtClient,
    tmp_path,
) -> None:
    destination = tmp_path / "cold"
    destination.mkdir()
    payload = fx.qbt_payload([fx.torrent("Film", save_path="/downloads")])
    load_torrents(source_cache, qbt_client, payload)
    await make_rule(
        state,
        tree=tree("torrent.ratio", "gte", 0),
        actions=[("torrent.move", {"path": str(destination)})],
    )

    await runner.run()

    ops = [c.op for c in qbt_client.calls]
    # Order matters: auto-management would move it straight back otherwise.
    assert ops.index("set_auto_management") < ops.index("set_location")
    assert qbt_client.calls_for("set_auto_management")[0].args == (False,)


async def test_move_skips_when_the_destination_is_missing(
    runner: RuleRunner, state: AppState, source_cache: FakeSourceCache, qbt_client: FakeQbtClient
) -> None:
    payload = fx.qbt_payload([fx.torrent("Film")])
    load_torrents(source_cache, qbt_client, payload)
    await make_rule(
        state,
        tree=tree("torrent.ratio", "gte", 0),
        actions=[("torrent.move", {"path": "/definitely/not/here"})],
    )

    result = await runner.run()

    assert result.actions_skipped == 1
    assert qbt_client.request_count == 0


async def test_placeholders_are_substituted_from_the_torrent(
    runner: RuleRunner,
    state: AppState,
    source_cache: FakeSourceCache,
    qbt_client: FakeQbtClient,
    tmp_path,
) -> None:
    (tmp_path / "Movies").mkdir()
    payload = fx.qbt_payload([fx.torrent("Film", category="Movies", save_path="/downloads")])
    load_torrents(source_cache, qbt_client, payload)
    await make_rule(
        state,
        tree=tree("torrent.ratio", "gte", 0),
        actions=[("torrent.move", {"path": str(tmp_path / "{category}")})],
    )

    await runner.run()

    assert qbt_client.calls_for("set_location")[0].args == (str(tmp_path / "Movies"),)


async def test_an_unknown_placeholder_is_an_error_not_an_empty_string(
    runner: RuleRunner, state: AppState, source_cache: FakeSourceCache, qbt_client: FakeQbtClient
) -> None:
    payload = fx.qbt_payload([fx.torrent("Film")])
    load_torrents(source_cache, qbt_client, payload)
    await make_rule(
        state,
        tree=tree("torrent.ratio", "gte", 0),
        actions=[("torrent.move", {"path": "/media/{categoy}"})],
    )

    result = await runner.run()

    assert result.error_count == 1
    assert qbt_client.request_count == 0


async def test_speed_limit_is_idempotent_per_direction(
    runner: RuleRunner, state: AppState, source_cache: FakeSourceCache, qbt_client: FakeQbtClient
) -> None:
    payload = fx.qbt_payload([fx.torrent("Film", upload_limit=1024 * 1024)])
    load_torrents(source_cache, qbt_client, payload)
    await make_rule(
        state,
        tree=tree("torrent.ratio", "gte", 0),
        actions=[("speed.limit", {"upload_kib": 1024, "download_kib": -1})],
    )

    result = await runner.run()

    assert result.actions_skipped == 1
    assert qbt_client.request_count == 0


async def test_stop_on_match_prevents_later_rules_from_acting(
    runner: RuleRunner, state: AppState, source_cache: FakeSourceCache, qbt_client: FakeQbtClient
) -> None:
    payload = fx.qbt_payload([fx.torrent("Film", ratio=5.0)])
    load_torrents(source_cache, qbt_client, payload)
    await make_rule(
        state,
        name="first",
        order=0,
        tree=tree("torrent.ratio", "gt", 1),
        actions=[("tag.add", {"tag": "first"})],
        stop_on_match=True,
    )
    await make_rule(
        state,
        name="second",
        order=1,
        tree=tree("torrent.ratio", "gt", 1),
        actions=[("tag.add", {"tag": "second"})],
    )

    await runner.run()

    applied_tags = {c.args[0] for c in qbt_client.calls_for("add_tags")}
    assert applied_tags == {("first",)}


async def test_a_dead_source_is_recorded_and_the_run_still_succeeds(
    runner: RuleRunner, state: AppState, source_cache: FakeSourceCache, qbt_client: FakeQbtClient
) -> None:
    payload = fx.qbt_payload([], error="main-qbt: connection refused")
    load_torrents(source_cache, qbt_client, payload)
    await make_rule(
        state, tree=tree("torrent.ratio", "gte", 0), actions=[("tag.add", {"tag": "x"})]
    )

    result = await runner.run()

    assert result.status is RunStatus.SUCCEEDED
    assert result.error_count == 1
    assert result.source_errors == ["main-qbt: connection refused"]


async def test_an_invalid_condition_fails_only_its_own_rule(
    runner: RuleRunner, state: AppState, source_cache: FakeSourceCache, qbt_client: FakeQbtClient
) -> None:
    payload = fx.qbt_payload([fx.torrent("Film", ratio=5.0)])
    load_torrents(source_cache, qbt_client, payload)
    await make_rule(
        state,
        name="broken",
        order=0,
        tree=tree("torrent.does_not_exist", "eq", "x"),
        actions=[("tag.add", {"tag": "broken"})],
    )
    await make_rule(
        state,
        name="healthy",
        order=1,
        tree=tree("torrent.ratio", "gt", 1),
        actions=[("tag.add", {"tag": "ok"})],
    )

    result = await runner.run()

    assert result.status is RunStatus.SUCCEEDED
    assert result.error_count == 1
    assert {c.args[0] for c in qbt_client.calls_for("add_tags")} == {("ok",)}


async def test_run_history_records_per_rule_results(
    runner: RuleRunner, state: AppState, source_cache: FakeSourceCache, qbt_client: FakeQbtClient
) -> None:
    payload = fx.qbt_payload([fx.torrent("Film", ratio=5.0)])
    load_torrents(source_cache, qbt_client, payload)
    await make_rule(
        state, name="tagger", tree=tree("torrent.ratio", "gt", 1),
        actions=[("tag.add", {"tag": "x"})],
    )

    result = await runner.run(trigger=RunTrigger.SCHEDULE)

    async with state.session_factory() as session:
        run = await session.get(RunHistory, result.run_id)
        assert run is not None
        assert run.status is RunStatus.SUCCEEDED
        assert run.trigger is RunTrigger.SCHEDULE
        assert run.torrents_matched == 1
        assert run.duration_ms is not None

        from sqlalchemy import select

        rule_results = (
            await session.execute(select(RunRuleResult).where(RunRuleResult.run_id == run.id))
        ).scalars().all()
        assert len(rule_results) == 1
        # Denormalised, so history survives the rule being deleted.
        assert rule_results[0].rule_name == "tagger"


async def test_no_run_row_is_written_when_no_rules_are_enabled(
    runner: RuleRunner, state: AppState
) -> None:
    """An idle install should not accumulate empty history."""
    result = await runner.run()

    assert result.run_id is None
    async with state.session_factory() as session:
        from sqlalchemy import func, select

        count = (await session.execute(select(func.count()).select_from(RunHistory))).scalar()
        assert count == 0


async def test_disabled_rules_do_not_run(
    runner: RuleRunner, state: AppState, source_cache: FakeSourceCache, qbt_client: FakeQbtClient
) -> None:
    payload = fx.qbt_payload([fx.torrent("Film", ratio=5.0)])
    load_torrents(source_cache, qbt_client, payload)
    await make_rule(
        state,
        enabled=False,
        tree=tree("torrent.ratio", "gt", 1),
        actions=[("tag.add", {"tag": "x"})],
    )

    result = await runner.run()

    assert result.rules_evaluated == 0
    assert qbt_client.request_count == 0


async def test_a_failing_batch_is_counted_without_failing_the_run(
    runner: RuleRunner, state: AppState, source_cache: FakeSourceCache, qbt_client: FakeQbtClient
) -> None:
    payload = fx.qbt_payload([fx.torrent("Film", ratio=5.0)])
    load_torrents(source_cache, qbt_client, payload)
    qbt_client.fail_on.add("add_tags")
    await make_rule(
        state, tree=tree("torrent.ratio", "gt", 1), actions=[("tag.add", {"tag": "x"})]
    )

    result = await runner.run()

    assert result.status is RunStatus.SUCCEEDED
    assert result.error_count == 1
    assert result.actions_applied == 0


async def test_script_action_is_skipped_unless_explicitly_enabled(
    runner: RuleRunner, state: AppState, source_cache: FakeSourceCache, qbt_client: FakeQbtClient
) -> None:
    """Arbitrary command execution stays off until someone opts in."""
    payload = fx.qbt_payload([fx.torrent("Film", ratio=5.0)])
    load_torrents(source_cache, qbt_client, payload)
    await make_rule(
        state,
        tree=tree("torrent.ratio", "gt", 1),
        actions=[("script.run", {"command": "echo hello"})],
    )

    result = await runner.run()

    assert result.actions_skipped == 1
    assert result.actions_applied == 0
