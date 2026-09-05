"""Password hashing with argon2id.

Argon2id is memory-hard, which is what makes an offline attack on a leaked
database expensive. The parameters below are the reference defaults, tuned down
slightly on memory because this application is expected to run on a Raspberry Pi
where 64 MiB per login attempt is a real cost.

Verification re-hashes transparently when the parameters change, so raising them
later upgrades existing users on their next login rather than requiring a reset.
"""

from __future__ import annotations

from argon2 import PasswordHasher
from argon2.exceptions import InvalidHashError, VerificationError, VerifyMismatchError

MIN_PASSWORD_LENGTH = 10

_hasher = PasswordHasher(
    time_cost=3,
    memory_cost=32 * 1024,  # 32 MiB
    parallelism=2,
    hash_len=32,
    salt_len=16,
)


class WeakPasswordError(ValueError):
    pass


def validate_strength(password: str) -> None:
    """A length floor and nothing else.

    Composition rules (a digit, a symbol, a capital) push people towards
    predictable substitutions without adding real entropy; length is what
    actually helps.
    """
    if len(password or "") < MIN_PASSWORD_LENGTH:
        raise WeakPasswordError(
            f"password must be at least {MIN_PASSWORD_LENGTH} characters"
        )


def hash_password(password: str) -> str:
    validate_strength(password)
    return _hasher.hash(password)


def verify_password(stored_hash: str, password: str) -> bool:
    try:
        return _hasher.verify(stored_hash, password)
    except (VerifyMismatchError, VerificationError, InvalidHashError):
        return False


def needs_rehash(stored_hash: str) -> bool:
    try:
        return _hasher.check_needs_rehash(stored_hash)
    except InvalidHashError:
        return True
