"""Adapter tests against stubbed HTTP.

These pin the parts of each adapter that no amount of live testing against one
person's server would catch: pagination that stops correctly, an auth retry that
happens exactly once, and the deliberate refusal to report a lifetime play count
that the source cannot actually vouch for.

Every route here is a stub, so the suite runs offline and in CI. The two
adapters whose real APIs could not be verified -- Jellystat and Jellyglance --
are tested against their *configured* contract instead: that the field map and
the row key drive the parse, which is the property that lets a user correct a
wrong default from the UI.
"""

from __future__ import annotations

import httpx
import pytest
import respx

from qbitflow.domain import SourceKind
from qbitflow.sources.base import SourceContext
from qbitflow.sources.jellyfin import JellyfinAdapter
from qbitflow.sources.plex import PlexAdapter
from qbitflow.sources.qbt import QbtAdapter, QbtClient
from qbitflow.sources.rest_history import JellyglanceAdapter, JellystatAdapter
from qbitflow.sources.tautulli import TautulliAdapter

BASE = "http://source.test"


def ctx(kind: SourceKind, **options) -> SourceContext:
    return SourceContext(
        source_id=1,
        name="test-instance",
        kind=kind,
        base_url=BASE,
        username="admin",
        secret="token",
        options=options,
    )


# --------------------------------------------------------------------------- #
# qBittorrent
# --------------------------------------------------------------------------- #


@respx.mock
async def test_qbt_logs_in_once_and_reuses_the_session() -> None:
    login = respx.post(f"{BASE}/api/v2/auth/login").mock(
        return_value=httpx.Response(200, text="Ok.")
    )
    respx.get(f"{BASE}/api/v2/torrents/info").mock(
        return_value=httpx.Response(200, json=[{"hash": "a" * 40, "name": "One"}])
    )

    client = QbtClient(ctx(SourceKind.QBITTORRENT))
    try:
        await client.torrents()
        await client.torrents()
    finally:
        await client.aclose()

    assert login.call_count == 1


@respx.mock
async def test_qbt_re_logs_in_once_when_the_cookie_has_expired() -> None:
    login = respx.post(f"{BASE}/api/v2/auth/login").mock(
        return_value=httpx.Response(200, text="Ok.")
    )
    responses = [
        httpx.Response(403, text="Forbidden"),
        httpx.Response(200, json=[{"hash": "b" * 40, "name": "Two"}]),
    ]
    respx.get(f"{BASE}/api/v2/torrents/info").mock(side_effect=responses)

    client = QbtClient(ctx(SourceKind.QBITTORRENT))
    try:
        torrents = await client.torrents()
    finally:
        await client.aclose()

    assert [t["name"] for t in torrents] == ["Two"]
    # Once to establish the session, once because the 403 forced it.
    assert login.call_count == 2


@respx.mock
async def test_qbt_rejects_bad_credentials_with_a_useful_message() -> None:
    respx.post(f"{BASE}/api/v2/auth/login").mock(
        return_value=httpx.Response(200, text="Fails.")
    )

    client = QbtClient(ctx(SourceKind.QBITTORRENT))
    try:
        with pytest.raises(PermissionError, match="rejected the credentials"):
            await client.torrents()
    finally:
        await client.aclose()


@respx.mock
async def test_qbt_batches_a_tag_into_one_request() -> None:
    respx.post(f"{BASE}/api/v2/auth/login").mock(return_value=httpx.Response(200, text="Ok."))
    route = respx.post(f"{BASE}/api/v2/torrents/addTags").mock(
        return_value=httpx.Response(200)
    )

    client = QbtClient(ctx(SourceKind.QBITTORRENT))
    try:
        await client.add_tags(["a" * 40, "b" * 40, "c" * 40], ["cold"])
    finally:
        await client.aclose()

    assert route.call_count == 1
    body = route.calls[0].request.content.decode()
    assert f"{'a' * 40}%7C{'b' * 40}%7C{'c' * 40}" in body  # pipe-delimited


@respx.mock
async def test_qbt_falls_back_to_the_legacy_pause_endpoint() -> None:
    """qBittorrent 5.0 renamed pause to stop; 4.x answers only the old name."""
    respx.post(f"{BASE}/api/v2/auth/login").mock(return_value=httpx.Response(200, text="Ok."))
    modern = respx.post(f"{BASE}/api/v2/torrents/stop").mock(
        return_value=httpx.Response(404, text="Not Found")
    )
    legacy = respx.post(f"{BASE}/api/v2/torrents/pause").mock(
        return_value=httpx.Response(200)
    )

    client = QbtClient(ctx(SourceKind.QBITTORRENT))
    try:
        await client.stop(["a" * 40])
    finally:
        await client.aclose()

    assert modern.called and legacy.called


