"""Shared request dependencies."""

from __future__ import annotations

from typing import Annotated

from fastapi import Depends, HTTPException, Request

from qbitflow.auth.sessions import AuthenticatedUser, SessionManager
from qbitflow.engine.runner import RuleRunner
from qbitflow.state import AppState


def get_state(request: Request) -> AppState:
    state = getattr(request.app.state, "qbitflow", None)
    if state is None:  # pragma: no cover - only before the lifespan has run
        raise HTTPException(503, "application is still starting")
    return state


def get_sessions(request: Request) -> SessionManager:
    return request.app.state.sessions


def get_runner(state: Annotated[AppState, Depends(get_state)]) -> RuleRunner:
    if state.runner is None:
        raise HTTPException(503, "the engine is not available")
    return state.runner


def current_user(request: Request) -> AuthenticatedUser:
    """The signed-in user, guaranteed present by the auth middleware."""
    user = getattr(request.state, "user", None)
    if user is None:  # pragma: no cover - the middleware would have redirected
        raise HTTPException(401, "not signed in")
    return user


StateDep = Annotated[AppState, Depends(get_state)]
RunnerDep = Annotated[RuleRunner, Depends(get_runner)]
SessionsDep = Annotated[SessionManager, Depends(get_sessions)]
UserDep = Annotated[AuthenticatedUser, Depends(current_user)]
