"""FastAPI application factory and lifespan.

The lifespan owns everything with a lifetime longer than a request. In
particular, engine runs are started from a task owned here rather than from
FastAPI's ``BackgroundTasks`` -- a background task tied to a request is cancelled
when that request's response is returned, which would silently truncate a
manually triggered run partway through applying actions.
"""

from __future__ import annotations

from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from pathlib import Path

import structlog
from fastapi import FastAPI
from fastapi.staticfiles import StaticFiles
from starlette.middleware.trustedhost import TrustedHostMiddleware

from qbitflow.api import config_io, health, meta, rules, sources
from qbitflow.api import engine as engine_api
from qbitflow.auth.middleware import AuthMiddleware
from qbitflow.auth.sessions import SessionManager
from qbitflow.config import AppConfig, get_config
from qbitflow.crypto import SecretProtector, load_key
from qbitflow.db.base import create_engine, create_session_factory
from qbitflow.db.migrate import upgrade_to_head
from qbitflow.engine.runner import RuleRunner
from qbitflow.engine.scheduler import RuleScheduler
from qbitflow.logging import configure_logging
from qbitflow.settings import SettingsStore
from qbitflow.snapshot.builder import SnapshotHolder
from qbitflow.sources.registry import SourceCache, SourceResolver
from qbitflow.state import AppState
from qbitflow.web import routes as web_routes

log = structlog.get_logger(__name__)

STATIC_DIR = Path(__file__).parent / "web" / "static"


def build_state(config: AppConfig) -> AppState:
    config.data_dir.mkdir(parents=True, exist_ok=True)
    key = load_key(config.resolved_key_dir, config.secret_key)
    engine = create_engine(config.resolved_db_path)
    factory = create_session_factory(engine)
    protector = SecretProtector(key)

    state = AppState(
        config=config,
        engine=engine,
        session_factory=factory,
        settings=SettingsStore(factory),
        protector=protector,
        source_cache=SourceCache(factory, SourceResolver(protector, config)),
        snapshot=SnapshotHolder(),
    )
    state.runner = RuleRunner(state)
    state.scheduler = RuleScheduler(state, state.runner)
    return state


def _startup_banner(config: AppConfig, *, needs_setup: bool) -> None:
    """One log event, so ``docker compose logs`` shows it contiguously.

    Self-hosted users read the container log to find out where to go next; a
    banner is far kinder than making them guess the port and the setup path.
    """
    lines = [
        "",
        "  qbitflow is running",
        f"  URL:      http://{config.host}:{config.port}/",
        f"  Database: {config.resolved_db_path}",
    ]
    if needs_setup:
        lines.append("  Setup:    open the URL above to create the administrator account")
    lines.append("  Engine:   dry-run by default -- nothing is changed until you turn it off")
    if config.enable_script_action:
        lines.append("  WARNING:  the script.run action is ENABLED; rules can run commands here")
    lines.append("")
    log.info("startup.banner", banner="\n".join(lines))


@asynccontextmanager
async def lifespan(app: FastAPI) -> AsyncIterator[None]:
    config: AppConfig = app.state.config
    configure_logging(config.log_level, json_output=config.log_json)

    try:
        upgrade_to_head(config.resolved_db_path)
    except Exception as exc:  # noqa: BLE001 - surfaced via /health rather than crashing
        log.exception("startup.migration_failed")
        state = build_state(config)
        state.startup_error = f"database migration failed: {exc}"
        app.state.qbitflow = state
        app.state.sessions = SessionManager(state.session_factory)
        yield
        return

    state = build_state(config)
    app.state.qbitflow = state
    app.state.sessions = SessionManager(
        state.session_factory, ttl_hours=config.session_ttl_hours
    )

    needs_setup = True
    try:
        needs_setup = await app.state.sessions.needs_setup()
    except Exception:  # noqa: BLE001 - the banner is not worth failing startup over
        log.warning("startup.setup_check_failed", exc_info=True)
    _startup_banner(config, needs_setup=needs_setup)

    if not config.testing and state.scheduler is not None:
        # Owned by the lifespan, never by a request: a run started from an HTTP
        # handler must outlive that handler's response.
        try:
            await state.scheduler.start()
        except Exception:  # noqa: BLE001 - the UI must come up even if this does not
            log.exception("startup.scheduler_failed")
            state.startup_error = "the scheduler failed to start; rules will not run"

    try:
        yield
    finally:
        if state.scheduler is not None:
            await state.scheduler.shutdown()
        if state.snapshot is not None:
            await state.snapshot.close()
        if state.source_cache is not None:
            await state.source_cache.aclose()
        await state.engine.dispose()
        log.info("shutdown.complete")


def create_app(config: AppConfig | None = None) -> FastAPI:
    config = config or get_config()
    app = FastAPI(
        title="qbitflow",
        version="0.1.0",
        lifespan=lifespan,
        docs_url="/api/docs",
        openapi_url="/api/openapi.json",
    )
    app.state.config = config

    if config.allowed_hosts != ["*"]:
        app.add_middleware(TrustedHostMiddleware, allowed_hosts=config.allowed_hosts)

    # Added last so it runs first: nothing below it is reachable without a session.
    app.add_middleware(AuthMiddleware, sessions=_lazy_sessions(app))

    STATIC_DIR.mkdir(parents=True, exist_ok=True)
    app.mount("/static", StaticFiles(directory=str(STATIC_DIR)), name="static")

    app.include_router(health.router)
    app.include_router(meta.router)
    app.include_router(rules.router)
    app.include_router(sources.router)
    app.include_router(engine_api.router)
    app.include_router(config_io.router)
    app.include_router(web_routes.router)
    return app


class _LazySessions:
    """Defers to the SessionManager the lifespan builds.

    Middleware is constructed at app-creation time, before the database engine
    exists, so it holds this instead of the manager itself.
    """

    def __init__(self, app: FastAPI) -> None:
        self._app = app

    def __getattr__(self, name: str):  # noqa: ANN204 - proxies whatever is asked for
        manager = getattr(self._app.state, "sessions", None)
        if manager is None:
            raise RuntimeError("sessions are not available yet")
        return getattr(manager, name)


def _lazy_sessions(app: FastAPI) -> _LazySessions:
    return _LazySessions(app)


app = create_app()
