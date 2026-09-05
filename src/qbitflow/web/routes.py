"""Server-rendered pages and the auth forms.

Pages are Jinja templates; anything interactive talks to the JSON API with htmx
or a small amount of Alpine. There is no client-side router and no build step at
runtime, which is what keeps the idle footprint small enough for a Pi.
"""

from __future__ import annotations

from pathlib import Path
from typing import Any

import structlog
from fastapi import APIRouter, Form, Request
from fastapi.responses import HTMLResponse, RedirectResponse, Response
from fastapi.templating import Jinja2Templates
from sqlalchemy import select

from qbitflow.api.deps import StateDep, get_sessions
from qbitflow.auth.passwords import MIN_PASSWORD_LENGTH, WeakPasswordError
from qbitflow.auth.sessions import SESSION_COOKIE, AuthError
from qbitflow.db.models import Rule, RunHistory
from qbitflow.settings import MIN_RULE_INTERVAL_SECONDS

log = structlog.get_logger(__name__)

TEMPLATES_DIR = Path(__file__).parent / "templates"
templates = Jinja2Templates(directory=str(TEMPLATES_DIR))

router = APIRouter(include_in_schema=False)


def _cookie_kwargs(request: Request) -> dict[str, Any]:
    # Secure only over HTTPS: a LAN install on plain http must still be able to
    # log in, and marking the cookie secure there would silently drop it.
    return {
        "httponly": True,
        "samesite": "lax",
        "secure": request.url.scheme == "https",
        "path": "/",
    }


def render(
    request: Request,
    template: str,
    context: dict[str, Any] | None = None,
    *,
    status_code: int = 200,
    **extra: Any,
) -> HTMLResponse:
    payload = {
        "request": request,
        "user": getattr(request.state, "user", None),
        "csrf_token": getattr(getattr(request.state, "user", None), "csrf_token", ""),
        "active": "",
        **(context or {}),
        **extra,
    }
    return templates.TemplateResponse(request, template, payload, status_code=status_code)


# --------------------------------------------------------------------------- #
# First-run setup
# --------------------------------------------------------------------------- #


@router.get("/setup")
async def setup_form(request: Request) -> Response:
    sessions = get_sessions(request)
    if not await sessions.needs_setup():
        # Once an account exists the wizard is gone for good, so finding the URL
        # later gets you nothing.
        return RedirectResponse("/login", status_code=303)
    return render(
        request,
        "setup.html",
        {"min_password_length": MIN_PASSWORD_LENGTH, "error": None},
    )


@router.post("/setup")
async def setup_submit(
    request: Request,
    username: str = Form(...),
    password: str = Form(...),
    confirm: str = Form(...),
) -> Response:
    sessions = get_sessions(request)
    if not await sessions.needs_setup():
        return RedirectResponse("/login", status_code=303)

    error = None
    if password != confirm:
        error = "the two passwords do not match"
    else:
        try:
            user_id = await sessions.create_first_user(username, password)
        except (AuthError, WeakPasswordError) as exc:
            error = str(exc)
        else:
            token = await sessions.create_session(
                user_id, user_agent=request.headers.get("user-agent")
            )
            response = RedirectResponse("/", status_code=303)
            response.set_cookie(SESSION_COOKIE, token, **_cookie_kwargs(request))
            return response

    return render(
        request,
        "setup.html",
        {"min_password_length": MIN_PASSWORD_LENGTH, "error": error, "username": username},
    )


# --------------------------------------------------------------------------- #
# Login and logout
# --------------------------------------------------------------------------- #


@router.get("/login")
async def login_form(request: Request) -> Response:
    sessions = get_sessions(request)
    if await sessions.needs_setup():
        return RedirectResponse("/setup", status_code=303)
    if await sessions.resolve(request.cookies.get(SESSION_COOKIE)):
        return RedirectResponse("/", status_code=303)
    return render(request, "login.html", {"error": None})


