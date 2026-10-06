"""R3 production lanes: UI, audio (licensed library + rights record), localization, balance/replay."""

from __future__ import annotations

import json
import math
import shutil
import struct
import wave
from pathlib import Path

import pytest

from forge.production.__main__ import main as lane_main
from forge.production.audio import analyse_wav, select, verify_rights, write_rights
from forge.production.balance import DotnetSimRunner, SimRun, Thresholds, parse_csv, run_balance, verify_report
from forge.production.localization import ReviewError, ReviewLedger, check_assets, check_project, parse_table
from forge.production.ui import check_screens

ROOT = Path(__file__).resolve().parent.parent
REPO = ROOT.parent
ASTRA_ASSETS = REPO / "astra-kingdoms" / "unity" / "Assets"
ASTRA_CSV = REPO / "astra-kingdoms" / "reports" / "bot-screening-AK-TR-1-n2000.csv"


# ------------------------------------------------------------------ UI


def _screens(**over):
    spec = {"min_touch_dp": 48, "start": "menu", "screens": [
        {"id": "menu", "elements": [{"id": "play", "type": "button", "text_key": "menu.play", "size_dp": [200, 56],
                                     "goes_to": "match"}]},
        {"id": "match", "elements": [{"id": "end", "type": "event", "goes_to": "results"}]},
        {"id": "results", "elements": [{"id": "back", "type": "button", "text_key": "common.back",
                                        "size_dp": [120, 48], "goes_to": "menu"}]},
    ]}
    spec.update(over)
    return spec


def test_ui_lane_passes_a_valid_spec_and_keeps_visual_checkpoint():
    rep = check_screens(_screens(), {"menu.play": "Play", "common.back": "Back"})
    assert rep.status == "PASS" and rep.exit_code == 0
    assert any("visual_approval" in c for c in rep.human_checkpoints)
    ev = rep.evidence_fields()
    assert ev["status"] == "PASS" and ev["name"] == "ui-lane"


def test_ui_lane_finds_touch_text_navigation_and_reachability_problems():
    spec = _screens()
    spec["screens"][0]["elements"].append({"id": "tiny", "type": "button", "text_key": "menu.play", "size_dp": [30, 30]})
    spec["screens"][0]["elements"].append({"id": "lit", "type": "label", "text": "Hello"})
    spec["screens"][2]["elements"].append({"id": "x", "type": "button", "text_key": "nope.key", "size_dp": [60, 60],
                                           "goes_to": "shop"})
    spec["screens"].append({"id": "orphan", "elements": []})
    rep = check_screens(spec, {"menu.play": "Play", "common.back": "Back"})
    codes = {f.code for f in rep.findings}
    assert {"touch_target", "hardcoded_text", "missing_text_key", "unknown_text_key", "bad_navigation",
            "unreachable"} <= codes
    assert rep.status == "FAIL" and rep.exit_code == 1


def test_ui_lane_dead_end_and_required_screens():
    spec = _screens()
    spec["screens"][2]["elements"] = [{"id": "w", "type": "label", "text_key": "common.back"}]
    rep = check_screens(spec, None)
    assert any(f.code == "dead_end" for f in rep.findings)
    rep2 = check_screens({"screens": [{"id": "menu", "elements": []}]}, None)
    assert {f.message for f in rep2.findings if f.code == "required_screen"} == {
        "template screen 'match' is missing", "template screen 'results' is missing"}


# ------------------------------------------------------------------ audio


def _wav(path: Path, *, seconds=0.5, amp=0.5, channels=1, rate=22050, clip=False):
    n = int(seconds * rate)
    with wave.open(str(path), "wb") as w:
        w.setnchannels(channels)
        w.setsampwidth(2)
        w.setframerate(rate)
        frames = bytearray()
        for i in range(n):
            v = 32767 if clip and i == 10 else int(amp * 32000 * math.sin(2 * math.pi * 440 * i / rate))
            frames += struct.pack("<h", v) * channels
        w.writeframes(bytes(frames))


