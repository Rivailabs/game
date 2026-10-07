"""Shared audio helpers: deterministic synthesis primitives, WAV I/O and mix measurements.

All synthesis is plain numpy from fixed seeds (PCG64), written as 16-bit PCM WAV. Unity converts
these on import according to the presets in ``unity/Assets/Editor/Release/ArtImportPolicy.cs``
(effects: mono, decompress-on-load / compressed-in-memory; music: Vorbis, streaming), so the
source format is deliberately simple and lossless.
"""

from __future__ import annotations

import math
import wave
from dataclasses import dataclass
from pathlib import Path

import numpy as np

SAMPLE_RATE = 22050  # placeholder rate: half of 44.1 kHz keeps the repository small
PEAK_DBFS = -1.0     # normalisation ceiling: never clip at the approved mix


def t_axis(seconds: float, sr: int = SAMPLE_RATE) -> np.ndarray:
    return np.arange(int(round(seconds * sr)), dtype=np.float64) / sr


def rng(seed: int) -> np.random.Generator:
    return np.random.Generator(np.random.PCG64(seed))


def env_adsr(n: int, attack: float, decay: float, sustain: float, release: float, sr: int = SAMPLE_RATE) -> np.ndarray:
    """Linear attack, exponential decay to ``sustain``, linear release over the last ``release`` s."""
    t = np.arange(n) / sr
    e = np.where(t < attack, t / max(attack, 1e-6), sustain + (1 - sustain) * np.exp(-(t - attack) / max(decay, 1e-6)))
    r = int(release * sr)
    if r > 0:
        e[-r:] *= np.linspace(1.0, 0.0, r)
    return e


def sweep(f0: float, f1: float, seconds: float, sr: int = SAMPLE_RATE) -> np.ndarray:
    """Sine with an exponential frequency glide from f0 to f1."""
    n = int(round(seconds * sr))
    f = f0 * (f1 / f0) ** (np.arange(n) / max(n - 1, 1))
    phase = 2 * math.pi * np.cumsum(f) / sr
    return np.sin(phase)


def lowpass(x: np.ndarray, cutoff: float, sr: int = SAMPLE_RATE) -> np.ndarray:
    """One-pole low-pass (deterministic, no scipy)."""
    a = math.exp(-2 * math.pi * cutoff / sr)
    y = np.empty_like(x)
    acc = 0.0
    for i, v in enumerate(x):
        acc = (1 - a) * v + a * acc
        y[i] = acc
    return y


def highpass(x: np.ndarray, cutoff: float, sr: int = SAMPLE_RATE) -> np.ndarray:
    return x - lowpass(x, cutoff, sr)


def pluck(freq: float, seconds: float, seed: int, damping: float = 0.996, sr: int = SAMPLE_RATE) -> np.ndarray:
    """Karplus-Strong plucked string (a generic physical model, no samples)."""
    n = int(round(seconds * sr))
    period = max(2, int(round(sr / freq)))
    buf = rng(seed).uniform(-1, 1, period)
    out = np.empty(n)
    for i in range(n):
        j = i % period
        out[i] = buf[j]
        buf[j] = damping * 0.5 * (buf[j] + buf[(j + 1) % period])
    return out


def normalise(x: np.ndarray, peak_dbfs: float = PEAK_DBFS) -> np.ndarray:
    m = float(np.max(np.abs(x))) if x.size else 0.0
    return x if m == 0 else x * (10 ** (peak_dbfs / 20) / m)


def fade_edges(x: np.ndarray, ms: float = 4.0, sr: int = SAMPLE_RATE) -> np.ndarray:
    """Short fades so one-shots start and end at zero (no clicks)."""
    n = min(len(x) // 2, int(sr * ms / 1000))
    if n > 0:
        x = x.copy()
        x[:n] *= np.linspace(0, 1, n)
        x[-n:] *= np.linspace(1, 0, n)
    return x


def to_pcm16(x: np.ndarray) -> np.ndarray:
    return np.clip(np.round(x * 32767), -32768, 32767).astype("<i2")


def write_wav(path: Path, x: np.ndarray, sr: int = SAMPLE_RATE) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(sr)
        w.writeframes(to_pcm16(x).tobytes())


@dataclass
class WavInfo:
    channels: int
    sample_rate: int
    frames: int
    peak_dbfs: float
    rms_dbfs: float
    clipped_samples: int
    first: int
    last: int

    @property
    def seconds(self) -> float:
        return self.frames / self.sample_rate


def read_wav(path: Path) -> tuple[WavInfo, np.ndarray]:
    with wave.open(str(path), "rb") as w:
        ch, sw, sr, n = w.getnchannels(), w.getsampwidth(), w.getframerate(), w.getnframes()
        if sw != 2:
            raise ValueError(f"{path}: expected 16-bit PCM")
        data = np.frombuffer(w.readframes(n), dtype="<i2").astype(np.int32)
    mono = data[::ch] if ch > 1 else data
    peak = int(np.max(np.abs(mono))) if mono.size else 0
    rms = float(np.sqrt(np.mean((mono / 32768.0) ** 2))) if mono.size else 0.0
    info = WavInfo(ch, sr, n, 20 * math.log10(max(peak, 1) / 32768.0), 20 * math.log10(max(rms, 1e-9)),
                   int(np.sum(np.abs(mono) >= 32767)), int(mono[0]) if mono.size else 0, int(mono[-1]) if mono.size else 0)
    return info, mono
