"""The authentication gate.

Fails **closed**. If the gate cannot determine who you are, you do not get in.
The alternative -- serving the UI when auth is misconfigured -- turns a
configuration mistake into an exposure, and the person it exposes the box to is
not the one who made the mistake.

Health endpoints and static assets are exempt, because a container healthcheck
that needs a session reports the container unhealthy the moment auth is on.
"""

from __future__ import annotations

import hmac

import structlog
from fastapi import Request, Response
from fastapi.responses import JSONResponse, RedirectResponse
from starlette.middleware.base import BaseHTTPMiddleware, RequestResponseEndpoint

from qbitflow.auth.sessions import CSRF_FIELD, CSRF_HEADER, SESSION_COOKIE, SessionManager

log = structlog.get_logger(__name__)

#: Reachable without a session.
EXEMPT_PREFIXES = ("/static/", "/setup", "/login")
EXEMPT_PATHS = frozenset({"/healthz", "/health", "/favicon.ico"})

SAFE_METHODS = frozenset({"GET", "HEAD", "OPTIONS"})


def _is_exempt(path: str) -> bool:
    return path in EXEMPT_PATHS or path.startswith(EXEMPT_PREFIXES)


def _wants_json(request: Request) -> bool:
    if request.url.path.startswith("/api/"):
        return True
    return "application/json" in request.headers.get("accept", "")


class AuthMiddleware(BaseHTTPMiddleware):
    def __init__(self, app, sessions: SessionManager) -> None:  # noqa: ANN001
        super().__init__(app)
        self._sessions = sessions
        #: Setup can only ever go from incomplete to complete, so once it is done
        #: the check can be skipped rather than repeated on every request.
        self._setup_complete = False

    async def dispatch(self, request: Request, call_next: RequestResponseEndpoint) -> Response:
        path = request.url.path

        if _is_exempt(path):
            return await call_next(request)

        try:
            if not self._setup_complete:
                if await self._sessions.needs_setup():
                    return self._redirect(request, "/setup", "first-run setup is not complete")
                self._setup_complete = True

            token = request.cookies.get(SESSION_COOKIE)
            user = await self._sessions.resolve(token)
        except Exception:  # noqa: BLE001 - fail closed, never open
            log.exception("auth.gate_failed")
            return JSONResponse(
                {"detail": "authentication is unavailable"}, status_code=503
            )

        if user is None:
            return self._redirect(request, "/login", "not signed in")

        if request.method not in SAFE_METHODS and not await self._csrf_ok(request, user.csrf_token):
            log.warning("auth.csrf_rejected", path=path)
            return JSONResponse({"detail": "invalid or missing CSRF token"}, status_code=403)

        request.state.user = user
        return await call_next(request)

    @staticmethod
    async def _csrf_ok(request: Request, expected: str) -> bool:
        supplied = request.headers.get(CSRF_HEADER)
        if not supplied:
            content_type = request.headers.get("content-type", "")
            if content_type.startswith(
                ("application/x-www-form-urlencoded", "multipart/form-data")
            ):
                form = await request.form()
                supplied = str(form.get(CSRF_FIELD) or "")
        return bool(supplied) and hmac.compare_digest(supplied, expected)

    @staticmethod
    def _redirect(request: Request, target: str, reason: str) -> Response:
        if _wants_json(request):
            return JSONResponse({"detail": reason, "redirect": target}, status_code=401)
        # htmx follows this header rather than swapping the redirect body in.
        if request.headers.get("HX-Request"):
            response = Response(status_code=204)
            response.headers["HX-Redirect"] = target
            return response
        return RedirectResponse(target, status_code=303)