def _library(tmp_path):
    from forge.production.audio import sha256_file

    _wav(tmp_path / "hit.wav")
    _wav(tmp_path / "win.wav", seconds=1.0, clip=True)
    _wav(tmp_path / "nc.wav")
    return {"name": "Test Library", "licence_doc": "LICENCES.txt", "clips": [
        {"id": "hit-1", "title": "Hit", "file": "hit.wav", "sha256": sha256_file(tmp_path / "hit.wav"),
         "licence": "CC0-1.0", "tags": ["hit", "impact"], "duration_s": 0.5, "source_url": "https://example.org/hit"},
        {"id": "win-1", "title": "Win", "file": "win.wav", "sha256": sha256_file(tmp_path / "win.wav"),
         "licence": "CC-BY-4.0", "attribution": "Jane Doe", "tags": ["win", "fanfare"], "duration_s": 1.0},
        {"id": "win-nc", "title": "Win NC", "file": "nc.wav", "licence": "CC-BY-NC-4.0", "attribution": "X",
         "tags": ["win", "fanfare", "extra"], "duration_s": 0.5},
        {"id": "draw-noattr", "file": "nc.wav", "licence": "CC-BY-4.0", "tags": ["draw"], "duration_s": 0.5},
        {"id": "nolicence", "file": "nc.wav", "tags": ["draw"], "duration_s": 0.5},
    ]}


BRIEFS = {"allowed_licences": ["CC0-1.0", "CC-BY-4.0"], "loudness_target_dbfs": -16, "briefs": [
    {"id": "hit", "purpose": "player loses health", "tags": ["hit"], "max_seconds": 1.0, "mono": True},
    {"id": "win", "purpose": "match won", "tags": ["win"], "max_seconds": 3.0, "mono": True},
    {"id": "draw", "purpose": "match drawn", "tags": ["draw"], "max_seconds": 2.0, "mono": True},
]}


def test_audio_selection_fails_closed_on_rights_and_writes_rights_record(tmp_path):
    rep, picks = select(BRIEFS, _library(tmp_path), library_dir=tmp_path, clock=lambda: 5.0)
    assert [p.clip_id for p in picks] == ["hit-1", "win-1"]  # the NC clip ranks higher but is never eligible
    rejected = rep.details["rejected_for_rights"]
    assert "not allowed" in rejected["win-nc"] and "attribution" in rejected["draw-noattr"]
    assert "missing" in rejected["nolicence"]
    assert any(f.code == "no_candidate" and f.where == "draw" for f in rep.findings)
    assert rep.status == "FAIL"
    win = picks[1]
    assert win.attribution == "Jane Doe" and win.status == "AWAITING_OWNER_APPROVAL"
    assert win.format["sample_rate"] == 22050 and win.format["channels"] == 1
    assert win.loudness["method"].startswith("RMS dBFS")
    assert any(f.code == "clipping" for f in rep.findings)
    out = write_rights(picks, tmp_path / "rights.json")
    ok = verify_rights(out, allowed=BRIEFS["allowed_licences"])
    assert ok.status == "PASS"
    (tmp_path / "hit.wav").write_bytes(b"RIFF tampered")
    bad = verify_rights(out, allowed=BRIEFS["allowed_licences"])
    assert bad.status == "FAIL" and any(f.code == "hash_mismatch" for f in bad.findings)


def test_audio_without_licence_policy_is_an_error(tmp_path):
    rep, picks = select({"briefs": [{"id": "a", "tags": ["x"]}]}, {"clips": []})
    assert rep.status == "FAIL" and picks == []


def test_wav_analysis(tmp_path):
    _wav(tmp_path / "s.wav", seconds=0.25, amp=0.5, channels=2)
    f = analyse_wav(tmp_path / "s.wav")
    assert f.channels == 2 and f.duration_s == 0.25 and f.clipped_samples == 0
    assert -7.0 < f.peak_dbfs < -5.5 and f.rms_dbfs < f.peak_dbfs


# ------------------------------------------------------------------ localization


def test_parse_table_reports_duplicates_and_malformed_lines():
    t = parse_table("# header\na = 1\nb = {0} x\na = 2\nbroken line\n")
    assert t.entries == {"a": "1", "b": "{0} x"} and len(t.errors) == 2 and t.header == ["header"]


