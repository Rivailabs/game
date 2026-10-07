"""Configurable price table: provider usage -> cost in micro-USD.

Defaults are Anthropic first-party list prices per million tokens as cached in
the claude-api reference on 2026-09-25. They are configuration, not truth:
override them in ``project.toml`` ``[pricing.<model>]`` when prices change.
Cache writes are billed at 1.25x input, cache reads at the listed read price.
"""

from __future__ import annotations

import math
from dataclasses import dataclass

from .base import Usage


@dataclass(frozen=True)
class ModelPrice:
    input_per_mtok: float
    output_per_mtok: float
    cache_read_per_mtok: float | None = None
    cache_write_per_mtok: float | None = None

    @property
    def cache_read(self) -> float:
        return self.cache_read_per_mtok if self.cache_read_per_mtok is not None else self.input_per_mtok * 0.1

    @property
    def cache_write(self) -> float:
        return self.cache_write_per_mtok if self.cache_write_per_mtok is not None else self.input_per_mtok * 1.25

    @property
    def max_input_rate(self) -> float:
        return max(self.input_per_mtok, self.cache_write, self.cache_read)


DEFAULT_PRICES: dict[str, ModelPrice] = {
    "claude-opus-5-5": ModelPrice(4.00, 20.00, 0.20),
    "claude-opus-5": ModelPrice(5.00, 25.00),
    "claude-opus-4-8": ModelPrice(5.00, 25.00),
    "claude-sonnet-5-5": ModelPrice(2.00, 10.00, 0.20),
    "claude-sonnet-5": ModelPrice(2.00, 10.00),
    "claude-haiku-4-5": ModelPrice(1.00, 5.00),
    "claude-fable-5-1": ModelPrice(10.00, 50.00, 0.25),
}


class UnknownModelPrice(Exception):
    pass


class PriceTable:
    def __init__(self, prices: dict[str, ModelPrice] | None = None):
        self.prices = dict(DEFAULT_PRICES)
        if prices:
            self.prices.update(prices)

    @classmethod
    def from_config(cls, cfg: dict) -> "PriceTable":
        extra = {
            model: ModelPrice(float(v["input_per_mtok"]), float(v["output_per_mtok"]),
                              v.get("cache_read_per_mtok"), v.get("cache_write_per_mtok"))
            for model, v in (cfg or {}).items()
        }
        return cls(extra)

    def get(self, model: str) -> ModelPrice:
        if model not in self.prices:
            raise UnknownModelPrice(f"no price configured for model {model}; refusing to estimate cost")
        return self.prices[model]

    def cost_micros(self, usage: Usage) -> int:
        p = self.get(usage.model)
        usd = (
            usage.input_tokens * p.input_per_mtok
            + usage.output_tokens * p.output_per_mtok
            + usage.cache_read_input_tokens * p.cache_read
            + usage.cache_creation_input_tokens * p.cache_write
        )  # USD * 1e6 / 1e6 tokens -> micros directly
        return math.ceil(usd)

    def ceiling_micros(self, model: str, *, input_tokens: int, output_tokens: int, requests: int = 1) -> int:
        p = self.get(model)
        return math.ceil(requests * (input_tokens * p.max_input_rate + output_tokens * p.output_per_mtok))
