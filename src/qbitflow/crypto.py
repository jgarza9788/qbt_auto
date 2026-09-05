"""Encryption for credentials stored in the database.

Source credentials (qBittorrent passwords, Plex tokens, API keys) are encrypted at
rest with AES-256-GCM. Real encryption is the default and there is no "off" mode:
an obfuscation-only default trains users to believe their volume is safe when it
is not.

The key comes from ``QBITFLOW_SECRET_KEY`` when set, otherwise from a generated
key file under the secrets key directory. Losing the key means the stored
credentials are unrecoverable and must be re-entered -- which is the correct
failure mode, and why the key file is worth backing up.
"""

from __future__ import annotations

import base64
import contextlib
import os
import secrets
import stat
from pathlib import Path

from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives.ciphers.aead import AESGCM

KEY_BYTES = 32
NONCE_BYTES = 12
_KEY_FILENAME = "secret.key"

#: Bound into the AEAD as associated data so a ciphertext cannot be lifted out of
#: one column and replayed into another.
_AAD = b"qbitflow.source-secret.v1"


class SecretDecryptError(RuntimeError):
    """Raised when a stored secret cannot be decrypted with the current key."""


def _read_or_create_key_file(key_dir: Path) -> bytes:
    key_dir.mkdir(parents=True, exist_ok=True)
    path = key_dir / _KEY_FILENAME
    if path.exists():
        raw = path.read_bytes().strip()
        key = base64.urlsafe_b64decode(raw)
        if len(key) != KEY_BYTES:
            raise RuntimeError(
                f"{path} does not contain a {KEY_BYTES}-byte key. "
                "Delete it to generate a new one -- stored credentials will need re-entering."
            )
        return key

    key = secrets.token_bytes(KEY_BYTES)
    # Write before chmod so the key never exists world-readable, even briefly.
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    try:
        os.write(fd, base64.urlsafe_b64encode(key))
    finally:
        os.close(fd)
    # Some bind-mounted filesystems (notably CIFS) reject chmod. The key is
    # still only as exposed as the volume itself.
    with contextlib.suppress(OSError):
        path.chmod(stat.S_IRUSR | stat.S_IWUSR)
    return key


def load_key(key_dir: Path, env_key: str | None = None) -> bytes:
    if env_key:
        try:
            key = base64.urlsafe_b64decode(env_key)
        except Exception as exc:  # noqa: BLE001 - surfaced as a startup error
            raise RuntimeError("QBITFLOW_SECRET_KEY is not valid urlsafe base64") from exc
        if len(key) != KEY_BYTES:
            raise RuntimeError(
                f"QBITFLOW_SECRET_KEY must decode to {KEY_BYTES} bytes, got {len(key)}"
            )
        return key
    return _read_or_create_key_file(key_dir)


def generate_key() -> str:
    """A key suitable for ``QBITFLOW_SECRET_KEY``."""
    return base64.urlsafe_b64encode(secrets.token_bytes(KEY_BYTES)).decode()


class SecretProtector:
    """Encrypts and decrypts short strings. Cheap to construct; hold one per app."""

    __slots__ = ("_aesgcm",)

    def __init__(self, key: bytes) -> None:
        if len(key) != KEY_BYTES:
            raise ValueError(f"key must be {KEY_BYTES} bytes")
        self._aesgcm = AESGCM(key)

    def protect(self, plaintext: str) -> tuple[bytes, bytes]:
        """Returns ``(ciphertext, nonce)``. A fresh nonce per call -- GCM nonce
        reuse under one key is catastrophic, so this is never caller-supplied."""
        nonce = secrets.token_bytes(NONCE_BYTES)
        ct = self._aesgcm.encrypt(nonce, plaintext.encode("utf-8"), _AAD)
        return ct, nonce

    def unprotect(self, ciphertext: bytes, nonce: bytes) -> str:
        try:
            return self._aesgcm.decrypt(nonce, ciphertext, _AAD).decode("utf-8")
        except (InvalidTag, ValueError) as exc:
            raise SecretDecryptError(
                "Stored credential could not be decrypted. The encryption key has "
                "changed or the value is corrupt; re-enter the credential."
            ) from exc
