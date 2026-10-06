"""Scaffolding generator for a supported template.

Copies ``templates/<id>/scaffold/`` into a new project directory, substituting the game's name,
ID and C# namespace, and writes ``forge-template.json``: the template version, which files belong
to which module, and the sha256 of every generated file (so a later template update can tell a
generated file from one the project has changed).
"""

from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass, field
from pathlib import Path

from .template import Template

RENAMES = {"gitignore.txt": ".gitignore"}
TEXT_SUFFIXES = {".cs", ".csproj", ".props", ".json", ".md", ".toml", ".txt", ".asmdef", ""}


class ScaffoldError(Exception):
    pass


@dataclass
class ScaffoldResult:
    out_dir: Path
    files: dict[str, str] = field(default_factory=dict)  # relative path -> sha256
    modules: dict[str, list[str]] = field(default_factory=dict)


def namespace_for(name: str) -> str:
    parts = re.findall(r"[A-Za-z0-9]+", name)
    ns = "".join(p[:1].upper() + p[1:] for p in parts)
    if not ns or ns[0].isdigit():
        ns = "Game" + ns
    return ns


def game_id_for(name: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", name.lower()).strip("-") or "game"


def generate(template: Template, out_dir: str | Path, *, game_name: str, game_id: str | None = None,
             namespace: str | None = None, overwrite: bool = False) -> ScaffoldResult:
    src = template.root / "scaffold"
    if not src.is_dir():
        raise ScaffoldError(f"template {template.id} has no scaffold directory")
    for m in template.modules:
        for rel in m.scaffold:
            if not (src / rel).exists():
                raise ScaffoldError(f"template module {m.id} lists missing scaffold file {rel}")
    out = Path(out_dir)
    if out.exists() and any(out.iterdir()) and not overwrite:
        raise ScaffoldError(f"{out} is not empty; refusing to overwrite an existing project")
    subs = {"__GAME_NAME__": game_name, "__GAME_ID__": game_id or game_id_for(game_name),
            "__NAMESPACE__": namespace or namespace_for(game_name), "__TEMPLATE_VERSION__": template.version}
    if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_.]*", subs["__NAMESPACE__"]):
        raise ScaffoldError(f"invalid C# namespace {subs['__NAMESPACE__']!r}")
    res = ScaffoldResult(out_dir=out)
    for path in sorted(p for p in src.rglob("*") if p.is_file()):
        rel = path.relative_to(src).as_posix()
        target_rel = "/".join(RENAMES.get(part, part) for part in rel.split("/"))
        data = path.read_bytes()
        if path.suffix in TEXT_SUFFIXES:
            text = data.decode("utf-8")
            for k, v in subs.items():
                text = text.replace(k, v)
            data = text.encode("utf-8")
        dest = out / target_rel
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_bytes(data)
        res.files[target_rel] = hashlib.sha256(data).hexdigest()
    for m in template.modules:
        res.modules[m.id] = [RENAMES.get(r, r) for r in m.scaffold]
    manifest = {"template": template.id, "template_version": template.version, "game_name": game_name,
                "game_id": subs["__GAME_ID__"], "namespace": subs["__NAMESPACE__"], "modules": res.modules,
                "files": res.files}
    (out / "forge-template.json").write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n")
    return res