@router.post("/login")
async def login_submit(
    request: Request, username: str = Form(...), password: str = Form(...)
) -> Response:
    sessions = get_sessions(request)
    user = await sessions.authenticate(username, password)
    if user is None:
        log.info("auth.login_failed", username=username)
        # One message for both causes: naming which half was wrong tells an
        # attacker which usernames exist.
        return render(
            request,
            "login.html",
            {"error": "incorrect username or password", "username": username},
            status_code=401,
        )

    token = await sessions.create_session(user.id, user_agent=request.headers.get("user-agent"))
    response = RedirectResponse("/", status_code=303)
    response.set_cookie(SESSION_COOKIE, token, **_cookie_kwargs(request))
    return response


@router.post("/logout")
async def logout(request: Request) -> Response:
    sessions = get_sessions(request)
    await sessions.revoke(request.cookies.get(SESSION_COOKIE))
    response = RedirectResponse("/login", status_code=303)
    response.delete_cookie(SESSION_COOKIE, path="/")
    return response


@router.post("/account/password")
async def change_password(
    request: Request,
    current_password: str = Form(...),
    new_password: str = Form(...),
    confirm: str = Form(...),
) -> Response:
    sessions = get_sessions(request)
    user = request.state.user

    if new_password != confirm:
        return _toast(request, "the two passwords do not match", ok=False)
    try:
        await sessions.change_password(user.id, current_password, new_password)
    except (AuthError, WeakPasswordError) as exc:
        return _toast(request, str(exc), ok=False)

    # Every other device is signed out, which is the point of changing it.
    await sessions.revoke_all_for_user(user.id)
    response = _toast(request, "password changed -- signing you back in", ok=True)
    response.headers["HX-Redirect"] = "/login"
    return response


def _toast(request: Request, message: str, *, ok: bool) -> HTMLResponse:
    return templates.TemplateResponse(
        request, "partials/toast.html", {"request": request, "message": message, "ok": ok}
    )


# --------------------------------------------------------------------------- #
# Pages
# --------------------------------------------------------------------------- #


@router.get("/")
async def dashboard(request: Request, state: StateDep) -> Response:
    async with state.session_factory() as session:
        recent = (
            await session.execute(
                select(RunHistory).order_by(RunHistory.started_at.desc()).limit(10)
            )
        ).scalars().all()
    return render(request, "dashboard.html", {"active": "dashboard", "recent_runs": recent})


@router.get("/rules")
async def rules_page(request: Request) -> Response:
    return render(
        request,
        "rules.html",
        {"active": "rules", "min_interval_seconds": MIN_RULE_INTERVAL_SECONDS},
    )


@router.get("/rules/new")
async def new_rule_page(request: Request) -> Response:
    """The editor with nothing loaded. Declared before the {rule_id} route."""
    return render(
        request,
        "rule_editor.html",
        {
            "active": "rules",
            "rule_id": None,
            "min_interval_seconds": MIN_RULE_INTERVAL_SECONDS,
        },
    )


@router.get("/rules/{rule_id}")
async def rule_editor_page(request: Request, rule_id: int, state: StateDep) -> Response:
    async with state.session_factory() as session:
        rule = await session.get(Rule, rule_id)
    if rule is None:
        return RedirectResponse("/rules", status_code=303)
    return render(
        request,
        "rule_editor.html",
        {
            "active": "rules",
            "rule_id": rule_id,
            "rule_name": rule.name,
            "min_interval_seconds": MIN_RULE_INTERVAL_SECONDS,
        },
    )


@router.get("/sources")
async def sources_page(request: Request) -> Response:
    return render(request, "sources.html", {"active": "sources"})


@router.get("/runs")
async def runs_page(request: Request) -> Response:
    return render(request, "runs.html", {"active": "runs"})


@router.get("/runs/{run_id}")
async def run_detail_page(request: Request, run_id: int) -> Response:
    return render(request, "run_detail.html", {"active": "runs", "run_id": run_id})


@router.get("/settings")
async def settings_page(request: Request) -> Response:
    return render(
        request,
        "settings.html",
        {"active": "settings", "min_password_length": MIN_PASSWORD_LENGTH},
    )
