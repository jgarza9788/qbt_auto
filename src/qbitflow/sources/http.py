"""Shared HTTP plumbing for the networked adapters.

One place for the things every adapter needs and none should re-invent: a bounded
connection pool, a per-host concurrency ceiling, and retry with exponential
backoff and jitter on transient failures.

Retries cover network errors and 5xx only. A 401 or 404 is an answer, not a
hiccup -- retrying it just delays the error the user needs to see.
"""

from __future__ import annotations

import asyncio
import random
from typing import Any

import httpx
import structlog

from qbitflow.sources.base import SourceContext

log = structlog.get_logger(__name__)

RETRYABLE_STATUS = frozenset({502, 503, 504, 429})
DEFAULT_MAX_ATTEMPTS = 3


class TransientSourceError(RuntimeError):
    """A failure worth retrying."""


def build_client(ctx: SourceContext, *, headers: dict[str, str] | None = None) -> httpx.AsyncClient:
    return httpx.AsyncClient(
        base_url=ctx.base_url.rstrip("/"),
        timeout=httpx.Timeout(ctx.timeout_seconds),
        verify=ctx.verify_ssl,
        follow_redirects=True,
        headers=headers or {},
        limits=httpx.Limits(
            max_connections=ctx.max_concurrency,
            max_keepalive_connections=max(2, ctx.max_concurrency // 2),
            keepalive_expiry=120.0,
        ),
    )


class HttpSession:
    """An adapter's HTTP client plus its retry policy and concurrency gate."""

    def __init__(
        self,
        ctx: SourceContext,
        *,
        headers: dict[str, str] | None = None,
        max_attempts: int = DEFAULT_MAX_ATTEMPTS,
    ) -> None:
        self.ctx = ctx
        self._client = build_client(ctx, headers=headers)
        self._gate = asyncio.Semaphore(ctx.max_concurrency)
        self._max_attempts = max_attempts

    @property
    def client(self) -> httpx.AsyncClient:
        return self._client

    async def request(
        self,
        method: str,
        url: str,
        *,
        retry: bool = True,
        **kwargs: Any,
    ) -> httpx.Response:
        attempts = self._max_attempts if retry else 1
        last_exc: Exception | None = None

        for attempt in range(1, attempts + 1):
            async with self._gate:
                try:
                    response = await self._client.request(method, url, **kwargs)
                except (httpx.TransportError, httpx.TimeoutException) as exc:
                    last_exc = exc
                else:
                    if response.status_code not in RETRYABLE_STATUS:
                        return response
                    last_exc = TransientSourceError(
                        f"{response.status_code} from {method} {url}"
                    )

            if attempt < attempts:
                # Full jitter: with several rules waking at once, a fixed backoff
                # just re-synchronises every retry into the same burst.
                delay = random.uniform(0, min(8.0, 0.5 * 2**attempt))  # noqa: S311
                log.debug(
                    "source.retry",
                    source=self.ctx.name,
                    attempt=attempt,
                    delay=round(delay, 2),
                    error=str(last_exc),
                )
                await asyncio.sleep(delay)

        raise TransientSourceError(
            f"{self.ctx.name}: {method} {url} failed after {attempts} attempts: {last_exc}"
        ) from last_exc

    async def get(self, url: str, **kwargs: Any) -> httpx.Response:
        return await self.request("GET", url, **kwargs)

    async def post(self, url: str, **kwargs: Any) -> httpx.Response:
        return await self.request("POST", url, **kwargs)

    async def aclose(self) -> None:
        await self._client.aclose()
