"""The art briefs carry every plan field (art/tools/check_briefs.py)."""

from __future__ import annotations

import sys
from pathlib import Path

ART = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ART / "tools"))

import check_briefs  # noqa: E402


def test_committed_briefs_are_complete():
    assert check_briefs.check() == []


def test_missing_field_is_reported(tmp_path):
    (tmp_path / "65-x.md").write_text("### AK-ART-065-A Thing\n\n| Field | Brief |\n| --- | --- |\n| Use | yes |\n")
    problems = check_briefs.check(tmp_path)
    assert any("missing 'Silhouette'" in p for p in problems)
    assert any("ticket 66: no brief section" in p for p in problems)


def test_weapon_count_is_enforced(tmp_path):
    (tmp_path / "67-x.md").write_text("| 1 | A |\n| 2 | B |\n")
    assert any("weapon identities" in p for p in check_briefs.check(tmp_path))
