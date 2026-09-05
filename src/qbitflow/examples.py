"""A starter library of rules.

Every one arrives disabled and in the visual builder's own format, so it can be
opened, read and adjusted rather than taken on trust. They are also the clearest
documentation of what the field set can express.
"""

from __future__ import annotations

from typing import Any


def _cond(field: str, operator: str, value: Any = None, qualifier: str | None = None) -> dict:
    condition: dict[str, Any] = {"field": field, "operator": operator, "value": value}
    if qualifier:
        condition["qualifier"] = qualifier
    return condition


def _group(*conditions: dict, logic: str = "and", groups: list | None = None) -> dict:
    return {
        "logic": logic,
        "negate": False,
        "conditions": list(conditions),
        "groups": groups or [],
    }


EXAMPLE_RULES: list[dict[str, Any]] = [
    {
        "name": "Tag media nobody has watched in 90 days",
        "description": (
            "Marks completed torrents whose library item has had no play from any "
            "source in three months. The tag is synced, so it comes off again as "
            "soon as someone watches it."
        ),
        "enabled": False,
        "cron": "0 4 * * *",
        "condition_mode": "builder",
        "condition_tree": _group(
            _cond("torrent.is_complete", "is_true"),
            _cond("media.is_matched", "is_true"),
            groups=[
                _group(
                    _cond("watch.min_days_since_played", "gt", 90),
                    # Never watched at all also counts as "not watched in 90 days".
                    _cond("watch.min_days_since_played", "is_null"),
                    logic="or",
                )
            ],
        ),
        "actions": [{"type": "tag.sync", "params": {"tag": "unwatched-90d"}}],
    },
    {
        "name": "Throttle upload on bulk category",
        "description": "Caps upload on a low-priority category so it does not crowd out the rest.",
        "enabled": False,
        "cron": "*/30 * * * *",
        "condition_mode": "builder",
        "condition_tree": _group(_cond("torrent.category", "eq", "Bulk")),
        "actions": [
            {"type": "speed.limit", "params": {"upload_kib": 512, "download_kib": -1}}
        ],
    },
    {
        "name": "Move cold, unwatched media to slower storage",
        "description": (
            "Completed, well-seeded, unwatched for six months, and only when the fast "
            "drive is actually under pressure. Adjust the destination before enabling."
        ),
        "enabled": False,
        "cron": "0 3 * * 0",
        "condition_mode": "builder",
        "condition_tree": _group(
            _cond("torrent.is_complete", "is_true"),
            _cond("torrent.ratio", "gte", 1.0),
            _cond("watch.min_days_since_played", "gt", 180),
            _cond("storage.used_percent", "gt", 85, qualifier="downloads"),
        ),
        "actions": [
            {
                "type": "torrent.move",
                "params": {"path": "/mnt/cold/{category}", "verify_path_exists": True},
            }
        ],
    },
    {
        "name": "Categorise matched films",
        "description": "Files anything matched to a film in the library under Movies.",
        "enabled": False,
        "cron": "*/30 * * * *",
        "condition_mode": "builder",
        "condition_tree": _group(
            _cond("media.media_type", "eq", "movie"),
            _cond("torrent.category", "neq", "Movies"),
            _cond("torrent.match_confidence", "gte", 0.8),
        ),
        "actions": [
            {
                "type": "category.set",
                "params": {"category": "Movies", "enable_auto_management": True},
            }
        ],
    },
    {
        "name": "Flag torrents on a nearly full disk",
        "description": (
            "Tags whatever lives on a drive above 92%, using the mount the torrent is "
            "actually on -- so the rule works on any machine without naming a path."
        ),
        "enabled": False,
        "cron": "*/15 * * * *",
        "condition_mode": "builder",
        "condition_tree": _group(_cond("torrent.mount_used_percent", "gt", 92)),
        "actions": [{"type": "tag.sync", "params": {"tag": "disk-pressure"}}],
    },
    {
        "name": "Stop seeding once the ratio target is met",
        "description": "Pauses torrents past 2.0 that have been seeding for at least a week.",
        "enabled": False,
        "cron": "0 */6 * * *",
        "condition_mode": "builder",
        "condition_tree": _group(
            _cond("torrent.ratio", "gte", 2.0),
            _cond("torrent.seeding_time_days", "gte", 7),
            _cond("torrent.is_paused", "is_false"),
        ),
        "actions": [{"type": "seeding.stop", "params": {}}],
    },
    {
        "name": "Tag highly rated films",
        "description": (
            "Anything rated 8 or above in the library, for a quick filter in qBittorrent."
        ),
        "enabled": False,
        "cron": "0 5 * * *",
        "condition_mode": "builder",
        "condition_tree": _group(
            _cond("media.rating", "gte", 8.0),
            _cond("media.media_type", "eq", "movie"),
        ),
        "actions": [{"type": "tag.sync", "params": {"tag": "highly-rated"}}],
    },
    {
        "name": "Remove the stale tag once something is watched",
        "description": (
            "Only needed if you tag with tag.add rather than tag.sync -- a synced tag "
            "already removes itself."
        ),
        "enabled": False,
        "cron": "0 * * * *",
        "condition_mode": "builder",
        "condition_tree": _group(
            _cond("torrent.tags", "contains", "unwatched-90d"),
            _cond("watch.min_days_since_played", "lte", 90),
        ),
        "actions": [{"type": "tag.remove", "params": {"tag": "unwatched-90d"}}],
    },
    {
        "name": "Force start small, poorly seeded torrents",
        "description": "Gives anything under 2 GB with few seeds a better chance of finishing.",
        "enabled": False,
        "cron": "*/20 * * * *",
        "condition_mode": "builder",
        "condition_tree": _group(
            _cond("torrent.is_complete", "is_false"),
            _cond("torrent.size_gb", "lt", 2),
            _cond("torrent.num_seeds", "lte", 2),
        ),
        "actions": [{"type": "seeding.forceStart", "params": {"on": True}}],
    },
    {
        "name": "Advanced: torrents whose library item was never matched",
        "description": (
            "A raw-SQL example. Finds completed torrents with no library match at all, "
            "which usually means a naming mismatch worth looking at."
        ),
        "enabled": False,
        "cron": "0 6 * * *",
        "condition_mode": "raw_sql",
        "condition_tree": None,
        "raw_sql": "t.is_complete = 1 AND t.media_item_id IS NULL AND t.size_gb > 1",
        "actions": [{"type": "tag.sync", "params": {"tag": "unmatched"}}],
    },
]


def example_payload() -> dict[str, Any]:
    """Shaped so it can be pasted straight into the import box."""
    return {
        "version": 1,
        "rules": [
            {
                "name": rule["name"],
                "description": rule["description"],
                "enabled": False,
                "cron": rule["cron"],
                "condition_mode": rule["condition_mode"],
                "condition_tree": rule.get("condition_tree"),
                "raw_sql": rule.get("raw_sql"),
                "target_source_ids": [],
                "timezone": None,
                "dry_run": None,
                "stop_on_match": None,
                "cooldown_seconds": None,
                "actions": rule["actions"],
            }
            for rule in EXAMPLE_RULES
        ],
    }