@respx.mock
async def test_qbt_fetch_records_the_error_rather_than_raising() -> None:
    """One dead instance must degrade the cycle, never end it."""
    respx.post(f"{BASE}/api/v2/auth/login").mock(
        return_value=httpx.Response(500, text="boom")
    )

    adapter = QbtAdapter(ctx(SourceKind.QBITTORRENT))
    try:
        payload = await adapter.fetch()
    finally:
        await adapter.aclose()

    assert payload.torrents == []
    assert payload.error and "test-instance" in payload.error


@respx.mock
async def test_qbt_normalises_a_torrent_row() -> None:
    respx.post(f"{BASE}/api/v2/auth/login").mock(return_value=httpx.Response(200, text="Ok."))
    respx.get(f"{BASE}/api/v2/torrents/info").mock(
        return_value=httpx.Response(
            200,
            json=[
                {
                    "hash": "d" * 40,
                    "name": "A Film 2001 1080p",
                    "category": "Movies",
                    "tags": "keep, seeding",
                    "save_path": "/downloads/Movies",
                    "content_path": "/downloads/Movies/A Film",
                    "state": "uploading",
                    "size": 5_368_709_120,
                    "ratio": 2.5,
                    "progress": 1.0,
                }
            ],
        )
    )

    adapter = QbtAdapter(ctx(SourceKind.QBITTORRENT))
    try:
        payload = await adapter.fetch()
    finally:
        await adapter.aclose()

    (record,) = payload.torrents
    assert record.name == "A Film 2001 1080p"
    # qBittorrent returns tags as one comma-separated string, spaces included.
    assert record.tags == ["keep", "seeding"]
    assert record.source_name == "test-instance"


# --------------------------------------------------------------------------- #
# Plex
# --------------------------------------------------------------------------- #


def _plex_page(items: list[str], *, total: int) -> str:
    return (
        f'<MediaContainer size="{len(items)}" totalSize="{total}">'
        + "".join(items)
        + "</MediaContainer>"
    )


def _plex_movie(index: int) -> str:
    return (
        f'<Video ratingKey="{index}" type="1" title="Film {index}" year="2001" rating="8.1">'
        f'<Genre tag="Drama"/>'
        f'<Media><Part file="/data/movies/Film.{index}.2001.1080p.mkv" size="123"/></Media>'
        f"</Video>"
    )


@respx.mock
async def test_plex_pages_through_a_library_section() -> None:
    """A section larger than one page must not be silently truncated."""
    respx.get(f"{BASE}/library/sections").mock(
        return_value=httpx.Response(
            200,
            text='<MediaContainer><Directory key="1" type="movie" title="Movies"/>'
            "</MediaContainer>",
        )
    )

    total = 501  # one full page plus a remainder

    def paged(request: httpx.Request) -> httpx.Response:
        start = int(request.headers.get("X-Plex-Container-Start", "0"))
        size = int(request.headers.get("X-Plex-Container-Size", "500"))
        window = range(start, min(start + size, total))
        return httpx.Response(200, text=_plex_page([_plex_movie(i) for i in window], total=total))

    route = respx.get(f"{BASE}/library/sections/1/all").mock(side_effect=paged)
    respx.get(f"{BASE}/status/sessions/history/all").mock(
        return_value=httpx.Response(200, text="<MediaContainer/>")
    )

    adapter = PlexAdapter(ctx(SourceKind.PLEX))
    try:
        payload = await adapter.fetch()
    finally:
        await adapter.aclose()

    assert payload.error is None
    assert len(payload.media_items) == total
    assert route.call_count == 2
    assert payload.media_items[0].files[0].path == "/data/movies/Film.0.2001.1080p.mkv"


@respx.mock
async def test_plex_rolls_episode_history_up_to_the_series() -> None:
    """"Nobody has watched this show in 90 days" must not be true per episode."""
    respx.get(f"{BASE}/library/sections").mock(
        return_value=httpx.Response(200, text="<MediaContainer/>")
    )
    respx.get(f"{BASE}/status/sessions/history/all").mock(
        return_value=httpx.Response(
            200,
            text=(
                "<MediaContainer>"
                '<Video type="episode" grandparentTitle="A Show" title="Ep 1" '
                'ratingKey="10" viewedAt="1700000000"/>'
                '<Video type="episode" grandparentTitle="A Show" title="Ep 2" '
                'ratingKey="11" viewedAt="1700100000"/>'
                '<Video type="movie" title="A Film" ratingKey="12" viewedAt="1700200000"/>'
                "</MediaContainer>"
            ),
        )
    )

    adapter = PlexAdapter(ctx(SourceKind.PLEX))
    try:
        payload = await adapter.fetch()
    finally:
        await adapter.aclose()

    by_title = {r.title: r for r in payload.watch_records}
    assert set(by_title) == {"A Show", "A Film"}
    assert by_title["A Show"].play_count == 2
    assert by_title["A Show"].media_type == "show"
    # The most recent of the two episodes wins.
    assert by_title["A Show"].last_played_at.timestamp() == 1700100000


