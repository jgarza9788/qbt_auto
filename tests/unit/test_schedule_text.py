"""Cron parsing, description, clamping and next-run calculation."""

from __future__ import annotations

import pytest

from qbitflow.engine.schedule_text import (
    MIN_INTERVAL_MINUTES,
    CronError,
    clamp,
    describe_schedule,
    min_interval_seconds,
    next_runs,
    parse_phrase,
)
from qbitflow.settings import MIN_RULE_INTERVAL_SECONDS


@pytest.mark.parametrize(
    ("phrase", "cron"),
    [
        ("every 15 minutes", "*/15 * * * *"),
        ("Every 15 Minutes", "*/15 * * * *"),
        ("  every   15   minutes  ", "*/15 * * * *"),
        ("every 6 hours", "0 */6 * * *"),
        ("hourly", "0 * * * *"),
        ("daily at 3am", "0 3 * * *"),
        ("daily at 11:30 pm", "30 23 * * *"),
        ("daily at 12am", "0 0 * * *"),
        ("daily at 12pm", "0 12 * * *"),
        ("every sunday", "0 3 * * 0"),
        ("every 20 min", "*/20 * * * *"),
    ],
)
def test_phrases_become_cron(phrase: str, cron: str) -> None:
    assert parse_phrase(phrase) == cron


@pytest.mark.parametrize("phrase", ["nonsense here", "", "   ", "every banana minutes"])
def test_unreadable_phrases_are_rejected_rather_than_guessed(phrase: str) -> None:
    with pytest.raises(CronError):
        parse_phrase(phrase)


@pytest.mark.parametrize(
    ("cron", "expected"),
    [
        ("*/15 * * * *", 900),
        ("0 * * * *", 3600),
        ("* * * * *", 60),
        ("0 3 * * *", 86_400),
    ],
)
def test_min_interval_is_measured_from_actual_firings(cron: str, expected: int) -> None:
    assert min_interval_seconds(cron) == expected


def test_uneven_schedules_report_their_shortest_gap() -> None:
    """A rule firing at :00, :01 and :30 runs one minute apart at its tightest."""
    assert min_interval_seconds("0,1,30 * * * *") == 60


@pytest.mark.parametrize("cron", ["* * * * *", "*/2 * * * *", "*/4 * * * *"])
def test_schedules_below_the_floor_are_clamped_with_a_reason(cron: str) -> None:
    clamped, warning = clamp(cron)

    assert clamped == f"*/{MIN_INTERVAL_MINUTES} * * * *"
    assert warning is not None
    # The message has to say what happened and why, not just that it changed.
    assert cron in warning
    assert str(MIN_RULE_INTERVAL_SECONDS) in warning


@pytest.mark.parametrize("cron", ["*/5 * * * *", "*/15 * * * *", "0 3 * * *"])
def test_schedules_at_or_above_the_floor_are_left_alone(cron: str) -> None:
    clamped, warning = clamp(cron)

    assert clamped == cron
    assert warning is None


def test_invalid_cron_is_rejected() -> None:
    with pytest.raises(CronError):
        clamp("not a cron")


def test_next_runs_are_ordered_and_in_the_future() -> None:
    runs = next_runs("0 3 * * *", count=3, timezone="UTC")

    assert len(runs) == 3
    assert runs == sorted(runs)
    assert all(a != b for a, b in zip(runs, runs[1:], strict=False))


def test_describe_schedule_accepts_both_input_forms() -> None:
    from_phrase = describe_schedule("every 15 minutes")
    from_cron = describe_schedule("*/15 * * * *")

    assert from_phrase.cron == from_cron.cron == "*/15 * * * *"
    assert "15" in from_phrase.description
    assert len(from_phrase.next_runs) == 3


def test_describe_schedule_reports_the_clamp() -> None:
    info = describe_schedule("* * * * *")

    assert info.cron == f"*/{MIN_INTERVAL_MINUTES} * * * *"
    assert info.clamped_from == "* * * * *"
    assert info.warning


def test_an_unknown_timezone_falls_back_rather_than_raising() -> None:
    """A bad timezone should degrade to UTC, not break the rule editor."""
    assert len(next_runs("0 3 * * *", timezone="Mars/Olympus_Mons")) == 3
