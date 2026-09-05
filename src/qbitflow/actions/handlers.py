"""The eleven action handlers.

Every one of them checks the torrent's current state before doing anything, so
running a rule twice changes nothing the second time, and a dry run reports
SKIPPED rather than WOULD_APPLY for torrents that are already correct.
"""

from __future__ import annotations

import asyncio
import os
import re
import shlex
import subprocess
from pathlib import Path
from typing import Any

import structlog
from pydantic import BaseModel, Field

from qbitflow.actions.base import (
    ActionContext,
    ActionDecision,
    ActionHandler,
    ActionIntent,
    register,
)
from qbitflow.domain import ActionOutcome

log = structlog.get_logger(__name__)


def _intent(ctx: ActionContext, op: str, *args: Any) -> ActionIntent:
    return ActionIntent(
        op=op,
        args=tuple(args),
        source_id=ctx.torrent.source_id,
        torrent_hash=ctx.torrent.hash,
    )


def _pending(ctx: ActionContext, intent: ActionIntent, message: str) -> ActionDecision:
    """Either a promise (dry run) or work to flush.

    The order matters: callers reach here only after their idempotency check, so
    a dry run never claims it would change something that is already right.
    """
    if ctx.dry_run:
        return ActionDecision(ActionOutcome.WOULD_APPLY, None, f"[dry-run] would {message}")
    return ActionDecision(ActionOutcome.APPLIED, intent, message)


# --------------------------------------------------------------------------- #
# Tags
# --------------------------------------------------------------------------- #


class TagParams(BaseModel):
    tag: str = Field(min_length=1, max_length=128, description="Tag name")


@register
class TagAddHandler(ActionHandler):
    type = "tag.add"
    display_name = "Add tag"
    description = "Adds a tag to matched torrents."
    params_model = TagParams

    def decide(self, ctx: ActionContext) -> ActionDecision:
        if ctx.match is not True:
            return ActionDecision(ActionOutcome.NOT_APPLICABLE)
        tag = str(ctx.params["tag"])
        if tag in ctx.torrent.tags:
            return ActionDecision(ActionOutcome.SKIPPED, None, f"already tagged '{tag}'")
        return _pending(ctx, _intent(ctx, "add_tags", tag), f"add tag '{tag}'")


@register
class TagRemoveHandler(ActionHandler):
    type = "tag.remove"
    display_name = "Remove tag"
    description = "Removes a tag from matched torrents."
    params_model = TagParams

    def decide(self, ctx: ActionContext) -> ActionDecision:
        if ctx.match is not True:
            return ActionDecision(ActionOutcome.NOT_APPLICABLE)
        tag = str(ctx.params["tag"])
        if tag not in ctx.torrent.tags:
            return ActionDecision(ActionOutcome.SKIPPED, None, f"tag '{tag}' not present")
        return _pending(ctx, _intent(ctx, "remove_tags", tag), f"remove tag '{tag}'")


@register
class TagSyncHandler(ActionHandler):
    type = "tag.sync"
    display_name = "Sync tag"
    description = (
        "Adds the tag to torrents the rule matches and removes it from torrents it "
        "no longer matches, so the tag always reflects the rule."
    )
    params_model = TagParams
    bidirectional = True

    def decide(self, ctx: ActionContext) -> ActionDecision:
        tag = str(ctx.params["tag"])
        has_tag = tag in ctx.torrent.tags

        if ctx.match is True:
            if has_tag:
                return ActionDecision(ActionOutcome.SKIPPED, None, f"already tagged '{tag}'")
            return _pending(ctx, _intent(ctx, "add_tags", tag), f"add tag '{tag}'")

        if ctx.match is False:
            if not has_tag:
                return ActionDecision(ActionOutcome.SKIPPED, None, f"tag '{tag}' not present")
            return _pending(
                ctx, _intent(ctx, "remove_tags", tag), f"remove tag '{tag}' (no longer matches)"
            )

        # match is None: the condition could not be evaluated, so do nothing.
        return ActionDecision(ActionOutcome.NOT_APPLICABLE)

    def inverse_predicate(self, params: dict[str, Any]) -> tuple[str, list[Any]] | None:
        """Only torrents that already carry the tag can need it removed.

        This keeps the reverse pass a targeted query instead of a walk over every
        torrent in the client.
        """
        tag = str(params.get("tag", "")).strip().lower()
        if not tag:
            return None
        return "t.tags LIKE ?", [f"%,{tag},%"]


