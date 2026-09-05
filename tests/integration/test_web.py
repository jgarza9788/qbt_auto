"""The HTTP surface: auth gate, setup wizard, pages, and the API contract.

Runs against the real app through a TestClient, so routing, middleware ordering
and template rendering are all exercised rather than assumed.
"""

from __future__ import annotations

from collections.abc import Iterator
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from qbitflow.auth.sessions import SESSION_COOKIE
from qbitflow.config import AppConfig
from qbitflow.crypto import generate_key
from qbitflow.main import create_app

PASSWORD = "correct horse battery staple"


@pytest.fixture
def client(tmp_path: Path) -> Iterator[TestClient]:
    config = AppConfig(
        data_dir=tmp_path / "data",
        exports_dir=tmp_path / "exports",
        secret_key=generate_key(),
        testing=True,
    )
    with TestClient(create_app(config), follow_redirects=False) as c:
        yield c


@pytest.fixture
def signed_in(client: TestClient) -> TestClient:
    client.post(
        "/setup",
        data={"username": "admin", "password": PASSWORD, "confirm": PASSWORD},
    )
    client.follow_redirects = True
    return client


# --------------------------------------------------------------------------- #
# The gate
# --------------------------------------------------------------------------- #


def test_a_fresh_install_sends_you_to_setup(client: TestClient) -> None:
    response = client.get("/")
    assert response.status_code == 303
    assert response.headers["location"] == "/setup"


def test_health_endpoints_are_reachable_without_a_session(client: TestClient) -> None:
    """A container healthcheck that needs a login reports the container unhealthy."""
    assert client.get("/healthz").status_code == 200
    assert client.get("/health").status_code == 200


def test_the_api_is_closed_to_anonymous_callers(client: TestClient) -> None:
    for path in ["/api/fields", "/api/rules", "/api/sources", "/api/settings"]:
        assert client.get(path).status_code == 401, path


def test_static_assets_are_served(signed_in: TestClient) -> None:
    for path in ["/static/app.css", "/static/htmx.min.js", "/static/alpine.min.js"]:
        response = signed_in.get(path)
        assert response.status_code == 200, path
        assert len(response.content) > 500, path


# --------------------------------------------------------------------------- #
# Setup and login
# --------------------------------------------------------------------------- #


def test_setup_creates_the_admin_and_signs_them_in(client: TestClient) -> None:
    response = client.post(
        "/setup", data={"username": "admin", "password": PASSWORD, "confirm": PASSWORD}
    )
    assert response.status_code == 303
    assert response.headers["location"] == "/"
    assert client.cookies.get(SESSION_COOKIE)


def test_setup_rejects_mismatched_passwords(client: TestClient) -> None:
    response = client.post(
        "/setup", data={"username": "admin", "password": PASSWORD, "confirm": "different"}
    )
    assert response.status_code == 200
    assert "do not match" in response.text


def test_setup_rejects_a_short_password(client: TestClient) -> None:
    response = client.post(
        "/setup", data={"username": "admin", "password": "short", "confirm": "short"}
    )
    assert response.status_code == 200
    assert "at least" in response.text


def test_setup_cannot_be_replayed_once_an_account_exists(signed_in: TestClient) -> None:
    """Finding the setup URL later must not let anyone create a second admin."""
    signed_in.follow_redirects = False
    response = signed_in.get("/setup")
    assert response.status_code == 303
    assert response.headers["location"] == "/login"


def test_login_with_the_wrong_password_is_refused(client: TestClient) -> None:
    client.post("/setup", data={"username": "admin", "password": PASSWORD, "confirm": PASSWORD})
    client.cookies.clear()

    response = client.post("/login", data={"username": "admin", "password": "wrong"})
    assert response.status_code == 401
    assert "incorrect username or password" in response.text


def test_the_login_error_does_not_reveal_whether_the_username_exists(
    client: TestClient,
) -> None:
    client.post("/setup", data={"username": "admin", "password": PASSWORD, "confirm": PASSWORD})
    client.cookies.clear()

    wrong_password = client.post("/login", data={"username": "admin", "password": "nope"})
    no_such_user = client.post("/login", data={"username": "ghost", "password": "nope"})

    assert wrong_password.status_code == no_such_user.status_code == 401
    # The only difference between the two pages is the username the caller
    # typed back into the form; the error itself must not distinguish them.
    assert wrong_password.text.replace("admin", "USER") == no_such_user.text.replace(
        "ghost", "USER"
    )


