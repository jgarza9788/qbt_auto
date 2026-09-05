"""The snapshot build: ingest, derive, match, aggregate."""

from __future__ import annotations

import pytest

from qbitflow.snapshot.builder import SnapshotBuilder
from qbitflow.sources.paths import PathMapper, PathRule
from tests.fixtures import payloads as fx


async def build(*payload_list, mapper: PathMapper | None = None):
    # Pinned to the fixtures' clock so every days_since_* assertion is exact.
    return await SnapshotBuilder(mapper).build(list(payload_list), now=fx.NOW)


async def test_torrents_land_with_derived_columns() -> None:
    snapshot = await build(
        fx.qbt_payload(
            [
                fx.torrent(
                    "The.Big.Film.2019.1080p.BluRay.x264-SPARKS",
                    category="Movies",
                    tags=["hd", "seed"],
                    size=8 * 1024**3,
                    added_days_ago=100,
                )
            ]
        )
    )
    row = snapshot.query("SELECT * FROM torrents")[0]

    assert row["category_lower"] == "movies"
    # Comma-wrapped so a test for "hd" cannot match a tag like "hdr".
    assert row["tags"] == ",hd,seed,"
    assert row["tag_count"] == 2
    assert row["size_gb"] == pytest.approx(8.0)
    assert row["days_since_added"] == pytest.approx(100, abs=0.01)
    assert row["is_complete"] == 1
    assert row["media_key"] == "the big film 2019"


async def test_missing_timestamps_stay_null_not_epoch_zero() -> None:
    """A torrent that never completed must not read as "completed in 1970"."""
    snapshot = await build(
        fx.qbt_payload([fx.torrent("Incomplete", completed_days_ago=None, progress=0.4)])
    )
    row = snapshot.query("SELECT completion_on, days_since_completed, is_complete FROM torrents")[0]

    assert row["completion_on"] is None
    assert row["days_since_completed"] is None
    assert row["is_complete"] == 0


async def test_media_items_merge_across_sources() -> None:
    """The same film from Plex and Jellyfin is one row carrying both names."""
    snapshot = await build(
        fx.media_payload(
            [fx.media_item("The Big Film", year=2019, source_name="plex", rating=8.1)],
            source_id=2,
            name="plex",
        ),
        fx.media_payload(
            [fx.media_item("The Big Film", year=2019, source_name="jellyfin")],
            source_id=3,
            name="jellyfin",
        ),
    )
    rows = snapshot.query("SELECT title, source_names, rating FROM media_items")

    assert len(rows) == 1
    assert rows[0]["source_names"] == ",jellyfin,plex,"
    # A later source with no rating must not blank the one we already had.
    assert rows[0]["rating"] == pytest.approx(8.1)


async def test_torrent_matches_library_item_by_filename() -> None:
    snapshot = await build(
        fx.qbt_payload(
            [
                fx.torrent(
                    "The.Big.Film.2019.1080p.BluRay.x264-SPARKS",
                    content_path="/downloads/The.Big.Film.2019.1080p.BluRay.x264-SPARKS.mkv",
                )
            ]
        ),
        fx.media_payload(
            [
                fx.media_item(
                    "The Big Film",
                    year=2019,
                    files=["/media/movies/The.Big.Film.2019.1080p.BluRay.x264-SPARKS.mkv"],
                )
            ]
        ),
    )
    row = snapshot.query(
        "SELECT match_strategy, match_confidence, media_item_id FROM torrents"
    )[0]

    assert row["match_strategy"] == "filename"
    assert row["match_confidence"] == pytest.approx(1.0)
    assert row["media_item_id"] is not None


async def test_path_mapping_makes_differing_mounts_match() -> None:
    """Plex says /data, qBittorrent says /downloads; a mapping reconciles them."""
    mapper = PathMapper([PathRule("/data/movies", "/downloads/movies")])
    snapshot = await build(
        fx.qbt_payload(
            [fx.torrent("Film", content_path="/downloads/movies/Film.mkv")]
        ),
        fx.media_payload([fx.media_item("Film", files=["/data/movies/Film.mkv"])]),
        mapper=mapper,
    )
    row = snapshot.query("SELECT media_item_id, match_strategy FROM torrents")[0]

    assert row["media_item_id"] is not None
    assert row["match_strategy"] == "filename"


