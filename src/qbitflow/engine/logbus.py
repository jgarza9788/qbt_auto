"""Run logs: buffered for one bulk insert, published live for the UI.

A run emits a line per interesting decision. Writing each one immediately would
mean thousands of small transactions competing with everything else on the same
SQLite file, so entries are buffered and inserted once at the end.

They are simultaneously published to any live subscriber, which is what lets the
run detail page tail a run in progress. Subscribers see a sequence number so the
page can replay persisted rows and then stream new ones without showing anything
twice.
"""

from __future__ import annotations

import asyncio
import contextlib
from collections.abc import AsyncIterator
from dataclasses import dataclass
from datetime import UTC, datetime

#: Dropped rather than blocking the run when a browser cannot keep up.
SUBSCRIBER_QUEUE_SIZE = 500


@dataclass(frozen=True, slots=True)
class LogLine:
    run_id: int
    seq: int
    timestamp: datetime
    level: str
    message: str
    torrent_hash: str | None = None
    rule_id: int | None = None

    def as_dict(self) -> dict[str, object]:
        return {
            "run_id": self.run_id,
            "seq": self.seq,
            "timestamp": self.timestamp.isoformat(),
            "level": self.level,
            "message": self.message,
            "torrent_hash": self.torrent_hash,
            "rule_id": self.rule_id,
        }


class RunLogBuffer:
    """Collects one run's log lines and fans them out to live subscribers."""

    def __init__(self, run_id: int, bus: RunLogBus | None = None) -> None:
        self.run_id = run_id
        self._bus = bus
        self._lines: list[LogLine] = []
        self._seq = 0

    def emit(
        self,
        message: str,
        *,
        level: str = "info",
        torrent_hash: str | None = None,
        rule_id: int | None = None,
    ) -> LogLine:
        self._seq += 1
        line = LogLine(
            run_id=self.run_id,
            seq=self._seq,
            timestamp=datetime.now(UTC),
            level=level,
            message=message,
            torrent_hash=torrent_hash,
            rule_id=rule_id,
        )
        self._lines.append(line)
        if self._bus is not None:
            self._bus.publish(line)
        return line

    @property
    def lines(self) -> list[LogLine]:
        return list(self._lines)


class RunLogBus:
    """Fan-out for live run logs. One queue per subscriber."""

    def __init__(self) -> None:
        self._subscribers: dict[int, set[asyncio.Queue[LogLine | None]]] = {}

    def publish(self, line: LogLine) -> None:
        for queue in self._subscribers.get(line.run_id, set()):
            # A slow reader must never slow the run down. Its page will look
            # gappy; the persisted log stays complete.
            with contextlib.suppress(asyncio.QueueFull):
                queue.put_nowait(line)

    def finish(self, run_id: int) -> None:
        for queue in self._subscribers.get(run_id, set()):
            with contextlib.suppress(asyncio.QueueFull):
                queue.put_nowait(None)

    async def subscribe(self, run_id: int) -> AsyncIterator[LogLine]:
        queue: asyncio.Queue[LogLine | None] = asyncio.Queue(SUBSCRIBER_QUEUE_SIZE)
        self._subscribers.setdefault(run_id, set()).add(queue)
        try:
            while True:
                line = await queue.get()
                if line is None:
                    return
                yield line
        finally:
            subscribers = self._subscribers.get(run_id)
            if subscribers is not None:
                subscribers.discard(queue)
                if not subscribers:
                    del self._subscribers[run_id]
