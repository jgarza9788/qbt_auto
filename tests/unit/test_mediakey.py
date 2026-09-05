"""Golden tests for release-name normalization.

These pin the behaviour everything downstream in matching depends on. A change
here silently changes which torrents match which library items, so the cases are
written as exact expected strings rather than loose assertions.
"""

from __future__ import annotations

import pytest

from qbitflow.snapshot.mediakey import (
    extract_title_year,
    is_episode_code,
    is_year,
    match_key,
    normalize_filename,
    normalize_segments,
    normalize_title,
)


@pytest.mark.parametrize(
    ("raw", "expected"),
    [
        # The technical soup is dropped, but the episode code survives it.
        (
            "Some.Show.S01E04.2160p.WEB-DL.DDP5.1.HDR.x265-GROUP.mkv",
            "some show s01e04",
        ),
        # A trailing year likewise survives.
        (
            "The.Big.Film.2019.1080p.BluRay.x264-SPARKS.mkv",
            "the big film 2019",
        ),
        # Full paths reduce to their leaf.
        (
            "/downloads/Movies/The.Big.Film.2019.1080p/The.Big.Film.2019.1080p.mkv",
            "the big film 2019",
        ),
        # Brackets and braces go; a parenthesised year stays.
        ("Another Movie (2021) [1080p] {edition-Extended}.mkv", "another movie 2021"),
        # Nothing technical to strip.
        ("No.Quality.Tags.Here.mkv", "no quality tags here"),
        ("Simple Movie 1998.mp4", "simple movie 1998"),
        # A dense stack of tags, including 7.1 audio and a REMUX.
        (
            "Movie.Title.2020.REPACK.PROPER.2160p.UHD.BluRay.REMUX."
            "HDR10.TrueHD.7.1.Atmos-FraMeSToR.mkv",
            "movie title 2020",
        ),
        # Separator-joined tags are recognised as single tokens.
        ("Film.2018.Blu-Ray.DTS-HD.MA.x-264.mkv", "film 2018"),
        ("", ""),
        ("   ", ""),
    ],
)
def test_normalize_filename(raw: str, expected: str) -> None:
    assert normalize_filename(raw) == expected


@pytest.mark.parametrize(
    ("raw", "expected"),
    [
        # Truncation at the year is what lets an episode file match its series.
        ("Some.Show.S01E04.1080p.mkv", "some show"),
        ("The.Big.Film.2019.1080p.BluRay.mkv", "the big film"),
        ("Plain Title", "plain title"),
        ("Show Name - S02E10 - Episode Title [1080p].mkv", "show name"),
    ],
)
def test_normalize_title(raw: str, expected: str) -> None:
    assert normalize_title(raw) == expected


@pytest.mark.parametrize(
    ("raw", "expected"),
    [
        ("The.Big.Film.2019.1080p.mkv", ("the big film", 2019)),
        ("Some.Show.S01E04.mkv", ("some show", None)),
        ("Untitled", ("untitled", None)),
    ],
)
def test_extract_title_year(raw: str, expected: tuple[str, int | None]) -> None:
    assert extract_title_year(raw) == expected


def test_match_key_collapses_the_same_item_from_two_sources() -> None:
    """Plex's "The Big Film" and a Jellyfin release name are one library item."""
    from_plex = match_key("movie", "The Big Film", 2019)
    from_release = match_key("movie", "The.Big.Film.2019.1080p.BluRay.x264", 2019)
    assert from_plex == from_release == "movie|the big film|2019"


def test_match_key_separates_types_and_years() -> None:
    assert match_key("movie", "Title", 2019) != match_key("show", "Title", 2019)
    assert match_key("movie", "Title", 2019) != match_key("movie", "Title", 2020)


def test_normalize_segments_keeps_the_release_folder() -> None:
    result = normalize_segments("/dl/The.Big.Film.2019.1080p.BluRay-SPARKS/film.x264.mkv", 2)
    assert result == "the big film 2019/film"


@pytest.mark.parametrize("token", ["1999", "2026", "2000"])
def test_is_year_accepts_plausible_years(token: str) -> None:
    assert is_year(token)


@pytest.mark.parametrize("token", ["1899", "123", "20260", "abcd", "3000"])
def test_is_year_rejects_everything_else(token: str) -> None:
    assert not is_year(token)


@pytest.mark.parametrize("token", ["s01e04", "S1E1", "s001e0004", "s02"])
def test_is_episode_code(token: str) -> None:
    assert is_episode_code(token)


@pytest.mark.parametrize("token", ["e04", "season", "s", "1x04"])
def test_is_not_episode_code(token: str) -> None:
    assert not is_episode_code(token)


def test_normalization_is_stable_across_repeated_application() -> None:
    """Normalizing an already-normalized name must be a no-op.

    Both sides of a match are normalized, and library paths are sometimes already
    clean; an unstable function would make those cases miss.
    """
    once = normalize_filename("The.Big.Film.2019.1080p.BluRay.x264-SPARKS.mkv")
    assert normalize_filename(once) == once