@respx.mock
async def test_plex_reports_an_unreachable_server_as_unhealthy() -> None:
    respx.get(f"{BASE}/identity").mock(side_effect=httpx.ConnectError("refused"))

    adapter = PlexAdapter(ctx(SourceKind.PLEX))
    try:
        health = await adapter.test()
    finally:
        await adapter.aclose()

    assert not health.ok
    assert health.error


# --------------------------------------------------------------------------- #
# Jellyfin
# --------------------------------------------------------------------------- #


@respx.mock
async def test_jellyfin_pages_items_and_maps_ticks_to_milliseconds() -> None:
    total = 501

    def paged(request: httpx.Request) -> httpx.Response:
        start = int(request.url.params.get("StartIndex", 0))
        limit = int(request.url.params.get("Limit", 500))
        window = range(start, min(start + limit, total))
        return httpx.Response(
            200,
            json={
                "TotalRecordCount": total,
                "Items": [
                    {
                        "Id": str(i),
                        "Name": f"Film {i}",
                        "Type": "Movie",
                        "ProductionYear": 2001,
                        "RunTimeTicks": 60_000_000_000,  # 100 minutes
                        "Path": f"/data/movies/Film.{i}.mkv",
                    }
                    for i in window
                ],
            },
        )

    route = respx.get(f"{BASE}/Items").mock(side_effect=paged)
    respx.get(f"{BASE}/Users").mock(return_value=httpx.Response(200, json=[]))

    adapter = JellyfinAdapter(ctx(SourceKind.JELLYFIN))
    try:
        payload = await adapter.fetch()
    finally:
        await adapter.aclose()

    assert payload.error is None
    assert len(payload.media_items) == total
    assert route.call_count == 2
    assert payload.media_items[0].duration_ms == 100 * 60 * 1000


@respx.mock
async def test_jellyfin_honours_the_user_scope_option() -> None:
    """A household with separate libraries wants one person's activity, not everyone's."""
    respx.get(f"{BASE}/Items").mock(
        return_value=httpx.Response(200, json={"TotalRecordCount": 0, "Items": []})
    )
    respx.get(f"{BASE}/Users").mock(
        return_value=httpx.Response(
            200,
            json=[{"Id": "u1", "Name": "alice"}, {"Id": "u2", "Name": "bob"}],
        )
    )
    alice = respx.get(f"{BASE}/Users/u1/Items").mock(
        return_value=httpx.Response(
            200,
            json={
                "TotalRecordCount": 1,
                "Items": [
                    {
                        "Id": "1",
                        "Name": "A Film",
                        "Type": "Movie",
                        "UserData": {"PlayCount": 3},
                    }
                ],
            },
        )
    )
    bob = respx.get(f"{BASE}/Users/u2/Items").mock(return_value=httpx.Response(200, json={}))

    adapter = JellyfinAdapter(ctx(SourceKind.JELLYFIN, user_scope="alice"))
    try:
        payload = await adapter.fetch()
    finally:
        await adapter.aclose()

    assert alice.called and not bob.called
    (record,) = payload.watch_records
    assert record.title == "A Film"
    assert record.play_count == 3
    # PlayCount is a lifetime total, so it may only populate the "all" window.
    assert set(record.window_counts) <= {"all"}


# --------------------------------------------------------------------------- #
# Tautulli
# --------------------------------------------------------------------------- #


def _tautulli(rows: list[dict], *, filtered: int | None = None) -> httpx.Response:
    return httpx.Response(
        200,
        json={
            "response": {
                "result": "success",
                "data": {
                    "data": rows,
                    "recordsFiltered": len(rows) if filtered is None else filtered,
                },
            }
        },
    )