# --------------------------------------------------------------------------- #
# Category
# --------------------------------------------------------------------------- #


class CategoryParams(BaseModel):
    category: str = Field(max_length=128, description="Category name; blank clears it")
    enable_auto_management: bool = Field(
        default=True,
        description=(
            "Turn on qBittorrent's automatic torrent management so it relocates the "
            "files into the category's save path."
        ),
    )


@register
class CategorySetHandler(ActionHandler):
    type = "category.set"
    display_name = "Set category"
    description = "Sets the qBittorrent category on matched torrents."
    params_model = CategoryParams

    def decide(self, ctx: ActionContext) -> ActionDecision:
        if ctx.match is not True:
            return ActionDecision(ActionOutcome.NOT_APPLICABLE)
        category = str(ctx.params.get("category", ""))
        if ctx.torrent.category == category:
            return ActionDecision(
                ActionOutcome.SKIPPED, None, f"already in category '{category}'"
            )
        auto = bool(ctx.params.get("enable_auto_management", True))
        return _pending(
            ctx, _intent(ctx, "set_category", category, auto), f"set category '{category}'"
        )


# --------------------------------------------------------------------------- #
# Location
# --------------------------------------------------------------------------- #


class MoveParams(BaseModel):
    path: str = Field(min_length=1, max_length=512, description="Destination directory")
    verify_path_exists: bool = Field(
        default=True,
        description=(
            "Check the directory exists before moving. This checks *this* container's "
            "filesystem, which is only meaningful when it sees the same paths as "
            "qBittorrent -- turn it off if they differ."
        ),
    )


@register
class TorrentMoveHandler(ActionHandler):
    type = "torrent.move"
    display_name = "Move torrent"
    description = "Changes a torrent's save location, moving its data."
    params_model = MoveParams

    def decide(self, ctx: ActionContext) -> ActionDecision:
        if ctx.match is not True:
            return ActionDecision(ActionOutcome.NOT_APPLICABLE)

        path = str(ctx.params["path"]).rstrip("/\\")
        current = ctx.torrent.save_path.rstrip("/\\")
        if current == path:
            return ActionDecision(ActionOutcome.SKIPPED, None, f"already saved to '{path}'")

        if ctx.params.get("verify_path_exists", True) and not os.path.isdir(path):
            # Advisory, not authoritative: qBittorrent's filesystem is the one
            # that matters, and it may not be this one.
            return ActionDecision(
                ActionOutcome.SKIPPED,
                None,
                f"'{path}' is not a directory in this container; skipped. "
                "Turn off 'verify path exists' if qBittorrent sees different paths.",
            )

        return _pending(ctx, _intent(ctx, "set_location", path), f"move to '{path}'")


# --------------------------------------------------------------------------- #
# Speed
# --------------------------------------------------------------------------- #


class SpeedParams(BaseModel):
    upload_kib: int = Field(
        default=-1,
        ge=-1,
        description="Upload limit in KiB/s. 0 is unlimited, -1 leaves it unchanged.",
    )
    download_kib: int = Field(
        default=-1,
        ge=-1,
        description="Download limit in KiB/s. 0 is unlimited, -1 leaves it unchanged.",
    )
    pause_when_both_zero: bool = Field(
        default=False,
        description=(
            "Legacy behaviour: treat both limits being zero as a request to pause. "
            "Off by default because 'unlimited' and 'paused' are not the same thing."
        ),
    )


@register
class SpeedLimitHandler(ActionHandler):
    type = "speed.limit"
    display_name = "Limit speed"
    description = "Sets per-torrent upload and download limits."
    params_model = SpeedParams

    def decide(self, ctx: ActionContext) -> ActionDecision:
        if ctx.match is not True:
            return ActionDecision(ActionOutcome.NOT_APPLICABLE)

        upload = int(ctx.params.get("upload_kib", -1))
        download = int(ctx.params.get("download_kib", -1))

        if upload == 0 and download == 0 and ctx.params.get("pause_when_both_zero"):
            if ctx.torrent.is_paused:
                return ActionDecision(ActionOutcome.SKIPPED, None, "already paused")
            return _pending(ctx, _intent(ctx, "stop"), "pause")

        # Each direction is decided on its own; -1 means "leave this one alone".
        if upload >= 0 and upload * 1024 != ctx.torrent.upload_limit:
            return _pending(
                ctx, _intent(ctx, "set_upload_limit", upload * 1024), f"set upload {upload} KiB/s"
            )
        if download >= 0 and download * 1024 != ctx.torrent.download_limit:
            return _pending(
                ctx,
                _intent(ctx, "set_download_limit", download * 1024),
                f"set download {download} KiB/s",
            )
        return ActionDecision(ActionOutcome.SKIPPED, None, "limits already as requested")


