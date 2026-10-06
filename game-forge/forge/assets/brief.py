"""Asset production briefs (``<id>.asset.json``, next to the catalogue brief of the same id).

Plan: "Every asset starts with a brief containing intended screen size, art reference, silhouette,
scale, materials, attachment points, animation requirements, target budgets and licence
requirements. For the archer, keep character, bow, arrow and procedural bowstring as separate
assets."
"""

from __future__ import annotations

import hashlib
import re
from pathlib import Path
from typing import Literal, Optional

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator

from .budgets import AssetBudget, budget_for
from .clips import REQUIRED_ARCHER_CLIPS
from .lane import FULL_LANE_KINDS, PROP_SKIPPABLE, Stage

_ID = re.compile(r"^[a-z0-9_]{1,64}$")


class ArtReference(BaseModel):
    model_config = ConfigDict(extra="forbid")

    description: str
    path: str = ""  # repository-relative file, if any
    uri: str = ""
    rights: str  # e.g. "owner-created sketch", "licensed: <source>", "public domain"


class LicenceRequirements(BaseModel):
    model_config = ConfigDict(extra="forbid")

    commercial_use: bool = True
    public_demo: bool = False  # assets may appear in public demos / marketing
    attribution_allowed: bool = True  # CC-BY style attribution acceptable
    notes: str = ""


class AssetBrief(BaseModel):
    model_config = ConfigDict(extra="forbid")

    id: str
    kind: Literal["character", "weapon", "projectile", "prop", "arena", "effect", "audio"]
    title: str
    intended_screen_size: str  # e.g. "about 18% of screen height at the gameplay camera"
    art_reference: list[ArtReference]
    silhouette: str
    scale_m: float
    scale_axis: Literal["height", "largest"] = "largest"
    pivot: Literal["bottom_center", "center", "grip", "nock"] = "bottom_center"
    materials: str
    attachment_points: list[str] = Field(default_factory=list)
    animation_requirements: list[str] = Field(default_factory=list)
    budget: Optional[str] = None  # key into the plan's budget table (archer, bow, arrow, arena)
    max_triangles: Optional[int] = None  # props (not in the plan table) state their own ceiling
    max_texture_px: Optional[int] = None
    max_materials: Optional[int] = None
    collider: Literal["none", "box", "capsule", "sphere", "convex_mesh", "mesh"] = "box"
    licence: LicenceRequirements = Field(default_factory=LicenceRequirements)
    prompt: str
    negative_prompt: str = ""
    clip_prompts: dict[str, str] = Field(default_factory=dict)
    catalogue_first: bool = True
    skip_stages: list[Stage] = Field(default_factory=list)
    separate_assets: list[str] = Field(default_factory=list)  # e.g. bow, arrow for the archer
    procedural_parts: list[str] = Field(default_factory=list)  # e.g. bowstring: never generated

    @field_validator("id")
    @classmethod
    def _id(cls, v: str) -> str:
        if not _ID.match(v):
            raise ValueError("id must be lower-case letters, digits and underscores (max 64)")
        return v

    @model_validator(mode="after")
    def _complete(self) -> "AssetBrief":
        missing = [k for k in ("intended_screen_size", "silhouette", "materials", "prompt") if not getattr(self, k)]
        if not self.art_reference:
            missing.append("art_reference")
        if self.scale_m <= 0:
            missing.append("scale_m")
        if self.budget is None and self.max_triangles is None:
            missing.append("budget or max_triangles")
        if missing:
            raise ValueError(f"brief incomplete: {missing}")
        lane_kind = "character" if self.kind == "character" else self.kind
        if lane_kind in FULL_LANE_KINDS and self.skip_stages:
            raise ValueError("a character runs the full lane (no skipped stages)")
        bad = [s.value for s in self.skip_stages if s not in PROP_SKIPPABLE]
        if bad:
            raise ValueError(f"stages {bad} cannot be skipped: the asset contract remains")
        if self.kind == "character":
            missing_clips = [c for c in REQUIRED_ARCHER_CLIPS if c not in self.animation_requirements]
            if self.budget == "archer" and missing_clips:
                raise ValueError(f"archer brief must require the full clip set; missing {missing_clips}")
            if not self.attachment_points:
                raise ValueError("a character brief needs named attachment points")
        return self

    def asset_budget(self) -> AssetBudget:
        return budget_for(self.budget, max_triangles=self.max_triangles, max_texture_px=self.max_texture_px,
                          max_materials=self.max_materials)

    @property
    def rigged(self) -> bool:
        return Stage.RIG not in self.skip_stages

    @property
    def lane_kind(self) -> str:
        return self.kind


def brief_path_for(catalogue_brief: Path) -> Path:
    return catalogue_brief.with_name(catalogue_brief.stem + ".asset.json")


def load_asset_brief(path: Path) -> tuple[AssetBrief, str]:
    raw = Path(path).read_bytes()
    return AssetBrief.model_validate_json(raw), hashlib.sha256(raw).hexdigest()