@respx.mock
async def test_tautulli_counts_windows_from_the_event_timestamps() -> None:
    from datetime import UTC, datetime, timedelta

    now = datetime.now(UTC)
    rows = [
        {
            "media_type": "movie",
            "title": "A Film",
            "rating_key": "1",
            "date": int((now - timedelta(days=days)).timestamp()),
            "duration": 100,
        }
        for days in (1, 20, 200)
    ]
    respx.get(f"{BASE}/api/v2").mock(return_value=_tautulli(rows))

    adapter = TautulliAdapter(ctx(SourceKind.TAUTULLI))
    try:
        payload = await adapter.fetch()
    finally:
        await adapter.aclose()

    (record,) = payload.watch_records
    assert record.play_count == 3
    assert record.window_counts["week"] == 1
    assert record.window_counts["month"] == 2
    assert record.window_counts["year"] == 3
    # The read was exhaustive, so a lifetime total is honest here.
    assert record.window_counts.get("all") == 3


@respx.mock
async def test_tautulli_withholds_the_lifetime_total_when_history_was_capped() -> None:
    """A truncated read must not under-count exactly the old items rules look for."""
    from datetime import UTC, datetime

    now = int(datetime.now(UTC).timestamp())
    rows = [
        {"media_type": "movie", "title": f"Film {i}", "rating_key": str(i), "date": now}
        for i in range(2)
    ]
    # More history exists than the cap allows us to read.
    respx.get(f"{BASE}/api/v2").mock(return_value=_tautulli(rows, filtered=9_999))

    adapter = TautulliAdapter(ctx(SourceKind.TAUTULLI, max_history_records=2))
    try:
        payload = await adapter.fetch()
    finally:
        await adapter.aclose()

    assert payload.watch_records
    assert all("all" not in r.window_counts for r in payload.watch_records)


@respx.mock
async def test_tautulli_surfaces_an_api_level_error() -> None:
    respx.get(f"{BASE}/api/v2").mock(
        return_value=httpx.Response(
            200, json={"response": {"result": "error", "message": "Invalid apikey"}}
        )
    )

    adapter = TautulliAdapter(ctx(SourceKind.TAUTULLI))
    try:
        health = await adapter.test()
    finally:
        await adapter.aclose()

    assert not health.ok
    assert "Invalid apikey" in (health.error or "")


# --------------------------------------------------------------------------- #
# Jellystat / Jellyglance -- configured, not hard-coded
# --------------------------------------------------------------------------- #


@respx.mock
async def test_jellystat_parses_its_documented_shape() -> None:
    respx.post(f"{BASE}/api/getAllUserActivity").mock(
        return_value=httpx.Response(
            200,
            json=[
                {
                    "NowPlayingItemName": "Ep 1",
                    "SeriesName": "A Show",
                    "MediaType": "Episode",
                    "ActivityDateInserted": "2026-05-01T10:00:00Z",
                    "NowPlayingItemId": "abc",
                    "UserName": "alice",
                    "PlaybackDuration": 1200,
                }
            ],
        )
    )

    adapter = JellystatAdapter(ctx(SourceKind.JELLYSTAT))
    try:
        payload = await adapter.fetch()
    finally:
        await adapter.aclose()

    (record,) = payload.watch_records
    assert record.title == "A Show"  # episodes roll up to the series
    assert record.media_type == "show"
    assert record.watched_seconds == 1200


@respx.mock
async def test_an_unverified_adapter_can_be_corrected_from_the_source_options() -> None:
    """The whole point of the configurable adapters.

    If the shipped default endpoint or field names are wrong for a given build,
    the user fixes it in the UI rather than waiting for a new image.
    """
    respx.get(f"{BASE}/custom/plays").mock(
        return_value=httpx.Response(
            200,
            json={
                "payload": {
                    "rows": [
                        {
                            "name": "A Film",
                            "kind": "movie",
                            "when": 1_700_000_000,
                            "id": "42",
                        }
                    ]
                }
            },
        )
    )

    adapter = JellyglanceAdapter(
        ctx(
            SourceKind.JELLYGLANCE,
            history_path="/custom/plays",
            rows_key="payload.rows",
            field_map={
                "title": "name",
                "media_type": "kind",
                "played_at": "when",
                "item_id": "id",
            },
        )
    )
    try:
        payload = await adapter.fetch()
    finally:
        await adapter.aclose()

    (record,) = payload.watch_records
    assert record.title == "A Film"
    assert record.media_type == "movie"
    assert record.last_played_at is not None


@respx.mock
async def test_a_wrong_endpoint_says_which_option_to_change() -> None:
    respx.get(f"{BASE}/api/history").mock(
        return_value=httpx.Response(200, json={"message": "not the list you wanted"})
    )

    adapter = JellyglanceAdapter(ctx(SourceKind.JELLYGLANCE))
    try:
        payload = await adapter.fetch()
    finally:
        await adapter.aclose()

    assert payload.error
    assert "rows_key" in payload.error or "history_path" in payload.error