def test_logout_clears_the_session(signed_in: TestClient) -> None:
    signed_in.follow_redirects = False
    csrf = _csrf(signed_in)
    response = signed_in.post("/logout", data={"csrf_token": csrf})

    assert response.status_code == 303
    assert signed_in.get("/").headers["location"] == "/login"


# --------------------------------------------------------------------------- #
# CSRF
# --------------------------------------------------------------------------- #


def _csrf(client: TestClient) -> str:
    page = client.get("/settings").text
    marker = 'name="csrf-token" content="'
    start = page.index(marker) + len(marker)
    return page[start : page.index('"', start)]


def test_a_post_without_a_csrf_token_is_refused(signed_in: TestClient) -> None:
    response = signed_in.request(
        "POST", "/api/settings", json={"dry_run": False}, headers={"X-CSRF-Token": ""}
    )
    assert response.status_code == 403


def test_a_post_with_a_wrong_csrf_token_is_refused(signed_in: TestClient) -> None:
    response = signed_in.post(
        "/api/settings", json={"dry_run": False}, headers={"X-CSRF-Token": "nonsense"}
    )
    assert response.status_code == 403


def test_a_post_with_the_right_csrf_token_succeeds(signed_in: TestClient) -> None:
    response = signed_in.post(
        "/api/settings", json={"dry_run": False}, headers={"X-CSRF-Token": _csrf(signed_in)}
    )
    assert response.status_code == 200
    assert response.json()["dry_run"] is False


# --------------------------------------------------------------------------- #
# Pages and API
# --------------------------------------------------------------------------- #


@pytest.mark.parametrize("path", ["/", "/rules", "/sources", "/runs", "/settings"])
def test_every_page_renders(signed_in: TestClient, path: str) -> None:
    response = signed_in.get(path)
    assert response.status_code == 200
    assert "<title>" in response.text
    # A template that failed to render its component would still return 200.
    assert "x-data" in response.text


@pytest.mark.parametrize(
    "path",
    [
        "/api/fields",
        "/api/operators",
        "/api/actions",
        "/api/rules",
        "/api/engine",
        "/api/sources",
        "/api/storage",
        "/api/path-mappings",
        "/api/settings",
        "/api/settings/defaults",
        "/api/runs",
        "/api/config/export?format=yaml",
        "/api/config/examples",
        "/api/schedule/describe?expression=every+15+minutes",
    ],
)
def test_every_api_endpoint_answers(signed_in: TestClient, path: str) -> None:
    assert signed_in.get(path).status_code == 200, path


def test_the_field_reference_is_grouped_and_carries_operators(signed_in: TestClient) -> None:
    data = signed_in.get("/api/fields").json()

    assert data["groups"], "no field groups"
    assert data["helpers"], "no helper functions documented"
    assert "torrents" in data["tables"]
    for group in data["groups"]:
        for field in group["fields"]:
            assert field["operators"], f"{field['key']} has no operators"
            assert field["description"], f"{field['key']} has no description"


def test_action_schemas_describe_their_parameters(signed_in: TestClient) -> None:
    actions = signed_in.get("/api/actions").json()
    by_type = {a["type"]: a for a in actions}

    assert len(actions) == 11
    assert by_type["tag.sync"]["bidirectional"] is True
    assert by_type["script.run"]["requires_opt_in"] == "QBITFLOW_ENABLE_SCRIPT_ACTION"
    # The UI builds its form from this, so it has to carry the fields.
    assert "tag" in by_type["tag.add"]["params"]["properties"]


def test_saving_and_reading_back_a_rule(signed_in: TestClient) -> None:
    payload = {
        "rules": [
            {
                "id": None,
                "name": "big torrents",
                "condition_mode": "builder",
                "condition_tree": {
                    "logic": "and",
                    "conditions": [
                        {"field": "torrent.size_gb", "operator": "gt", "value": 10}
                    ],
                    "groups": [],
                },
                "cron": "*/30 * * * *",
                "actions": [{"type": "tag.add", "params": {"tag": "large"}}],
            }
        ]
    }
    saved = signed_in.post("/api/rules", json=payload, headers={"X-CSRF-Token": _csrf(signed_in)})
    assert saved.status_code == 200
    assert saved.json()["saved"] == 1

    rules = signed_in.get("/api/rules").json()
    assert len(rules) == 1
    assert rules[0]["name"] == "big torrents"
    assert rules[0]["compile_valid"] is True
    assert "t.size_gb > ?" in rules[0]["compiled_sql"]


