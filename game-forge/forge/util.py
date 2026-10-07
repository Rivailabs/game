"""Small shared helpers: IDs, clocks, hashing, money units."""

from __future__ import annotations

import datetime as _dt
import hashlib
import secrets
import time
from typing import Protocol

#: All money inside Forge is stored as integer micro-units (1 USD = 1_000_000 micros)
#: so ledger arithmetic is exact and SQLite comparisons never round.
MICROS = 1_000_000


def usd_to_micros(amount: float | int | str) -> int:
    from decimal import Decimal, ROUND_CEILING

    return int((Decimal(str(amount)) * MICROS).to_integral_value(rounding=ROUND_CEILING))


def micros_to_usd(micros: int | None) -> str:
    if micros is None:
        return "-"
    sign = "-" if micros < 0 else ""
    micros = abs(micros)
    return f"{sign}${micros // MICROS:,}.{(micros % MICROS) // 100:04d}"


def new_id(prefix: str) -> str:
    return f"{prefix}_{secrets.token_hex(6)}"


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_text(text: str) -> str:
    return sha256_bytes(text.encode("utf-8"))


class Clock(Protocol):
    def now(self) -> float: ...

    def sleep(self, seconds: float) -> None: ...


class SystemClock:
    def now(self) -> float:
        return time.time()

    def sleep(self, seconds: float) -> None:
        time.sleep(seconds)


class FakeClock:
    """Deterministic clock for tests: ``sleep`` advances time instantly."""

    def __init__(self, start: float = 1_800_000_000.0):
        self.t = start
        self.slept: list[float] = []

    def now(self) -> float:
        return self.t

    def sleep(self, seconds: float) -> None:
        self.slept.append(seconds)
        self.t += seconds

    def advance(self, seconds: float) -> None:
        self.t += seconds


def iso(ts: float | None) -> str:
    if ts is None:
        return "-"
    return _dt.datetime.fromtimestamp(ts, tz=_dt.timezone.utc).strftime("%Y-%m-%d %H:%M:%SZ")
