"""Licence inventory: what each component is licensed under, and what Forge's licence does NOT cover.

Plan: "Forge-owned code can use Apache-2.0; that label must not imply that Unity, every model,
third-party binary or generated output shares the same licence."

Sources: Forge itself; the installed Python distributions Forge depends on (read from package
metadata, not assumed); the toolchain found by preflight; the downloads manifest; and a fixed entry
for generated output, whose rights follow each asset's provenance record.
"""

from __future__ import annotations

import re
from dataclasses import asdict, dataclass
from importlib import metadata
from typing import Optional

from ..models import ToolchainManifest
from .downloads import DownloadsManifest

#: Python distributions Forge uses directly (required or optional).
FORGE_PY_DEPS = ("pydantic", "pydantic-core", "anthropic", "cryptography", "python-docx", "lxml", "objaverse",
                 "datasets", "pytest")

#: Toolchain licences as published by each project (the user's own terms still govern use).
TOOLCHAIN_LICENCES = {
    "git": ("GPL-2.0-only", "Applies to git itself."),
    "dotnet": ("MIT", ".NET SDK; NuGet packages carry their own licences."),
    "unity": ("Unity Editor Software Terms (proprietary)", "Requires an account and a plan that fits your revenue."),
    "adb": ("Android SDK License Agreement", "Platform tools are covered by the Android SDK terms."),
    "blender": ("GPL-2.0-or-later", "Covers Blender itself, not the files you create with it."),
    "python": ("PSF-2.0", ""),
}


@dataclass
class LicenceEntry:
    component: str
    version: str
    licence: str
    scope: str  # forge-code | python-dependency | toolchain | download | generated-output
    note: str = ""


def _dist_licence(dist: metadata.Distribution) -> str:
    md = dist.metadata
    expr = md.get("License-Expression")
    if expr:
        return expr
    classifiers = [c.split("::")[-1].strip() for c in md.get_all("Classifier") or [] if c.startswith("License ::")]
    if classifiers:
        return "; ".join(classifiers)
    lic = (md.get("License") or "").strip()
    if lic and len(lic) < 80 and "\n" not in lic:
        return lic
    return "UNKNOWN (see the package's licence file)"


def python_dependencies(names: tuple[str, ...] = FORGE_PY_DEPS) -> list[LicenceEntry]:
    out = []
    for n in names:
        try:
            d = metadata.distribution(n)
        except metadata.PackageNotFoundError:
            continue
        out.append(LicenceEntry(n, d.version, _dist_licence(d), "python-dependency"))
    return out


def inventory(*, toolchain: Optional[ToolchainManifest] = None, downloads: Optional[DownloadsManifest] = None,
              forge_version: str = "") -> list[LicenceEntry]:
    entries = [LicenceEntry("game-forge", forge_version, "Apache-2.0", "forge-code",
                            "Covers Forge-owned code only; not Unity, models, third-party binaries or generated output.")]
    entries += python_dependencies()
    if toolchain is not None:
        for name, t in sorted(toolchain.tools.items()):
            if not t.found:
                continue
            lic, note = TOOLCHAIN_LICENCES.get(name, ("UNKNOWN", ""))
            entries.append(LicenceEntry(name, t.version or "?", lic, "toolchain", note))
    if downloads is not None:
        for e in downloads.entries:
            entries.append(LicenceEntry(e.name, e.version, e.licence, "download",
                                        f"terms: {e.terms_url}" + ("; account required" if e.account_required else "")))
    entries.append(LicenceEntry("generated output (code, assets, builds)", "-", "Not covered by Forge's licence",
                                "generated-output", "Rights follow each asset's provenance record and the terms of the "
                                "provider/model/library that produced it."))
    return entries


def markdown(entries: list[LicenceEntry]) -> str:
    out = ["| Component | Version | Licence | Scope | Note |", "|---|---|---|---|---|"]
    for e in entries:
        out.append(f"| {e.component} | {e.version} | {re.sub(r'[|]', '/', e.licence)} | {e.scope} | {e.note} |")
    return "\n".join(out) + "\n"


def to_dicts(entries: list[LicenceEntry]) -> list[dict]:
    return [asdict(e) for e in entries]