def test_a_too_fast_schedule_is_clamped_and_explained(signed_in: TestClient) -> None:
    payload = {
        "rules": [
            {
                "id": None,
                "name": "hyperactive",
                "condition_tree": {"logic": "and", "conditions": [], "groups": []},
                "cron": "* * * * *",
                "actions": [],
            }
        ]
    }
    response = signed_in.post(
        "/api/rules", json=payload, headers={"X-CSRF-Token": _csrf(signed_in)}
    )
    assert response.status_code == 200
    assert response.json()["warnings"], "the clamp was not explained"
    assert signed_in.get("/api/rules").json()[0]["cron"] == "*/5 * * * *"


def test_an_unknown_action_is_rejected_at_save_time(signed_in: TestClient) -> None:
    payload = {
        "rules": [
            {
                "id": None,
                "name": "bad",
                "condition_tree": {"logic": "and", "conditions": [], "groups": []},
                "actions": [{"type": "does.not.exist", "params": {}}],
            }
        ]
    }
    response = signed_in.post(
        "/api/rules", json=payload, headers={"X-CSRF-Token": _csrf(signed_in)}
    )
    assert response.status_code == 422


def test_the_compile_preview_shows_sql_and_reports_errors(signed_in: TestClient) -> None:
    csrf = _csrf(signed_in)
    good = signed_in.post(
        "/api/rules/compile",
        json={
            "condition_mode": "builder",
            "condition_tree": {
                "logic": "and",
                "conditions": [{"field": "torrent.ratio", "operator": "gt", "value": 2}],
                "groups": [],
            },
        },
        headers={"X-CSRF-Token": csrf},
    ).json()
    assert good["valid"] is True
    assert "t.ratio > ?" in good["sql"]
    # The preview inlines values for reading; the executed statement does not.
    assert "t.ratio > 2.0" in good["preview"]

    bad = signed_in.post(
        "/api/rules/compile",
        json={
            "condition_mode": "builder",
            "condition_tree": {
                "logic": "and",
                "conditions": [{"field": "nope.nope", "operator": "eq", "value": "x"}],
                "groups": [],
            },
        },
        headers={"X-CSRF-Token": csrf},
    ).json()
    assert bad["valid"] is False
    assert "unknown field" in bad["error"]


def test_secrets_are_never_returned(signed_in: TestClient) -> None:
    csrf = _csrf(signed_in)
    signed_in.post(
        "/api/sources",
        json={
            "name": "qbt",
            "kind": "qbittorrent",
            "base_url": "http://localhost:8080",
            "username": "admin",
            "secret": "super-secret-password",
        },
        headers={"X-CSRF-Token": csrf},
    )

    listing = signed_in.get("/api/sources")
    assert "super-secret-password" not in listing.text
    assert listing.json()[0]["has_secret"] is True

    export = signed_in.get("/api/config/export?format=yaml")
    assert "super-secret-password" not in export.text


def test_config_import_previews_before_it_writes(signed_in: TestClient) -> None:
    csrf = _csrf(signed_in)
    examples = signed_in.get("/api/config/examples").text

    preview = signed_in.post(
        "/api/config/import",
        json={"content": examples, "apply": False},
        headers={"X-CSRF-Token": csrf},
    ).json()
    assert preview["applied"] is False
    assert len(preview["rules"]) >= 5
    assert signed_in.get("/api/rules").json() == []

    applied = signed_in.post(
        "/api/config/import",
        json={"content": examples, "apply": True},
        headers={"X-CSRF-Token": csrf},
    ).json()
    assert applied["applied"] is True

    rules = signed_in.get("/api/rules").json()
    assert len(rules) == len(preview["rules"])
    # Imported rules must not start acting on someone's library unreviewed.
    assert all(not r["enabled"] for r in rules)


def test_settings_are_clamped_and_the_clamped_value_is_returned(
    signed_in: TestClient,
) -> None:
    response = signed_in.post(
        "/api/settings",
        json={"run_retention": 999_999, "action_batch_size": 0},
        headers={"X-CSRF-Token": _csrf(signed_in)},
    ).json()

    assert response["run_retention"] == 10_000
    assert response["action_batch_size"] == 1


