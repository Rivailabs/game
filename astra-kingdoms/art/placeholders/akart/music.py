"""Two placeholder music loops (V1 ticket 71): menu and match.

The plan sets "two music tracks" as a V1 content target and warns against building an
audio-generation lane before the game is playable. These loops are therefore deliberately simple
procedural stand-ins (a drone, a plucked pentatonic line and, for the match loop, a soft pulse)
that let the music channel, the independent music volume, streaming import and loop boundaries be
tested. They are not candidates for the shipped soundtrack.

Seamless looping is built in rather than edited afterwards: every note is rendered into a circular
buffer, so a tail that runs past the loop end continues at the loop start. The first and last
samples therefore belong to one continuous waveform, which ``loop_seam_jump`` measures.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

import numpy as np

from .audio import SAMPLE_RATE, lowpass, normalise, pluck, rng, write_wav
from .sfx import PENTA


@dataclass(frozen=True)
class MusicSpec:
    id: str
    cue: str
    purpose: str
    bpm: int
    bars: int
    seed: int
    pulse: bool

    @property
    def seconds(self) -> float:
        return self.bars * 4 * 60.0 / self.bpm


MUSIC = [
    MusicSpec("music-menu", "MusicMenu", "Menu loop: calm drone and slow plucked line", 80, 8, 101, False),
    MusicSpec("music-match", "MusicMatch", "Match loop: drone, plucked line and a soft pulse (sits under effects)", 96, 8, 202, True),
]


ATTACK = int(0.004 * SAMPLE_RATE)


def _add_circular(buf: np.ndarray, start: int, x: np.ndarray) -> None:
    n = len(buf)
    for off in range(0, len(x), n):
        chunk = x[off:off + n]
        s = (start + off) % n
        first = min(len(chunk), n - s)
        buf[s:s + first] += chunk[:first]
        if first < len(chunk):
            buf[:len(chunk) - first] += chunk[first:]


def render(spec: MusicSpec) -> np.ndarray:
    sr = SAMPLE_RATE
    beat = 60.0 / spec.bpm
    n = int(round(spec.seconds * sr))
    t = np.arange(n) / sr
    buf = np.zeros(n)

    # Drone on the tonic and fifth. Frequencies are snapped to whole cycles per loop so the drone
    # itself is periodic over the loop length.
    def periodic(f: float) -> float:
        return round(f * spec.seconds) / spec.seconds

    for f, a in ((PENTA[0] / 2, 0.35), (PENTA[3] / 2, 0.2), (PENTA[0], 0.12)):
        buf += a * np.sin(2 * np.pi * periodic(f) * t)
    # Slow swell, also periodic (one cycle per two bars).
    buf *= 0.8 + 0.2 * np.sin(2 * np.pi * t / (beat * 8))

    g = rng(spec.seed)
    steps = spec.bars * (4 if not spec.pulse else 8)
    step = spec.seconds / steps
    idx = 2
    for k in range(steps):
        if g.uniform() < (0.45 if not spec.pulse else 0.55):
            idx = int(np.clip(idx + g.integers(-2, 3), 0, len(PENTA) - 1))
            note = pluck(PENTA[idx], 1.6, seed=spec.seed * 1000 + k, damping=0.997) * 0.5
            note[:ATTACK] *= np.linspace(0.0, 1.0, ATTACK)  # soft 4 ms attack: no onset click
            _add_circular(buf, int(round(k * step * sr)), note)
    if spec.pulse:
        thump_len = int(0.25 * sr)
        tt = np.arange(thump_len) / sr
        thump = np.sin(2 * np.pi * 70 * tt * (1 - tt)) * np.exp(-tt * 18)
        for b in range(spec.bars * 4):
            _add_circular(buf, int(round(b * beat * sr)), thump * (0.7 if b % 4 == 0 else 0.4))
        shaker = lowpass(rng(spec.seed + 1).uniform(-1, 1, int(0.06 * sr)), 6000) * np.exp(-np.arange(int(0.06 * sr)) / (0.012 * sr))
        for b in range(spec.bars * 8):
            if b % 2 == 1:
                _add_circular(buf, int(round(b * beat / 2 * sr)), 0.15 * shaker)
    return normalise(buf, -3.0)  # music sits lower than effects


def loop_seam_jump(x: np.ndarray) -> float:
    """|last - first| relative to the typical sample-to-sample step: ~1 means seamless."""
    steps = np.abs(np.diff(x))
    typical = float(np.percentile(steps, 99)) or 1.0
    return float(abs(x[-1] - x[0]) / typical)


def generate(out_dir: Path) -> list[tuple[MusicSpec, Path]]:
    out = []
    for spec in MUSIC:
        path = out_dir / f"{spec.id}.wav"
        write_wav(path, render(spec))
        out.append((spec, path))
    return out