def _tables(**langs):
    return {code: parse_table(text, code) for code, text in langs.items()}


def test_localization_key_checks():
    tables = _tables(en="menu.play = Play\nmatch.hp = HP {0}\nunused.k = Z\n",
                     hi="menu.play = खेलें\nmatch.hp = HP {1}\nextra.k = Q\n")
    sources = {"A.cs": 'x = Loc.Get("menu.play"); y = TF("match.hp", 3); z = T("missing.key");\n'
                       'label.text = "Hard coded words";'}
    rep = check_project(tables, sources)
    codes = {f.code for f in rep.findings}
    assert {"missing_key", "param_mismatch", "extra_key", "unused_key", "hardcoded_text"} <= codes
    assert rep.status == "FAIL"


def test_localization_review_workflow(tmp_path):
    t = [0.0]
    ledger = ReviewLedger(tmp_path / "review.jsonl", clock=lambda: t[0])
    en = parse_table("a.k = Play\nb.k = Back\n", "en")
    hi = parse_table("a.k = खेलें\nb.k = वापस\n", "hi")
    assert ledger.language_status("hi", en, hi) == {"counts": {"DRAFT": 2}, "release_ready": False}
    ledger.submit("hi", "a.k", "Play", "खेलें", by="translator", machine=True)
    with pytest.raises(ReviewError, match="own translation"):
        ledger.decide("hi", "a.k", "Play", "खेलें", reviewer="translator", approve=True)
    with pytest.raises(ReviewError, match="reason"):
        ledger.decide("hi", "a.k", "Play", "खेलें", reviewer="speaker", approve=False)
    ledger.decide("hi", "a.k", "Play", "खेलें", reviewer="speaker", approve=True)
    ledger.submit("hi", "b.k", "Back", "वापस", by="translator")
    ledger.decide("hi", "b.k", "Back", "वापस", reviewer="speaker", approve=True)
    assert ledger.language_status("hi", en, hi)["release_ready"] is True
    en2 = parse_table("a.k = Play now\nb.k = Back\n", "en")  # English changed: approval goes stale
    st = ledger.language_status("hi", en2, hi)
    assert st == {"counts": {"STALE": 1, "APPROVED": 1}, "release_ready": False}
    with pytest.raises(ReviewError, match="not in review"):
        ledger.decide("hi", "a.k", "Play now", "खेलें", reviewer="speaker", approve=True)


@pytest.mark.skipif(not ASTRA_ASSETS.exists(), reason="astra-kingdoms Unity assets not present")
def test_localization_lane_on_real_astra_assets():
    rep = check_assets(ASTRA_ASSETS)
    assert rep.status == "PASS", [f for f in rep.findings if f.severity == "error"]
    langs = rep.details["languages"]
    assert set(langs) == {"hi", "kn"}
    # Hindi and Kannada are marked for fluent-speaker review: not release-ready until reviewed.
    assert all(v["header_says_needs_review"] and not v["release_ready"] for v in langs.values())
    assert rep.details["keys_used_in_code"] > 100


# ------------------------------------------------------------------ balance


class FakeRunner:
    def __init__(self, csv_text: str, second: str | None = None, blocked: str = ""):
        self.texts = [csv_text, second if second is not None else csv_text]
        self.blocked = blocked
        self.calls = []

    def run(self, out_dir, name, *, matches, seed, catalog):
        self.calls.append((matches, seed, catalog))
        if self.blocked:
            return SimRun(127, None, None, blocked=self.blocked)
        out_dir.mkdir(parents=True, exist_ok=True)
        p = out_dir / f"{name}.csv"
        p.write_text(self.texts[len(self.calls) - 1])
        return SimRun(0, p, None)


CSV = ("metric,group,key,n,wins,losses,draws,win_share,wilson_lo,wilson_hi\n"
       "first_attacker,mirror policies,First attacker,1000,500,500,0,0.5,0.47,0.53\n"
       "weapon_duel,mirror,Fire,1000,700,300,0,0.70,0.67,0.73\n"
       "weapon_duel,mirror,Water,1000,520,480,0,0.52,0.49,0.55\n"
       "policy_pairing,all,Hard v Normal,300,100,200,0,0.33,0.28,0.39\n")


