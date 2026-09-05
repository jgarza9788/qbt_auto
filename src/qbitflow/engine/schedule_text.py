"""Cron in both directions, plus the minimum-interval rule.

Most people want "every 15 minutes" or "daily at 3am" and should never see a cron
expression. The ones who do want cron should not be stopped by a picker. So both
exist, and they are two views of the same stored expression.

The 5-minute floor is enforced here rather than in the scheduler, so the editor
can explain the clamp at the point the user typed it instead of silently doing
something other than what they asked.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from datetime import datetime
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

from cron_descriptor import ExpressionDescriptor, FormatException, MissingFieldException
from croniter import CroniterBadCronError, croniter

from qbitflow.settings import MIN_RULE_INTERVAL_SECONDS

MIN_INTERVAL_MINUTES = MIN_RULE_INTERVAL_SECONDS // 60

#: Phrases the picker offers, and what they compile to.
PRESETS: dict[str, str] = {
    "every 5 minutes": "*/5 * * * *",
    "every 15 minutes": "*/15 * * * *",
    "every 30 minutes": "*/30 * * * *",
    "hourly": "0 * * * *",
    "every 6 hours": "0 */6 * * *",
    "every 12 hours": "0 */12 * * *",
    "daily at 3am": "0 3 * * *",
    "daily at midnight": "0 0 * * *",
    "every sunday": "0 3 * * 0",
    "weekly": "0 3 * * 1",
    "monthly": "0 3 1 * *",
}

_EVERY_N = re.compile(r"^every\s+(\d+)\s*(minute|minutes|min|hour|hours|day|days)$")
_DAILY_AT = re.compile(r"^daily\s+at\s+(\d{1,2})(?::(\d{2}))?\s*(am|pm)?$")


class CronError(ValueError):
    pass


@dataclass(frozen=True, slots=True)
class ScheduleInfo:
    cron: str
    description: str
    next_runs: list[datetime]
    #: Set when the expression asked for something faster than the floor.
    clamped_from: str | None = None
    warning: str | None = None


def parse_phrase(text: str) -> str:
    """Turn a human phrase into a cron expression.

    Raises rather than guessing: silently scheduling something other than what
    was typed is worse than saying "I did not understand that".
    """
    phrase = " ".join((text or "").strip().lower().split())
    if not phrase:
        raise CronError("no schedule given")
    if phrase in PRESETS:
        return PRESETS[phrase]

    match = _EVERY_N.match(phrase)
    if match:
        count, unit = int(match.group(1)), match.group(2)
        if count < 1:
            raise CronError("interval must be at least 1")
        if unit.startswith("min"):
            return f"*/{count} * * * *"
        if unit.startswith("hour"):
            return f"0 */{count} * * *"
        return f"0 3 */{count} * *"

    match = _DAILY_AT.match(phrase)
    if match:
        hour = int(match.group(1))
        minute = int(match.group(2) or 0)
        meridiem = match.group(3)
        if meridiem == "pm" and hour < 12:
            hour += 12
        elif meridiem == "am" and hour == 12:
            hour = 0
        if not (0 <= hour <= 23 and 0 <= minute <= 59):
            raise CronError(f"'{text}' is not a valid time of day")
        return f"{minute} {hour} * * *"

    raise CronError(
        f"could not read '{text}' as a schedule. Try something like "
        "'every 15 minutes', 'daily at 3am', or a cron expression."
    )


def describe(cron: str) -> str:
    """Cron rendered back into English for the UI."""
    try:
        return str(ExpressionDescriptor(cron, use_24hour_time_format=False).get_description())
    except (FormatException, MissingFieldException, Exception):  # noqa: B014 - library raises broadly
        return cron


def validate(cron: str) -> None:
    if not croniter.is_valid(cron):
        raise CronError(f"'{cron}' is not a valid cron expression")


def min_interval_seconds(cron: str, *, samples: int = 12) -> int:
    """Shortest gap the expression produces, sampled over the next few firings.

    Sampling rather than parsing: an expression like ``0,1,30 * * * *`` has an
    uneven cadence, and its *shortest* gap is what matters for the floor.
    """
    try:
        iterator = croniter(cron, datetime.now())
    except (CroniterBadCronError, ValueError) as exc:
        raise CronError(str(exc)) from exc

    previous = iterator.get_next(datetime)
    shortest = None
    for _ in range(samples):
        current = iterator.get_next(datetime)
        gap = int((current - previous).total_seconds())
        shortest = gap if shortest is None else min(shortest, gap)
        previous = current
    return shortest or 0


def clamp(cron: str) -> tuple[str, str | None]:
    """Enforce the minimum interval, returning the cron and an explanation.

    A rule firing every minute reads every configured service every minute for
    no benefit -- torrent state does not change that fast, and the services
    being polled are usually on the same small box.
    """
    validate(cron)
    interval = min_interval_seconds(cron)
    if interval >= MIN_RULE_INTERVAL_SECONDS or interval == 0:
        return cron, None

    clamped = f"*/{MIN_INTERVAL_MINUTES} * * * *"
    return clamped, (
        f"'{cron}' would run every {interval}s. The minimum is "
        f"{MIN_RULE_INTERVAL_SECONDS}s, so it has been set to every "
        f"{MIN_INTERVAL_MINUTES} minutes -- polling faster than this reads every "
        "configured service more often without seeing anything new."
    )


def next_runs(cron: str, *, count: int = 3, timezone: str | None = None) -> list[datetime]:
    tz = _zone(timezone)
    base = datetime.now(tz) if tz else datetime.now()
    try:
        iterator = croniter(cron, base)
    except (CroniterBadCronError, ValueError) as exc:
        raise CronError(str(exc)) from exc
    return [iterator.get_next(datetime) for _ in range(count)]


def _zone(timezone: str | None) -> ZoneInfo | None:
    if not timezone:
        return None
    try:
        return ZoneInfo(timezone)
    except (ZoneInfoNotFoundError, ValueError):
        return None


def describe_schedule(
    expression: str, *, timezone: str | None = None, count: int = 3
) -> ScheduleInfo:
    """Everything the editor shows about a schedule, in one call.

    Accepts a cron expression or a human phrase, so the same endpoint serves both
    input modes.
    """
    text = (expression or "").strip()
    if not text:
        raise CronError("no schedule given")

    cron = text if croniter.is_valid(text) else parse_phrase(text)
    clamped, warning = clamp(cron)
    return ScheduleInfo(
        cron=clamped,
        description=describe(clamped),
        next_runs=next_runs(clamped, count=count, timezone=timezone),
        clamped_from=cron if warning else None,
        warning=warning,
    )