def test_the_schedule_helper_accepts_a_phrase_and_reports_next_runs(
    signed_in: TestClient,
) -> None:
    data = signed_in.get("/api/schedule/describe?expression=daily+at+3am").json()

    assert data["cron"] == "0 3 * * *"
    assert len(data["next_runs"]) == 3
    assert data["presets"]


# --------------------------------------------------------------------------- #
# Disaster recovery
# --------------------------------------------------------------------------- #


def test_a_configuration_survives_export_wipe_and_reimport(tmp_path: Path) -> None:
    """Export, lose the volume, import: the rules come back -- disabled.

    This is the recovery path the README tells people to rely on, so it is worth
    proving end to end rather than testing export and import separately and
    assuming they meet in the middle. The one thing that deliberately does *not*
    survive is the credential, because an export that carried it would not be
    safe to share.
    """
    first = AppConfig(
        data_dir=tmp_path / "one",
        exports_dir=tmp_path / "exports",
        secret_key=generate_key(),
        testing=True,
    )
    with TestClient(create_app(first), follow_redirects=True) as client:
        client.post(
            "/setup", data={"username": "admin", "password": PASSWORD, "confirm": PASSWORD}
        )
        csrf = _csrf(client)
        created = client.post(
            "/api/sources",
            json={
                "name": "main-qbt",
                "kind": "qbittorrent",
                "base_url": "http://qbt:8080",
                "username": "admin",
                "secret": "super-secret-password",
                "enabled": True,
            },
            headers={"X-CSRF-Token": csrf},
        )
        assert created.status_code == 200, created.text
        client.post(
            "/api/rules",
            json={
                "rules": [
                    {
                        "name": "Tag big torrents",
                        "enabled": True,
                        "cron": "0 4 * * *",
                        "condition_mode": "builder",
                        "condition_tree": {
                            "logic": "and",
                            "negate": False,
                            "conditions": [
                                {"field": "torrent.size_gb", "operator": "gt", "value": 20}
                            ],
                            "groups": [],
                        },
                        "actions": [{"type": "tag.sync", "params": {"tag": "big"}}],
                    }
                ]
            },
            headers={"X-CSRF-Token": csrf},
        )
        backup = client.get("/api/config/export?format=yaml").text

    assert "super-secret-password" not in backup

    # A new volume, a new encryption key: everything is gone.
    second = AppConfig(
        data_dir=tmp_path / "two",
        exports_dir=tmp_path / "exports",
        secret_key=generate_key(),
        testing=True,
    )
    with TestClient(create_app(second), follow_redirects=True) as restored:
        restored.post(
            "/setup", data={"username": "admin", "password": PASSWORD, "confirm": PASSWORD}
        )
        result = restored.post(
            "/api/config/import",
            json={"content": backup, "apply": True},
            headers={"X-CSRF-Token": _csrf(restored)},
        ).json()

        assert result["applied"] is True
        # The missing credential is called out rather than silently restored blank.
        assert any("credential" in w for w in result["warnings"])

        sources = restored.get("/api/sources").json()
        assert [s["name"] for s in sources] == ["main-qbt"]
        assert sources[0]["base_url"] == "http://qbt:8080"
        assert sources[0]["has_secret"] is False

        (rule,) = restored.get("/api/rules").json()
        assert rule["name"] == "Tag big torrents"
        assert rule["cron"] == "0 4 * * *"
        assert rule["actions"][0]["type"] == "tag.sync"
        assert rule["compile_valid"] is True
        # It was enabled when exported; it comes back disabled either way.
        assert rule["enabled"] is False


def test_the_engine_can_be_run_without_a_request_body(signed_in: TestClient) -> None:
    """"Run everything now" is the common case; it should not require posting `{}`."""
    response = signed_in.post("/api/engine/run", headers={"X-CSRF-Token": _csrf(signed_in)})

    assert response.status_code == 200, response.text
    assert response.json()["started"] is True


# --------------------------------------------------------------------------- #
# The rule editor's own page and its single-rule endpoints
# --------------------------------------------------------------------------- #


def _a_rule(name: str = "Big torrents", gb: int = 20) -> dict:
    return {
        "name": name,
        "enabled": False,
        "cron": "0 4 * * *",
        "condition_mode": "builder",
        "condition_tree": {
            "logic": "and",
            "negate": False,
            "conditions": [{"field": "torrent.size_gb", "operator": "gt", "value": gb}],
            "groups": [],
        },
        "actions": [{"type": "tag.sync", "params": {"tag": "big"}}],
    }


