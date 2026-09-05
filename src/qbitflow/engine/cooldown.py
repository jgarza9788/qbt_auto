"""Per-rule, per-torrent cooldowns.

A safety valve for actions that are not naturally idempotent -- mainly
``script.run``. Deliberately in-memory only: losing the state on restart costs at
most one extra fire, which is cheaper than the complexity of persisting it, and
the script action carries a durable marker of its own for the case where that
actually matters.

Dry runs neither consume a cooldown nor are blocked by one. Previewing what a
rule would do must not change what it will do.
"""

from __future__ import annotations

from datetime import UTC, datetime, timedelta

SWEEP_INTERVAL = timedelta(minutes=10)


class CooldownTracker:
    def __init__(self) -> None:
        self._blocked_until: dict[tuple[int, str], datetime] = {}
        self._next_sweep = datetime.now(UTC) + SWEEP_INTERVAL

    def try_fire(
        self,
        rule_id: int,
        torrent_hash: str,
        cooldown_seconds: int | None,
        *,
        now: datetime | None = None,
    ) -> bool:
        """True when the action may run, recording the new cooldown if so."""
        if not cooldown_seconds or cooldown_seconds <= 0:
            return True

        now = now or datetime.now(UTC)
        self._maybe_sweep(now)

        key = (rule_id, torrent_hash)
        blocked_until = self._blocked_until.get(key)
        if blocked_until is not None and blocked_until > now:
            return False

        self._blocked_until[key] = now + timedelta(seconds=cooldown_seconds)
        return True

    def forget_rule(self, rule_id: int) -> None:
        """Called when a rule is edited or deleted, so a change takes effect now."""
        for key in [k for k in self._blocked_until if k[0] == rule_id]:
            del self._blocked_until[key]

    def clear(self) -> None:
        self._blocked_until.clear()

    def _maybe_sweep(self, now: datetime) -> None:
        if now < self._next_sweep:
            return
        self._next_sweep = now + SWEEP_INTERVAL
        for key in [k for k, until in self._blocked_until.items() if until <= now]:
            del self._blocked_until[key]
