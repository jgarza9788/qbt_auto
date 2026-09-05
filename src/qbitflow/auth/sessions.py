"""Server-side sessions and CSRF tokens.

The cookie carries an opaque random token. Only its SHA-256 is stored, so someone
who reads the database cannot use what they find to log in -- which is not true
of a scheme that keeps the credential itself in the cookie.

CSRF uses a per-session token rendered into every form and sent by htmx as a
header. Because the token lives in the session row rather than in a second
cookie, a subdomain that can set cookies on the parent domain cannot forge it.
"""

from __future__ import annotations

import hashlib
import secrets
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta

import structlog
from sqlalchemy import delete, func, select
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker

from qbitflow.auth import passwords
from qbitflow.db.base import session_scope
from qbitflow.db.models import Session, User

log = structlog.get_logger(__name__)

SESSION_COOKIE = "qbitflow_session"
CSRF_HEADER = "X-CSRF-Token"
CSRF_FIELD = "csrf_token"
TOKEN_BYTES = 32


def _hash_token(token: str) -> str:
    return hashlib.sha256(token.encode("utf-8")).hexdigest()


def _csrf_for(token_hash: str) -> str:
    """Derived from the session, so it needs no storage of its own."""
    return hashlib.sha256(f"csrf:{token_hash}".encode()).hexdigest()


@dataclass(frozen=True, slots=True)
class AuthenticatedUser:
    id: int
    username: str
    is_admin: bool
    session_id: int
    csrf_token: str


class AuthError(Exception):
    pass


class SetupRequired(AuthError):
    """No account exists yet; every route redirects to the setup wizard."""


class SessionManager:
    def __init__(
        self,
        session_factory: async_sessionmaker[AsyncSession],
        *,
        ttl_hours: int = 24 * 14,
    ) -> None:
        self._factory = session_factory
        self._ttl = timedelta(hours=ttl_hours)

    # -- setup --------------------------------------------------------------

    async def user_count(self) -> int:
        async with self._factory() as session:
            return int(
                (await session.execute(select(func.count()).select_from(User))).scalar() or 0
            )

    async def needs_setup(self) -> bool:
        return await self.user_count() == 0

    async def create_first_user(self, username: str, password: str) -> int:
        """Create the admin account, once.

        Refuses if any account already exists, so the wizard cannot be replayed
        by someone who finds the URL later.
        """
        username = (username or "").strip()
        if not username:
            raise AuthError("username is required")
        passwords.validate_strength(password)

        async with session_scope(self._factory) as session:
            existing = (
                await session.execute(select(func.count()).select_from(User))
            ).scalar()
            if existing:
                raise AuthError("setup has already been completed")
            user = User(
                username=username,
                password_hash=passwords.hash_password(password),
                is_admin=True,
            )
            session.add(user)
            await session.flush()
            log.info("auth.first_user_created", username=username)
            return user.id

    # -- login --------------------------------------------------------------

    async def authenticate(self, username: str, password: str) -> User | None:
        async with self._factory() as session:
            user = (
                await session.execute(
                    select(User).where(
                        func.lower(User.username) == (username or "").strip().lower()
                    )
                )
            ).scalar_one_or_none()

        if user is None:
            # Hash anyway, so a missing username and a wrong password take the
            # same time and cannot be told apart.
            passwords.verify_password(
                "$argon2id$v=19$m=32768,t=3,p=2$c29tZXNhbHRzb21lc2E$"
                "0000000000000000000000000000000000000000000",
                password,
            )
            return None

        if not passwords.verify_password(user.password_hash, password):
            return None

        if passwords.needs_rehash(user.password_hash):
            async with session_scope(self._factory) as session:
                row = await session.get(User, user.id)
                if row is not None:
                    row.password_hash = passwords.hash_password(password)
        return user

    async def create_session(self, user_id: int, *, user_agent: str | None = None) -> str:
        token = secrets.token_urlsafe(TOKEN_BYTES)
        async with session_scope(self._factory) as session:
            session.add(
                Session(
                    token_hash=_hash_token(token),
                    user_id=user_id,
                    expires_at=datetime.now(UTC) + self._ttl,
                    user_agent=(user_agent or "")[:256] or None,
                )
            )
        return token

    async def resolve(self, token: str | None) -> AuthenticatedUser | None:
        if not token:
            return None
        token_hash = _hash_token(token)

        async with self._factory() as session:
            row = (
                await session.execute(select(Session).where(Session.token_hash == token_hash))
            ).scalar_one_or_none()
            if row is None or row.expires_at <= datetime.now(UTC):
                return None
            user = await session.get(User, row.user_id)
            if user is None:
                return None
            session_id = row.id
            last_seen = row.last_seen_at

        # Written at most hourly: a touch on every request would make each page
        # load a write to a database the engine is also using.
        if datetime.now(UTC) - last_seen > timedelta(hours=1):
            async with session_scope(self._factory) as session:
                refreshed = await session.get(Session, session_id)
                if refreshed is not None:
                    refreshed.last_seen_at = datetime.now(UTC)

        return AuthenticatedUser(
            id=user.id,
            username=user.username,
            is_admin=user.is_admin,
            session_id=session_id,
            csrf_token=_csrf_for(token_hash),
        )

    async def revoke(self, token: str | None) -> None:
        if not token:
            return
        async with session_scope(self._factory) as session:
            await session.execute(
                delete(Session).where(Session.token_hash == _hash_token(token))
            )

    async def revoke_all_for_user(self, user_id: int) -> None:
        """Called after a password change, so other devices are signed out."""
        async with session_scope(self._factory) as session:
            await session.execute(delete(Session).where(Session.user_id == user_id))

    async def change_password(self, user_id: int, current: str, new: str) -> None:
        async with self._factory() as session:
            user = await session.get(User, user_id)
        if user is None:
            raise AuthError("account not found")
        # Confirming the current password stops a borrowed unlocked browser from
        # locking the real owner out.
        if not passwords.verify_password(user.password_hash, current):
            raise AuthError("current password is incorrect")
        passwords.validate_strength(new)

        async with session_scope(self._factory) as session:
            row = await session.get(User, user_id)
            if row is not None:
                row.password_hash = passwords.hash_password(new)

    async def purge_expired(self) -> int:
        async with session_scope(self._factory) as session:
            result = await session.execute(
                delete(Session).where(Session.expires_at < datetime.now(UTC))
            )
            return int(result.rowcount or 0)


def csrf_token_for_session_token(token: str) -> str:
    return _csrf_for(_hash_token(token))