async def test_watch_history_rolls_up_into_play_counts() -> None:
    snapshot = await build(
        fx.media_payload(
            [fx.media_item("Watched Film", year=2020)],
            [
                fx.watch("Watched Film", play_count=3, last_played_days_ago=5, source_name="plex"),
            ],
            source_id=2,
            name="plex",
        ),
        fx.media_payload(
            [],
            [
                fx.watch(
                    "Watched Film",
                    play_count=2,
                    last_played_days_ago=40,
                    source_id=4,
                    source_name="tautulli",
                    windows={"week": 0, "month": 1, "year": 2},
                )
            ],
            source_id=4,
            name="tautulli",
        ),
    )
    counts = snapshot.query("SELECT * FROM play_counts")[0]
    item = snapshot.query("SELECT watch_total, days_since_last_watched FROM media_items")[0]

    assert counts["total_plays"] == 5
    assert counts["source_count"] == 2
    # The most recent play across all sources wins, not the last one ingested.
    assert counts["days_since_played"] == pytest.approx(5, abs=0.1)
    assert counts["plays_year"] == 2
    assert item["watch_total"] == 5
    assert item["days_since_last_watched"] == pytest.approx(5, abs=0.1)


async def test_watch_popularity_is_ranked_within_a_media_type() -> None:
    """A rarely-watched episode should not look popular next to unwatched films."""
    snapshot = await build(
        fx.media_payload(
            [
                fx.media_item("Cold Film", year=2001),
                fx.media_item("Warm Film", year=2002),
                fx.media_item("Hot Film", year=2003),
                fx.media_item("Lonely Episode", media_type="episode", year=2004),
            ],
            [
                fx.watch("Warm Film", play_count=5),
                fx.watch("Hot Film", play_count=50),
            ],
        )
    )
    rows = {
        r["title"]: r["watch_popularity"]
        for r in snapshot.query("SELECT title, watch_popularity FROM media_items")
    }

    assert rows["Cold Film"] == pytest.approx(0.0)
    assert rows["Warm Film"] == pytest.approx(0.5)
    assert rows["Hot Film"] == pytest.approx(1.0)
    # Ranked in its own type, where it is the only member.
    assert rows["Lonely Episode"] == pytest.approx(1.0)


async def test_storage_records_land_with_percentages_out_of_a_hundred() -> None:
    snapshot = await build(
        fx.storage_payload([fx.storage("downloads", total_gb=1000, used_percent=87.5)])
    )
    row = snapshot.query("SELECT * FROM storage_paths WHERE name_key = 'downloads'")[0]

    assert row["used_percent"] == pytest.approx(87.5)
    assert row["free_percent"] == pytest.approx(12.5)
    assert row["total_gb"] == pytest.approx(1000, abs=0.01)
    assert row["available"] == 1


async def test_unavailable_storage_is_recorded_not_raised() -> None:
    snapshot = await build(
        fx.storage_payload(
            [fx.storage("nas", available=False, error="path does not exist in this container")]
        )
    )
    row = snapshot.query("SELECT available, error, total_bytes, used_percent FROM storage_paths")[0]

    assert row["available"] == 0
    assert row["error"]
    # Defined values, so a rule reading them gets an answer rather than an error.
    assert row["total_bytes"] == 0
    assert row["used_percent"] == 0


async def test_a_failed_source_is_recorded_and_the_others_still_build() -> None:
    snapshot = await build(
        fx.qbt_payload([], error="dead-qbt: connection refused", source_id=9, name="dead-qbt"),
        fx.qbt_payload([fx.torrent("Alive")], source_id=1, name="main-qbt"),
    )

    assert snapshot.stats.source_errors == ["dead-qbt: connection refused"]
    assert snapshot.stats.torrents == 1
    status = {r["source_name"]: r["ok"] for r in snapshot.query("SELECT * FROM source_status")}
    assert status == {"dead-qbt": 0, "main-qbt": 1}


async def test_torrents_from_two_instances_coexist_under_the_same_hash() -> None:
    """The same torrent on two clients is two rows; the key is (source, hash)."""
    shared = "a" * 40
    snapshot = await build(
        fx.qbt_payload([fx.torrent("Shared", hash=shared)], source_id=1, name="one"),
        fx.qbt_payload([fx.torrent("Shared", hash=shared)], source_id=2, name="two"),
    )

    assert snapshot.stats.torrents == 2
    assert len(snapshot.query("SELECT * FROM torrents")) == 2
