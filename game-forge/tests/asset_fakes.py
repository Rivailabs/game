"""Fakes for the R2 asset tests: HTTP transport, nvidia-smi output, Blender and Unity runners."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any, Callable

from forge.assets.adapters.http import HttpResponse
from forge.checks.base import ProcResult

NVIDIA_SMI_TWO_GPUS = (
    "0, GPU-aaaa-1111, NVIDIA GeForce RTX 4070, 12282, 11800, 550.54.14, 8.9\n"
    "1, GPU-bbbb-2222, NVIDIA GeForce RTX 3060, 12288, 12000, 550.54.14, 8.6\n"
)
NVIDIA_SMI_4090 = "0, GPU-cccc-3333, NVIDIA GeForce RTX 4090, 24564, 23000, 550.54.14, 8.9\n"


class FakeTransport:
    """Scripted HTTP: ``routes`` maps (METHOD, url-prefix) -> response or callable(body) -> response."""

    def __init__(self, routes: dict[tuple[str, str], Any]):
        self.routes = routes
        self.calls: list[dict] = []

    def request(self, method, url, *, headers=None, json_body=None, data=None, timeout=60):
        self.calls.append({"method": method, "url": url, "headers": dict(headers or {}), "json": json_body,
                           "data": data})
        best = None
        for (m, prefix), resp in self.routes.items():
            if m == method and url.startswith(prefix) and (best is None or len(prefix) > len(best[0])):
                best = (prefix, resp)
        if best is None:
            return HttpResponse(404, b'{"message": "not found"}')
        resp = best[1]
        if callable(resp):
            resp = resp(json_body)
        if isinstance(resp, HttpResponse):
            return resp
        if isinstance(resp, (bytes, bytearray)):
            return HttpResponse(200, bytes(resp))
        return HttpResponse(200, json.dumps(resp).encode())


def sequence(*responses):
    """A callable route returning the given responses in order (last one repeats)."""
    items = list(responses)

    def f(_body):
        return items.pop(0) if len(items) > 1 else items[0]
    return f


def fake_smi(text: str) -> Callable:
    def run(argv, cwd, timeout, env=None):
        if argv[0] == "nvidia-smi":
            return ProcResult(0, text, "", 0.01)
        return ProcResult(1, "", "unexpected", 0.0)
    return run


# ------------------------------------------------------------------ Blender


def tech_report(triangles=7_200, *, bones=38, weights=4, materials=1, tex=1024, size=(0.6, 1.8, 0.4),
                topology="topo-1", skeleton_hash=None, unweighted=0) -> dict:
    rep = {
        "triangles": triangles, "vertices": triangles // 2, "units": "m", "up_axis": "+Y", "forward_axis": "+Z",
        "pivot": "bottom_center", "bounds": {"size": list(size)}, "has_normals": True, "uv_layers": 1,
        "uvs_in_unit_square": True, "topology_hash": topology,
        "materials": [{"name": f"mat{i}", "surface": "opaque", "textures": [f"base{i}"]} for i in range(materials)],
        "textures": [{"name": f"base{i}", "file": f"textures/base{i}.png", "role": "base_color", "width": tex,
                      "height": tex} for i in range(materials)],
        "deforming_bones": bones, "bones": bones + 5, "max_weights_per_vertex": weights,
        "weights_normalized": True, "unweighted_vertices": unweighted,
        "attachments": [], "blender_version": "4.2.0",
    }
    if skeleton_hash:
        rep["skeleton_hash"] = skeleton_hash
    return rep


class FakeBlender:
    """Stands in for ``blender --background --python <script> -- ...``: writes what the script would.

    ``reports`` maps script name -> report dict (or callable(args) -> dict); renders are tiny PNGs.
    """

    PNG = (b"\x89PNG\r\n\x1a\n" + b"\x00" * 64)

    def __init__(self, reports: dict[str, Any] | None = None, *, fail: set[str] | None = None):
        self.reports = reports or {}
        self.fail = fail or set()
        self.calls: list[list[str]] = []

    def __call__(self, argv, cwd, timeout, env=None):
        self.calls.append(list(argv))
        script = Path(argv[argv.index("--python") + 1]).stem
        args = argv[argv.index("--") + 1:]
        opts = {}
        i = 0
        while i < len(args):
            if args[i].startswith("--"):
                key = args[i][2:].replace("-", "_")
                if i + 1 < len(args) and not args[i + 1].startswith("--"):
                    opts[key] = args[i + 1]
                    i += 2
                    continue
                opts[key] = True
            i += 1
        if script in self.fail:
            return ProcResult(1, "", f"Error: {script} failed", 0.1)
        rep = self.reports.get(script, {})
        rep = rep(opts) if callable(rep) else dict(rep)
        out = Path(opts.get("output") or opts.get("out_dir") or cwd)
        if "output" in opts:
            out.parent.mkdir(parents=True, exist_ok=True)
            out.write_bytes(b"FBX-normalized " + script.encode() + json.dumps(rep).encode()[:64])
        renders = Path(opts["renders"]) if "renders" in opts else None
        if renders:
            renders.mkdir(parents=True, exist_ok=True)
            names = rep.get("render_names") or ["front", "side", "back", "three_quarter"]
            rep["renders"] = {}
            for n in names:
                (renders / f"{n}.png").write_bytes(self.PNG + n.encode())
                rep["renders"][n] = f"{n}.png"
        for t in rep.get("textures") or []:
            if "texture_dir" in opts:
                d = Path(opts["texture_dir"])
                d.mkdir(parents=True, exist_ok=True)
                (d / Path(t["file"]).name).write_bytes(self.PNG + t["name"].encode())
        Path(opts["report"]).parent.mkdir(parents=True, exist_ok=True)
        Path(opts["report"]).write_text(json.dumps(rep))
        return ProcResult(0, "Blender 4.2.0 (fake)", "", 0.2)
