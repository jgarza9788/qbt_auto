"""Liveness and readiness endpoints.

Two endpoints, deliberately different:

``/healthz`` touches nothing and answers as long as the process is serving. It is
what Docker's HEALTHCHECK uses, so a database problem restarts nothing.

``/health`` hits the database and reports degraded when it cannot. That is the one
to point an external monitor at.

Both are exempt from authentication -- a healthcheck that needs a session is a
healthcheck that reports the container unhealthy the moment auth is enabled.
"""

from __future__ import annotations

from fastapi import APIRouter, Request, Response
from sqlalchemy import text

router = APIRouter(include_in_schema=False)


@router.get("/healthz")
async def liveness() -> Response:
    return Response("healthy", media_type="text/plain")


@router.get("/health")
async def readiness(request: Request) -> Response:
    state = request.app.state.qbitflow
    if state.startup_error:
        return _degraded(state.startup_error)
    try:
        async with state.session_factory() as session:
            await session.execute(text("SELECT 1"))
    except Exception as exc:  # noqa: BLE001 - the message is the point
        return _degraded(str(exc))
    return Response('{"status":"ready"}', media_type="application/json")


def _degraded(reason: str) -> Response:
    import json

    return Response(
        json.dumps({"status": "degraded", "reason": reason}),
        status_code=503,
        media_type="application/json",
    )