# --------------------------------------------------------------------------- #
# Seeding
# --------------------------------------------------------------------------- #


class NoParams(BaseModel):
    pass


@register
class SeedingStartHandler(ActionHandler):
    type = "seeding.start"
    display_name = "Start seeding"
    description = "Resumes paused torrents."
    params_model = NoParams

    def decide(self, ctx: ActionContext) -> ActionDecision:
        if ctx.match is not True:
            return ActionDecision(ActionOutcome.NOT_APPLICABLE)
        if not ctx.torrent.is_paused:
            return ActionDecision(ActionOutcome.SKIPPED, None, "already running")
        return _pending(ctx, _intent(ctx, "start"), "resume")


@register
class SeedingStopHandler(ActionHandler):
    type = "seeding.stop"
    display_name = "Stop seeding"
    description = "Pauses running torrents."
    params_model = NoParams

    def decide(self, ctx: ActionContext) -> ActionDecision:
        if ctx.match is not True:
            return ActionDecision(ActionOutcome.NOT_APPLICABLE)
        if ctx.torrent.is_paused:
            return ActionDecision(ActionOutcome.SKIPPED, None, "already paused")
        return _pending(ctx, _intent(ctx, "stop"), "pause")


class ForceStartParams(BaseModel):
    on: bool = Field(default=True, description="Whether force-start should be on")


@register
class SeedingForceStartHandler(ActionHandler):
    type = "seeding.forceStart"
    display_name = "Force start"
    description = "Turns qBittorrent's force-start flag on or off."
    params_model = ForceStartParams

    def decide(self, ctx: ActionContext) -> ActionDecision:
        if ctx.match is not True:
            return ActionDecision(ActionOutcome.NOT_APPLICABLE)
        wanted = bool(ctx.params.get("on", True))
        if ctx.torrent.force_start == wanted:
            return ActionDecision(
                ActionOutcome.SKIPPED, None, f"force-start already {'on' if wanted else 'off'}"
            )
        state = "on" if wanted else "off"
        return _pending(
            ctx, _intent(ctx, "set_force_start", wanted), f"set force-start {state}"
        )


# --------------------------------------------------------------------------- #
# Export
# --------------------------------------------------------------------------- #

_UNSAFE_FILENAME = re.compile(r'[<>:"/\\|?*\x00-\x1f]')


class ExportParams(BaseModel):
    folder: str = Field(
        default="", description="Destination folder; defaults to the exports volume"
    )


@register
class TorrentExportHandler(ActionHandler):
    type = "torrent.export"
    display_name = "Export .torrent"
    description = "Saves the .torrent file to disk."
    params_model = ExportParams
    direct = True

    def decide(self, ctx: ActionContext) -> ActionDecision:
        if ctx.match is not True:
            return ActionDecision(ActionOutcome.NOT_APPLICABLE)
        target = self._target(ctx)
        if target.exists():
            return ActionDecision(ActionOutcome.SKIPPED, None, f"'{target.name}' already exported")
        if ctx.dry_run:
            return ActionDecision(
                ActionOutcome.WOULD_APPLY, None, f"[dry-run] would export to '{target}'"
            )
        # Signals the runner to call perform(); the work is file I/O, not a
        # qBittorrent mutation, so it cannot be batched with anything.
        return ActionDecision(ActionOutcome.APPLIED, None, f"export to '{target}'")

    async def perform(self, ctx: ActionContext) -> ActionDecision:
        target = self._target(ctx)
        try:
            data = await ctx.client.export(ctx.torrent.hash)
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        except Exception as exc:  # noqa: BLE001 - reported per torrent
            # A partial file would make the next run skip an export that never
            # actually happened.
            target.unlink(missing_ok=True)
            return ActionDecision(ActionOutcome.ERROR, None, f"export failed: {exc}")
        return ActionDecision(ActionOutcome.APPLIED, None, f"exported to '{target}'")

    @staticmethod
    def _target(ctx: ActionContext) -> Path:
        folder = str(ctx.params.get("folder") or "").strip()
        base = Path(folder) if folder else Path(ctx.extras.get("exports_dir", "exports"))
        safe = _UNSAFE_FILENAME.sub("_", ctx.torrent.name)[:180] or ctx.torrent.hash
        return base / f"{safe}.torrent"


