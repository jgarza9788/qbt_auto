"""Path normalization and cross-service path mapping.

Plex may report ``/data/movies/Film.mkv`` for the file qBittorrent calls
``/downloads/movies/Film.mkv``. Mappings rewrite one prefix to the other so both
reduce to the same ``path_key``.

The normalization runs once, at ingest, and the result is stored as a column.
Doing it at query time would mean a Python callback per row for every rule in
every cycle, which is exactly what compiling conditions to set-based SQL is meant
to avoid.
"""

from __future__ import annotations

import ntpath
import posixpath
from dataclasses import dataclass


@dataclass(frozen=True, slots=True)
class PathRule:
    from_prefix: str
    to_prefix: str


def normalize_separators(path: str) -> str:
    """Forward slashes, no duplicates, no trailing slash.

    Windows paths reach us from a qBittorrent running on Windows even when this
    app is in a Linux container, so both separator styles have to survive here.
    """
    if not path:
        return ""
    text = path.replace("\\", "/")
    while "//" in text:
        text = text.replace("//", "/")
    if len(text) > 1 and text.endswith("/"):
        text = text[:-1]
    return text


class PathMapper:
    """Applies a set of prefix rewrites, longest prefix first."""

    __slots__ = ("_rules",)

    def __init__(self, rules: list[PathRule] | None = None) -> None:
        # Longest first, so a specific rule beats a general one regardless of the
        # order the user happened to add them in.
        self._rules = sorted(
            (
                PathRule(normalize_separators(r.from_prefix), normalize_separators(r.to_prefix))
                for r in (rules or [])
                if r.from_prefix
            ),
            key=lambda r: len(r.from_prefix),
            reverse=True,
        )

    def apply(self, path: str) -> str:
        text = normalize_separators(path)
        if not text:
            return ""
        lowered = text.lower()
        for rule in self._rules:
            prefix = rule.from_prefix.lower()
            if lowered == prefix:
                return rule.to_prefix
            if lowered.startswith(prefix + "/"):
                return rule.to_prefix + text[len(rule.from_prefix) :]
        return text

    def key(self, path: str) -> str:
        """The canonical form two services' paths must agree on to be the same file."""
        return self.apply(path).lower()


def basename(path: str) -> str:
    """Leaf of a path, whichever separator style it uses."""
    if not path:
        return ""
    text = normalize_separators(path)
    return posixpath.basename(text) or ntpath.basename(path)


def parent_dir(path: str) -> str:
    text = normalize_separators(path)
    return posixpath.dirname(text)


def last_segments(path: str, count: int = 2) -> str:
    """The trailing ``count`` segments, for matching a release folder plus file."""
    text = normalize_separators(path)
    if not text:
        return ""
    parts = [p for p in text.split("/") if p]
    return "/".join(parts[-count:])
