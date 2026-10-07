"""Placeholder sound effects (V1 ticket 70): one procedurally synthesised one-shot per game cue.

The cue list mirrors ``AudioCue`` in ``unity/Assets/Scripts/Core/Audio/AudioCues.cs`` (except the
two music cues, see ``music.py``) plus five element-impact variants, so each element's hit is
recognisable by sound as well as by its shape and effect pattern. Every effect is mono, peak
normalised to -1 dBFS (no clipping) and faded at both ends.

These are stand-ins for the licensed library the plan requires ("Use a licensed sound library or
a rights-qualified audio provider"); they carry no third-party rights because nothing external is
used to make them.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Callable

import numpy as np

from .audio import (SAMPLE_RATE, env_adsr, fade_edges, highpass, lowpass, normalise, pluck, rng, sweep, t_axis,
                    write_wav)


@dataclass(frozen=True)
class SfxSpec:
    id: str            # file stem and ledger id suffix
    cue: str           # AudioCue enum name (or "Hit" for the element variants)
    purpose: str
    target_seconds: float
    make: Callable[[], np.ndarray]


def _n(s: float) -> int:
    return int(round(s * SAMPLE_RATE))


def _noise(seconds: float, seed: int) -> np.ndarray:
    return rng(seed).uniform(-1, 1, _n(seconds))


def ui_click() -> np.ndarray:
    s = 0.045
    x = 0.7 * np.sin(2 * np.pi * 1800 * t_axis(s)) + 0.3 * _noise(s, 1)
    return x * env_adsr(len(x), 0.001, 0.008, 0.0, 0.005)


def card_select() -> np.ndarray:
    a = sweep(660, 700, 0.06) * env_adsr(_n(0.06), 0.002, 0.03, 0.2, 0.01)
    b = sweep(990, 1040, 0.08) * env_adsr(_n(0.08), 0.002, 0.04, 0.2, 0.02)
    return np.concatenate([a, b])


def lock() -> np.ndarray:
    s = 0.16
    body = sweep(520, 260, s) * env_adsr(_n(s), 0.002, 0.04, 0.0, 0.02)
    snap = lowpass(_noise(s, 2), 3000) * env_adsr(_n(s), 0.001, 0.012, 0.0, 0.01)
    return body + 0.8 * snap


def draw() -> np.ndarray:
    s = 0.45
    creak = lowpass(_noise(s, 3), 900) * np.linspace(0.2, 1.0, _n(s))
    tone = sweep(120, 185, s) * 0.5
    return (creak + tone) * env_adsr(_n(s), 0.05, 1.0, 1.0, 0.06)


def release() -> np.ndarray:
    s = 0.32
    string = pluck(196, s, seed=4, damping=0.99)
    snap = highpass(_noise(s, 5), 2000) * env_adsr(_n(s), 0.001, 0.01, 0.0, 0.01)
    return string * env_adsr(_n(s), 0.001, 0.12, 0.0, 0.05) + 0.6 * snap


def _metal(s: float, base: float, seed: int) -> np.ndarray:
    t = t_axis(s)
    ratios = (1.0, 1.53, 2.31, 2.84, 3.6)
    x = sum(np.sin(2 * np.pi * base * r * t) * np.exp(-t * (8 + 4 * k)) / (k + 1) for k, r in enumerate(ratios))
    return x + 0.4 * highpass(_noise(s, seed), 2500) * np.exp(-t * 40)


def clash() -> np.ndarray:
    return _metal(0.36, 1200, 6)


def hit() -> np.ndarray:
    s = 0.26
    thud = sweep(160, 55, s) * env_adsr(_n(s), 0.002, 0.07, 0.0, 0.04)
    dust = lowpass(_noise(s, 7), 1200) * env_adsr(_n(s), 0.001, 0.04, 0.0, 0.03)
    return thud + 0.6 * dust


def miss() -> np.ndarray:
    s = 0.3
    t = t_axis(s)
    bump = np.sin(np.pi * t / s) ** 2
    return lowpass(highpass(_noise(s, 8), 600), 3500) * bump


def dodge() -> np.ndarray:
    s = 0.18
    t = t_axis(s)
    return highpass(_noise(s, 9), 1500) * np.sin(np.pi * t / s) + 0.3 * sweep(400, 900, s) * np.sin(np.pi * t / s)


def shield_block() -> np.ndarray:
    m = _metal(0.3, 700, 10)
    h = hit()
    n = min(len(m), len(h))
    m[:n] += 0.8 * h[:n]
    return m


def _notes(freqs: list[float], step: float, tail: float, seed: int, damping: float = 0.995) -> np.ndarray:
    total = _n(step * (len(freqs) - 1) + tail)
    out = np.zeros(total)
    for i, f in enumerate(freqs):
        p = pluck(f, tail, seed=seed + i, damping=damping) * env_adsr(_n(tail), 0.001, tail / 2, 0.0, 0.05)
        start = _n(step * i)
        out[start:start + len(p)] += p[: total - start]
    return out


# A pentatonic set (C D E G A) used by both the jingles and the music, so they sit together.
PENTA = [261.63, 293.66, 329.63, 392.00, 440.00, 523.25, 587.33, 659.25]


def land_transfer() -> np.ndarray:
    return _notes([PENTA[0], PENTA[2], PENTA[4]], 0.12, 0.5, seed=20)


def victory() -> np.ndarray:
    return _notes([PENTA[0], PENTA[2], PENTA[3], PENTA[5]], 0.18, 0.8, seed=30)


def defeat() -> np.ndarray:
    return _notes([PENTA[3], PENTA[2], 220.0], 0.26, 0.7, seed=40, damping=0.99)


def timer_warning() -> np.ndarray:
    beep = np.sin(2 * np.pi * 880 * t_axis(0.09)) * env_adsr(_n(0.09), 0.003, 1.0, 1.0, 0.02)
    return np.concatenate([beep, np.zeros(_n(0.12)), beep, np.zeros(_n(0.08))])


# Element impacts: each has its own *texture*, mirroring the visual motion language in brief 67.

def impact_agni() -> np.ndarray:  # crackle: sparse random bursts over a warm body
    s = 0.4
    g = rng(50)
    crack = np.zeros(_n(s))
    for _ in range(14):
        i = int(g.integers(0, _n(s) - 200))
        crack[i:i + 120] += g.uniform(-1, 1, 120) * np.exp(-np.arange(120) / 25)
    body = lowpass(_noise(s, 51), 700) * env_adsr(_n(s), 0.005, 0.15, 0.0, 0.05)
    return crack * np.linspace(1, 0.2, _n(s)) + body


def impact_vayu() -> np.ndarray:  # swirling whoosh: noise with a moving band
    s = 0.42
    t = t_axis(s)
    n = _noise(s, 52)
    swirl = lowpass(n, 1800) * (0.5 + 0.5 * np.sin(2 * np.pi * 9 * t)) * np.sin(np.pi * t / s)
    return swirl + 0.2 * sweep(300, 1200, s) * np.sin(np.pi * t / s)


def impact_prithvi() -> np.ndarray:  # heavy stone thud with rumble
    s = 0.5
    thud = sweep(110, 40, s) * env_adsr(_n(s), 0.002, 0.12, 0.0, 0.08)
    rumble = lowpass(_noise(s, 53), 250) * env_adsr(_n(s), 0.01, 0.2, 0.0, 0.1)
    grit = highpass(_noise(s, 54), 2500) * env_adsr(_n(s), 0.001, 0.02, 0.0, 0.02)
    return thud + 1.5 * rumble + 0.3 * grit


def impact_vidyut() -> np.ndarray:  # electric zap: buzzing square-ish sweeps
    s = 0.3
    t = t_axis(s)
    buzz = np.sign(np.sin(2 * np.pi * 110 * t)) * 0.4 + np.sign(sweep(2400, 600, s)) * 0.4
    return lowpass(buzz, 5000) * env_adsr(_n(s), 0.001, 0.06, 0.15, 0.06)


def impact_varuna() -> np.ndarray:  # splash: filtered noise plus bubble chirps
    s = 0.45
    splash = lowpass(_noise(s, 55), 2200) * env_adsr(_n(s), 0.003, 0.09, 0.0, 0.08)
    g = rng(56)
    bubbles = np.zeros(_n(s))
    for _ in range(6):
        i = int(g.integers(_n(0.05), _n(s) - _n(0.06)))
        f = float(g.uniform(500, 1100))
        b = sweep(f, f * 1.8, 0.05) * env_adsr(_n(0.05), 0.002, 0.02, 0.0, 0.01)
        bubbles[i:i + len(b)] += b
    return splash + 0.35 * bubbles


SFX: list[SfxSpec] = [
    SfxSpec("ui-click", "UiClick", "Button press", 0.045, ui_click),
    SfxSpec("card-select", "CardSelect", "Formation card selected", 0.14, card_select),
    SfxSpec("lock", "Lock", "Volley choice locked", 0.16, lock),
    SfxSpec("draw", "Draw", "Bow draw", 0.45, draw),
    SfxSpec("release", "Release", "Bow release", 0.32, release),
    SfxSpec("clash", "Clash", "Mid-air projectile clash", 0.36, clash),
    SfxSpec("hit", "Hit", "Generic body hit (element variants below)", 0.26, hit),
    SfxSpec("miss", "Miss", "Arrow misses and lands", 0.3, miss),
    SfxSpec("dodge", "Dodge", "Archer dodges", 0.18, dodge),
    SfxSpec("shield-block", "ShieldBlock", "Ash Shield blocks a contact", 0.3, shield_block),
    SfxSpec("land-transfer", "LandTransfer", "Cells transfer after a cut", 0.74, land_transfer),
    SfxSpec("victory", "Victory", "Match won", 1.34, victory),
    SfxSpec("defeat", "Defeat", "Match lost", 1.22, defeat),
    SfxSpec("timer-warning", "TimerWarning", "Selection clock running out", 0.38, timer_warning),
    SfxSpec("impact-agni", "Hit", "Agni impact: crackle", 0.4, impact_agni),
    SfxSpec("impact-vayu", "Hit", "Vayu impact: swirling whoosh", 0.42, impact_vayu),
    SfxSpec("impact-prithvi", "Hit", "Prithvi impact: stone thud and rumble", 0.5, impact_prithvi),
    SfxSpec("impact-vidyut", "Hit", "Vidyut impact: electric zap", 0.3, impact_vidyut),
    SfxSpec("impact-varuna", "Hit", "Varuna impact: splash and bubbles", 0.45, impact_varuna),
]


def render(spec: SfxSpec) -> np.ndarray:
    return normalise(fade_edges(spec.make()))


def generate(out_dir: Path) -> list[tuple[SfxSpec, Path]]:
    out = []
    for spec in SFX:
        path = out_dir / f"sfx-{spec.id}.wav"
        write_wav(path, render(spec))
        out.append((spec, path))
    return out