# --------------------------------------------------------------------------- #
# Script
# --------------------------------------------------------------------------- #


class ScriptParams(BaseModel):
    command: str = Field(
        min_length=1,
        description=(
            "Command line to run. Split with shell-style quoting and executed directly, "
            "never through a shell. Placeholders like {name} are substituted first."
        ),
    )
    working_dir: str = Field(default="", description="Directory to run in; must already exist")
    timeout_seconds: int = Field(
        default=300,
        ge=1,
        le=3600,
        description="Kill the command if it has not finished in this many seconds",
    )
    run_once_per_torrent: bool = Field(
        default=True,
        description="Record a marker so the command runs at most once per torrent per rule.",
    )


@register
class ScriptRunHandler(ActionHandler):
    type = "script.run"
    display_name = "Run script"
    description = (
        "Runs a command on the qbitflow host. Disabled unless "
        "QBITFLOW_ENABLE_SCRIPT_ACTION is set."
    )
    params_model = ScriptParams
    direct = True
    requires_opt_in = "QBITFLOW_ENABLE_SCRIPT_ACTION"

    def decide(self, ctx: ActionContext) -> ActionDecision:
        if ctx.match is not True:
            return ActionDecision(ActionOutcome.NOT_APPLICABLE)
        if not ctx.extras.get("script_enabled"):
            return ActionDecision(
                ActionOutcome.SKIPPED,
                None,
                "the script action is disabled; set QBITFLOW_ENABLE_SCRIPT_ACTION=1 to enable it",
            )
        if ctx.params.get("run_once_per_torrent", True) and ctx.extras.get("already_ran"):
            return ActionDecision(ActionOutcome.SKIPPED, None, "already run for this torrent")
        if ctx.dry_run:
            return ActionDecision(
                ActionOutcome.WOULD_APPLY, None, f"[dry-run] would run: {ctx.params['command']}"
            )
        return ActionDecision(ActionOutcome.APPLIED, None, "run command")

    async def perform(self, ctx: ActionContext) -> ActionDecision:
        command = str(ctx.params["command"])
        working_dir = str(ctx.params.get("working_dir") or "").strip()
        timeout = int(ctx.params.get("timeout_seconds", 300))

        if working_dir and not os.path.isdir(working_dir):
            # Never silently run somewhere other than where the user said.
            return ActionDecision(
                ActionOutcome.ERROR, None, f"working directory '{working_dir}' does not exist"
            )

        try:
            # posix=False on Windows so backslashes in paths survive splitting.
            argv = shlex.split(command, posix=os.name != "nt")
        except ValueError as exc:
            return ActionDecision(ActionOutcome.ERROR, None, f"could not parse command: {exc}")
        if not argv:
            return ActionDecision(ActionOutcome.ERROR, None, "command is empty")

        try:
            result = await asyncio.to_thread(
                subprocess.run,
                argv,
                # Never shell=True: these arguments interpolate a torrent name,
                # which comes from a tracker and is not ours to trust.
                shell=False,
                cwd=working_dir or None,
                capture_output=True,
                text=True,
                timeout=timeout,
                check=False,
            )
        except subprocess.TimeoutExpired:
            return ActionDecision(
                ActionOutcome.ERROR, None, f"command timed out after {timeout}s"
            )
        except OSError as exc:
            return ActionDecision(ActionOutcome.ERROR, None, f"could not run command: {exc}")

        if result.returncode != 0:
            tail = (result.stderr or result.stdout or "").strip()[-400:]
            return ActionDecision(
                ActionOutcome.ERROR, None, f"exited {result.returncode}: {tail}"
            )
        return ActionDecision(ActionOutcome.APPLIED, None, "command succeeded")
