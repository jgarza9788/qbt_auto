"""The generated reference must match the code that generates it.

A field reference that advertises a field the compiler cannot emit is worse than
no reference at all, so the documentation is derived rather than written -- and
this is what stops it going stale. If it fails, run:

    uv run python scripts/gen_docs.py
"""

from __future__ import annotations

import importlib.util
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
DOCS = ROOT / "docs"


def _generator():
    spec = importlib.util.spec_from_file_location("gen_docs", ROOT / "scripts" / "gen_docs.py")
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    sys.modules["gen_docs"] = module
    spec.loader.exec_module(module)
    return module


@pytest.mark.parametrize("name", ["fields.md", "actions.md", "snapshot-schema.md"])
def test_the_generated_reference_is_up_to_date(name: str) -> None:
    expected = _generator().render()[name]
    actual = (DOCS / name).read_text(encoding="utf-8")
    assert actual == expected, (
        f"docs/{name} is stale -- run: uv run python scripts/gen_docs.py"
    )


def test_every_field_is_documented_with_a_description_and_an_example() -> None:
    """An undocumented field is one the field panel cannot explain to a user."""
    from qbitflow.conditions.registry import get_registry

    undocumented = [
        key
        for key, definition in get_registry().fields.items()
        if not definition.description.strip() or not definition.sample.strip()
    ]
    assert not undocumented, f"fields missing a description or example: {undocumented}"


def test_every_action_parameter_is_documented() -> None:
    from qbitflow.actions import action_schemas

    missing: list[str] = []
    for schema in action_schemas():
        for name, spec in ((schema.get("params") or {}).get("properties") or {}).items():
            if not str(spec.get("description", "")).strip():
                missing.append(f"{schema['type']}.{name}")
    assert not missing, f"action parameters with no description: {missing}"
