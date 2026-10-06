"""Checks of the committed placeholder assets, their generators and the placeholder ledger.

Run: ``python -m pytest astra-kingdoms/art/tests -q`` (needs numpy and Pillow).
"""

from __future__ import annotations

import itertools
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

import numpy as np
import pytest
from PIL import Image

AK = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(AK / "art" / "placeholders"))

from akart import icons, ledger, music, sfx  # noqa: E402
from akart.audio import read_wav  # noqa: E402

UNITY = AK / "unity"
LEDGER = AK / "art" / "ledger" / "placeholder-assets.ledger.json"
GAME_LEDGER = UNITY / "Assets" / "Resources" / "Ledger" / "asset-ledger.json"
AUDIO_CUES = UNITY / "Assets" / "Scripts" / "Core" / "Audio" / "AudioCues.cs"


@pytest.fixture(scope="module")
def placeholder_ledger() -> dict:
    return json.loads(LEDGER.read_text(encoding="ascii"))


def unity_file(entry: dict) -> Path:
    assert entry["path"].startswith("generated:")
    return UNITY / entry["path"][len("generated:"):]


# ---- icons ---------------------------------------------------------------------------------

def test_forty_unique_icons_in_the_briefed_categories():
    ids = [i.id for i in icons.ICONS]
    assert len(ids) == 40 and len(set(ids)) == 40
    cats = {c: sum(1 for i in icons.ICONS if i.category == c) for c in {i.category for i in icons.ICONS}}
    assert cats == {"element": 6, "terrain": 5, "card": 6, "status": 5, "dodge": 4, "ui": 14}


def test_icon_list_matches_brief_69():
    brief = (AK / "art" / "briefs" / "69-ui-store-icons.md").read_text(encoding="utf-8")
    listed = set(re.findall(r"`((?:element|terrain|card|status|dodge|ui)-[a-z-]+)`", brief))
    assert listed == {i.id for i in icons.ICONS}


def test_element_silhouettes_are_distinct_without_colour():
    elements = [i for i in icons.ICONS if i.category == "element"]
    for a, b in itertools.combinations(elements, 2):
        d = icons.silhouette_distinctness(a, b)
        assert d >= 0.35, f"{a.id} vs {b.id}: silhouettes too similar (1-IoU {d:.2f})"


@pytest.mark.parametrize("icon", icons.ICONS, ids=lambda i: i.id)
def test_glyph_contrast_meets_non_text_minimum(icon):
    # WCAG 2.x non-text contrast is 3:1; we hold the placeholders to 4.5:1 so small sizes stay legible.
    assert icons.contrast_ratio(icons.GLYPH, icon.tile) >= 4.5


@pytest.mark.parametrize("icon", icons.ICONS, ids=lambda i: i.id)
def test_committed_icon_png_and_svg(icon):
    png = UNITY / "Assets" / "Art" / "Placeholder" / "Icons" / f"{icon.id}.png"
    with Image.open(png) as im:
        assert im.size == (128, 128) and im.mode == "RGBA"
    svg = AK / "art" / "placeholders" / "svg" / f"{icon.id}.svg"
    root = ET.fromstring(svg.read_text(encoding="ascii"))
    assert root.tag.endswith("svg") and len(list(root)) >= 2


def test_rasteriser_draws_some_ink_for_every_icon():
    for icon in icons.ICONS:
        ink = sum(icons.glyph_mask(icon, 32))
        assert 25 < ink < 32 * 32 * 0.8, icon.id


# ---- sound effects -------------------------------------------------------------------------

def audio_cue_names() -> set[str]:
    text = AUDIO_CUES.read_text(encoding="utf-8")
    body = text[text.index("enum AudioCue"):]
    body = body[body.index("{") + 1:body.index("}")]
    return set(re.findall(r"(\w+)\s*=", body))


def test_every_non_music_cue_has_an_effect():
    cues = audio_cue_names()
    music_cues = {c for c in cues if c.startswith("Music")}
    assert {s.cue for s in sfx.SFX} == cues - music_cues
    assert {m.cue for m in music.MUSIC} == music_cues


@pytest.mark.parametrize("spec", sfx.SFX, ids=lambda s: s.id)
def test_committed_effect_is_mono_unclipped_and_click_free(spec):
    info, _ = read_wav(UNITY / "Assets" / "Art" / "Placeholder" / "Audio" / "Sfx" / f"sfx-{spec.id}.wav")
    assert info.channels == 1 and info.sample_rate == 22050
    assert info.clipped_samples == 0 and info.peak_dbfs <= -0.9
    assert abs(info.first) < 200 and abs(info.last) < 200
    assert abs(info.seconds - spec.target_seconds) < 0.02
    assert info.rms_dbfs > -40, "audible"


def _spectrum(x: np.ndarray) -> np.ndarray:
    s = np.abs(np.fft.rfft(x.astype(np.float64), 8192))
    bands = np.add.reduceat(s, np.geomspace(1, len(s) - 1, 40).astype(int))
    return bands / (np.linalg.norm(bands) or 1)


def test_effects_are_spectrally_distinct():
    spectra = {s.id: _spectrum(read_wav(UNITY / "Assets" / "Art" / "Placeholder" / "Audio" / "Sfx" / f"sfx-{s.id}.wav")[1])
               for s in sfx.SFX}
    for a, b in itertools.combinations(spectra, 2):
        sim = float(np.dot(spectra[a], spectra[b]))
        assert sim < 0.995, f"{a} and {b} sound alike (spectral similarity {sim:.3f})"


# ---- music ---------------------------------------------------------------------------------

@pytest.mark.parametrize("spec", music.MUSIC, ids=lambda s: s.id)
def test_music_loop_is_seamless_and_sized(spec):
    info, x = read_wav(UNITY / "Assets" / "Art" / "Placeholder" / "Audio" / "Music" / f"{spec.id}.wav")
    assert abs(info.seconds - spec.seconds) < 0.001
    assert info.peak_dbfs <= -2.9 and info.clipped_samples == 0
    assert music.loop_seam_jump(x / 32768.0) <= 1.5


def test_circular_render_wraps_tails():
    buf = np.zeros(10)
    music._add_circular(buf, 8, np.ones(5))
    assert buf.tolist() == [1, 1, 1, 0, 0, 0, 0, 0, 1, 1]


# ---- ledger --------------------------------------------------------------------------------

def test_ledger_rows_hash_the_committed_bytes(placeholder_ledger):
    assert placeholder_ledger["format"] == "AK-ASSET-LEDGER/1"
    entries = placeholder_ledger["entries"]
    assert len(entries) == len(icons.ICONS) + len(sfx.SFX) + len(music.MUSIC)
    for e in entries:
        assert e["status"] == "Placeholder" and e["purpose"] and e["licence"] and e["provenance"]
        assert ledger.sha256(unity_file(e)) == e["sha256"], e["id"]


def test_ledger_ids_do_not_clash_with_the_game_ledger(placeholder_ledger):
    game = json.loads(GAME_LEDGER.read_text(encoding="ascii"))
    ours = {e["id"] for e in placeholder_ledger["entries"]}
    assert len(ours) == len(placeholder_ledger["entries"])
    assert not ours & {e["id"] for e in game["entries"]}


def test_every_generated_file_is_in_the_ledger(placeholder_ledger):
    listed = {unity_file(e).resolve() for e in placeholder_ledger["entries"]}
    on_disk = {p.resolve() for p in (UNITY / "Assets" / "Art" / "Placeholder").rglob("*") if p.suffix in (".png", ".wav")}
    assert on_disk == listed


def test_ledger_is_ascii():
    LEDGER.read_bytes().decode("ascii")