def test_the_editor_has_a_page_of_its_own(signed_in: TestClient) -> None:
    created = signed_in.post(
        "/api/rules/new", json=_a_rule(), headers={"X-CSRF-Token": _csrf(signed_in)}
    ).json()
    rule_id = created["rule"]["id"]

    page = signed_in.get(f"/rules/{rule_id}")
    assert page.status_code == 200
    # The name is in the title, so a browser tab and a bookmark are meaningful.
    assert "Big torrents" in page.text
    assert "x-data" in page.text

    assert signed_in.get("/rules/new").status_code == 200


def test_an_unknown_rule_id_returns_to_the_list(signed_in: TestClient) -> None:
    signed_in.follow_redirects = False
    response = signed_in.get("/rules/9999")
    assert response.status_code == 303
    assert response.headers["location"] == "/rules"


def test_saving_one_rule_leaves_the_others_alone(signed_in: TestClient) -> None:
    """The editor posts a single rule; the bulk endpoint would delete the rest."""
    csrf = _csrf(signed_in)
    head = {"X-CSRF-Token": csrf}
    first = signed_in.post("/api/rules/new", json=_a_rule("First"), headers=head)
    second = signed_in.post("/api/rules/new", json=_a_rule("Second"), headers=head)
    assert first.status_code == second.status_code == 200

    target = first.json()["rule"]
    target["name"] = "First, renamed"
    saved = signed_in.put(
        f"/api/rules/{target['id']}", json=target, headers={"X-CSRF-Token": csrf}
    )
    assert saved.status_code == 200
    assert saved.json()["rule"]["name"] == "First, renamed"

    names = [r["name"] for r in signed_in.get("/api/rules").json()]
    assert names == ["First, renamed", "Second"]


def test_a_new_rule_is_appended_and_compiled(signed_in: TestClient) -> None:
    csrf = _csrf(signed_in)
    signed_in.post("/api/rules/new", json=_a_rule("First"), headers={"X-CSRF-Token": csrf})
    created = signed_in.post(
        "/api/rules/new", json=_a_rule("Second"), headers={"X-CSRF-Token": csrf}
    ).json()["rule"]

    assert created["compile_valid"] is True
    assert "t.size_gb > ?" in created["compiled_sql"]
    # Appended, not inserted: order is what "evaluated top to bottom" means.
    assert created["order"] > signed_in.get("/api/rules").json()[0]["order"]


def test_deleting_one_rule_keeps_the_rest_and_closes_the_gap(signed_in: TestClient) -> None:
    csrf = _csrf(signed_in)
    ids = [
        signed_in.post(
            "/api/rules/new", json=_a_rule(name), headers={"X-CSRF-Token": csrf}
        ).json()["rule"]["id"]
        for name in ("One", "Two", "Three")
    ]

    deleted = signed_in.delete(f"/api/rules/{ids[0]}", headers={"X-CSRF-Token": csrf})
    assert deleted.status_code == 200

    remaining = signed_in.get("/api/rules").json()
    assert [r["name"] for r in remaining] == ["Two", "Three"]
    # Contiguous from zero, so the list and the evaluation order agree.
    assert [r["order"] for r in remaining] == [0, 1]

    assert signed_in.delete(
        f"/api/rules/{ids[0]}", headers={"X-CSRF-Token": csrf}
    ).status_code == 404


def test_a_too_fast_schedule_is_clamped_on_the_single_rule_path_too(
    signed_in: TestClient,
) -> None:
    rule = _a_rule()
    rule["cron"] = "* * * * *"
    result = signed_in.post(
        "/api/rules/new", json=rule, headers={"X-CSRF-Token": _csrf(signed_in)}
    ).json()

    assert result["rule"]["cron"] == "*/5 * * * *"
    assert result["warnings"], "the clamp was not explained"


def test_an_unknown_action_is_refused_on_the_single_rule_path(signed_in: TestClient) -> None:
    rule = _a_rule()
    rule["actions"] = [{"type": "tag.nope", "params": {}}]
    response = signed_in.post(
        "/api/rules/new", json=rule, headers={"X-CSRF-Token": _csrf(signed_in)}
    )
    assert response.status_code == 422
    assert "tag.nope" in response.text