def test_balance_lane_screens_and_checks_replay_determinism(tmp_path):
    runner = FakeRunner(CSV)
    res = run_balance(runner, tmp_path, matches=100, seed=7, thresholds=Thresholds(max_share=0.6, requirement_id="R35"))
    assert runner.calls == [(100, 7, "full"), (100, 7, "full")]  # same seed twice
    rep = res.report
    assert rep.details["deterministic"] is True
    codes = [f.code for f in rep.findings]
    assert codes.count("outlier") == 1 and "difficulty_order" in codes
    assert any(f.code == "requirement_limit" and "R35" in f.message for f in rep.findings)
    assert rep.status == "FAIL" and "not balance proof" in rep.details["caveat"]


def test_balance_lane_nondeterminism_fails_and_missing_dotnet_blocks(tmp_path):
    rep = run_balance(FakeRunner(CSV, CSV.replace("0.53", "0.54")), tmp_path / "a").report
    assert rep.status == "FAIL" and any(f.code == "nondeterministic" for f in rep.findings)
    blocked = run_balance(FakeRunner(CSV, blocked="dotnet SDK not found"), tmp_path / "b").report
    assert blocked.status == "BLOCKED" and blocked.exit_code == 3
    missing = DotnetSimRunner(tmp_path / "nope.csproj", dotnet="definitely-not-dotnet").run(
        tmp_path, "x", matches=12, seed=1, catalog="full")
    assert missing.blocked


def test_balance_report_verification(tmp_path):
    rep = run_balance(FakeRunner(CSV.replace("Fire,1000,700,300,0,0.70,0.67,0.73", "Fire,1000,500,500,0,0.5,0.47,0.53")
                                 .replace("Hard v Normal,300,100,200,0,0.33,0.28,0.39",
                                          "Hard v Normal,300,200,100,0,0.66,0.61,0.71")), tmp_path).report
    assert rep.status == "PASS", rep.findings
    rep.write(tmp_path / "balance.json")
    assert verify_report(tmp_path / "balance.json").status == "PASS"
    assert verify_report(tmp_path / "missing.json").status == "FAIL"


@pytest.mark.skipif(not ASTRA_CSV.exists(), reason="Astra screening report not present")
def test_parse_real_astra_simulator_csv():
    rows = parse_csv(ASTRA_CSV.read_text())
    assert {r.metric for r in rows} >= {"first_attacker", "weapon_duel", "policy_pairing"}
    first = next(r for r in rows if r.metric == "first_attacker" and r.group == "mirror policies")
    assert first.n == 1002 and first.lo < 0.5 < first.hi


@pytest.mark.dotnet
@pytest.mark.skipif(shutil.which("dotnet") is None, reason="dotnet SDK not installed")
def test_balance_lane_with_real_astra_simulator(tmp_path):
    sim = REPO / "astra-kingdoms" / "tools" / "AstraKingdoms.Sim"
    res = run_balance(DotnetSimRunner(sim, threads=2), tmp_path, matches=12, seed=99)
    assert res.report.details["deterministic"] is True, res.report.findings
    assert res.rows and res.report.status in ("PASS", "FAIL")  # outliers at n=12 are owner flags, not errors


# ------------------------------------------------------------------ command-check entry point


def test_lane_cli_exit_codes(tmp_path, capsys):
    screens = tmp_path / "s.json"
    screens.write_text(json.dumps(_screens()))
    strings = tmp_path / "en.txt"
    strings.write_text("menu.play = Play\ncommon.back = Back\n")
    assert lane_main(["ui", str(screens), "--strings", str(strings), "--report-out", str(tmp_path / "r.json")]) == 0
    assert json.loads((tmp_path / "r.json").read_text())["status"] == "PASS"
    strings.write_text("menu.play = Play\n")
    assert lane_main(["ui", str(screens), "--strings", str(strings)]) == 1
    assert lane_main(["balance-verify", str(tmp_path / "none.json")]) == 1
    capsys.readouterr()
