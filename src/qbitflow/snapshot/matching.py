"""Linking a torrent to the library item it contains.

An ordered chain of strategies, first hit wins, each recording the confidence and
the strategy name on the match. Recording *how* a match was made is what makes a
wrong match debuggable -- "matched by fuzzy title at 0.52" tells the user
something that a bare item id does not.

File size is a tie-breaker within a strategy, never a matcher: two unrelated
films are routinely within a few percent of each other, so size alone proves
nothing, but among candidates that already matched by name it is a good signal.
"""

from __future__ import annotations

from dataclasses import dataclass, field

from rapidfuzz import process as fuzz_process

from qbitflow.snapshot.mediakey import normalize_filename, normalize_segments, normalize_title
from qbitflow.sources.paths import parent_dir

#: Below this, a fuzzy title match is more likely to be wrong than right.
FUZZY_THRESHOLD = 88

#: A candidate whose file size is within this fraction of the torrent's size wins
#: a tie against candidates that are not.
SIZE_TOLERANCE = 0.02

MIN_SEGMENT_LENGTH = 4


@dataclass(slots=True)
class CatalogEntry:
    media_item_id: int
    title_key: str
    year: int | None
    media_type: str
    file_sizes: list[int] = field(default_factory=list)


@dataclass(frozen=True, slots=True)
class MatchResult:
    media_item_id: int
    confidence: float
    strategy: str


class MediaCatalog:
    """Indexes over the library, built once per snapshot."""

    def __init__(self) -> None:
        self.entries: dict[int, CatalogEntry] = {}
        self._by_media_key: dict[str, list[int]] = {}
        self._by_parent_key: dict[str, list[int]] = {}
        self._by_title_key: dict[str, list[int]] = {}
        self._fuzzy_titles: list[str] = []
        self._fuzzy_ids: list[int] = []
        self._sealed = False

    def add_item(
        self,
        media_item_id: int,
        *,
        title: str,
        title_key: str,
        year: int | None,
        media_type: str,
    ) -> None:
        self.entries[media_item_id] = CatalogEntry(
            media_item_id=media_item_id,
            title_key=title_key or normalize_title(title),
            year=year,
            media_type=media_type,
        )
        if title_key:
            self._by_title_key.setdefault(title_key, []).append(media_item_id)

    def add_file(
        self, media_item_id: int, *, media_key: str, parent_key: str, size: int | None
    ) -> None:
        if media_key:
            self._by_media_key.setdefault(media_key, []).append(media_item_id)
        if parent_key:
            self._by_parent_key.setdefault(parent_key, []).append(media_item_id)
        entry = self.entries.get(media_item_id)
        if entry is not None and size:
            entry.file_sizes.append(size)

    def seal(self) -> None:
        """Freeze the fuzzy index. Cheap, but pointless to rebuild per lookup."""
        self._fuzzy_titles = []
        self._fuzzy_ids = []
        for media_item_id, entry in self.entries.items():
            if entry.title_key:
                self._fuzzy_titles.append(entry.title_key)
                self._fuzzy_ids.append(media_item_id)
        self._sealed = True

    # -- strategies ---------------------------------------------------------

    def match(
        self,
        *,
        name: str,
        content_path: str,
        save_path: str,
        size: int,
        file_media_keys: list[str] | None = None,
    ) -> MatchResult | None:
        if not self.entries:
            return None

        exact = self._match_exact(name, content_path, file_media_keys or [])
        if exact is not None:
            return MatchResult(self._pick(exact, size), 1.0, "filename")

        segment = self._match_segment(content_path or save_path)
        if segment is not None:
            return MatchResult(self._pick(segment, size), 0.8, "path-segment")

        title_match = self._match_title(name or content_path)
        if title_match is not None:
            candidates, with_year = title_match
            confidence = 0.7 if with_year else 0.6
            return MatchResult(self._pick(candidates, size), confidence, "title-year")

        fuzzy = self._match_fuzzy(name or content_path)
        if fuzzy is not None:
            media_item_id, score = fuzzy
            # Scaled into 0.4-0.55 so a fuzzy hit always ranks below an exact one
            # when a human is reading the confidence column.
            return MatchResult(media_item_id, round(0.4 + (score / 100) * 0.15, 3), "fuzzy-title")

        return None

    def _match_exact(
        self, name: str, content_path: str, file_media_keys: list[str]
    ) -> list[int] | None:
        keys = [normalize_filename(content_path), normalize_filename(name), *file_media_keys]
        for key in keys:
            if not key:
                continue
            hits = self._by_media_key.get(key)
            if hits:
                return hits
        return None

    def _match_segment(self, path: str) -> list[int] | None:
        """Match a torrent's containing folder against a library file's folder.

        Handles the common case where the file names differ but the release
        folder is shared -- a season pack whose episodes the library renamed.
        """
        if not path:
            return None
        folder_key = normalize_filename(parent_dir(path)) or normalize_segments(path, 1)
        if len(folder_key) <= MIN_SEGMENT_LENGTH:
            return None

        hits = self._by_parent_key.get(folder_key)
        if hits:
            return hits

        # Containment either way: the library folder may add or drop a suffix.
        for candidate_key, ids in self._by_parent_key.items():
            if len(candidate_key) <= MIN_SEGMENT_LENGTH:
                continue
            if folder_key in candidate_key or candidate_key in folder_key:
                return ids
        return None

    def _match_title(self, text: str) -> tuple[list[int], bool] | None:
        from qbitflow.snapshot.mediakey import extract_title_year

        title_key, year = extract_title_year(text)
        if len(title_key) <= 2:
            return None
        candidates = self._by_title_key.get(title_key)
        if not candidates:
            return None

        if year is not None:
            same_year = [i for i in candidates if self.entries[i].year == year]
            if same_year:
                return same_year, True
        return candidates, False

    def _match_fuzzy(self, text: str) -> tuple[int, float] | None:
        if not self._sealed or not self._fuzzy_titles:
            return None
        title_key = normalize_title(text)
        if len(title_key) <= 4:
            return None
        hit = fuzz_process.extractOne(
            title_key, self._fuzzy_titles, score_cutoff=FUZZY_THRESHOLD
        )
        if hit is None:
            return None
        _, score, index = hit
        return self._fuzzy_ids[index], float(score)

    def _pick(self, candidates: list[int], size: int) -> int:
        """Break a tie by file size, falling back to the first candidate."""
        if len(candidates) == 1 or not size:
            return candidates[0]
        for media_item_id in candidates:
            entry = self.entries.get(media_item_id)
            if entry is None:
                continue
            for file_size in entry.file_sizes:
                if file_size and abs(file_size - size) / size <= SIZE_TOLERANCE:
                    return media_item_id
        return candidates[0]
