"""Manual steps (Mixamo) and licensed libraries (stock animation packs, sound libraries).

Plan: "Manual browser-only services are labelled manual steps rather than presented as unattended API
adapters." :class:`ManualStepAdapter` writes exact instructions into an inbox directory and waits
until the owner drops the expected files there together with a ``done.json`` that records the
licence/terms. It is refused in unattended mode by route selection.

:class:`LicensedLibraryAdapter` imports items from a local, owner-purchased library described by a
manifest (``library.json``) that carries the licence for every file. It sends nothing anywhere.
"""

from __future__ import annotations

import json
import shutil
from pathlib import Path
from typing import Optional

from ..routes import ROUTES
from .base import AdapterError, AdapterUnavailable, GenerationAdapter, GenerationJob, JobStatus

MIXAMO_STEPS = """# Manual step: {title}

Asset: {asset}   Job: {job}   Operation: {operation}

1. Open https://www.mixamo.com in your browser and sign in with your own Adobe account.
2. Upload: {upload}
3. {instruction}
4. Download as **FBX Binary**, {fps} fps, **Without Skin** for animations / **With Skin** for the rig,
   and **In Place** where the clip list below says in_place.
5. Put the downloaded files into this folder with exactly these names:
{expected}
6. Create `done.json` in this folder:
   {{"by": "<your name>", "terms": "Adobe Mixamo terms", "terms_version": "<date you accepted>",
     "notes": "<anything unusual>"}}

Forge records this as a manual step in the asset's provenance. Do not rename bones in Mixamo; the
retarget mapping `retarget_mixamo_archer.json` expects the default `mixamorig:` names.
"""


class ManualStepAdapter(GenerationAdapter):
    def __init__(self, route_id: str, inbox_root: Path):
        self.route = ROUTES[route_id]
        if not self.route.manual_step:
            raise ValueError(f"{route_id} is not a manual route")
        self.inbox_root = Path(inbox_root)
        self.model = route_id

    def estimate_ceiling_micros(self, job: GenerationJob) -> Optional[int]:
        return 0  # paid (if at all) by the owner's own account outside Forge

    def inbox(self, provider_job_id: str) -> Path:
        return self.inbox_root / provider_job_id

    def submit(self, job: GenerationJob) -> str:
        jid = f"manual-{job.idempotency_key.replace(':', '-')}"
        box = self.inbox(jid)
        box.mkdir(parents=True, exist_ok=True)
        expected = list(job.params.get("expected_files") or [])
        if not expected:
            raise AdapterError("manual step needs expected_files")
        (box / "expected.json").write_text(json.dumps({"expected_files": expected, "asset": job.asset_id,
                                                       "operation": job.operation}, indent=2))
        if job.inputs.get("model") and Path(job.inputs["model"]).exists():
            shutil.copy2(job.inputs["model"], box / Path(job.inputs["model"]).name)
        (box / "INSTRUCTIONS.md").write_text(MIXAMO_STEPS.format(
            title=self.route.title, asset=job.asset_id, job=jid, operation=job.operation,
            upload=Path(job.inputs["model"]).name if job.inputs.get("model") else "(the rigged character)",
            instruction=job.params.get("instruction", "Apply the listed animations to the uploaded character."),
            fps=job.params.get("fps", 30), expected="\n".join(f"   - `{e}`" for e in expected)))
        return jid

    def poll(self, provider_job_id: str) -> JobStatus:
        box = self.inbox(provider_job_id)
        meta = json.loads((box / "expected.json").read_text()) if (box / "expected.json").exists() else {}
        expected = meta.get("expected_files") or []
        missing = [e for e in expected if not (box / e).exists()]
        done = box / "done.json"
        if missing or not done.exists():
            return JobStatus("manual_pending", provider_job_id,
                             error=f"waiting for the owner: missing {missing or ['done.json']} in {box}")
        try:
            confirm = json.loads(done.read_text())
        except json.JSONDecodeError:
            return JobStatus("failed", provider_job_id, error="done.json is not valid JSON")
        if not confirm.get("by") or not confirm.get("terms"):
            return JobStatus("failed", provider_job_id, error="done.json must record 'by' and 'terms'")
        return JobStatus("succeeded", provider_job_id, 1.0, "", {e: str(box / e) for e in expected}, cost_micros=0,
                         usage={"manual": True, "confirmed_by": confirm.get("by"), "terms": confirm.get("terms"),
                                "terms_version": confirm.get("terms_version", "")})

    def fetch(self, status: JobStatus, out_dir: Path) -> dict[str, Path]:
        out = {}
        for name, src in status.outputs.items():
            dest = out_dir / name
            shutil.copy2(src, dest)
            out[name] = dest
        return out

    def cancel(self, provider_job_id: str) -> str:
        return "confirmed"  # nothing external is running


class LicensedLibraryAdapter(GenerationAdapter):
    """Import from an owner-purchased library.

    ``library.json``::

        {"library": "Example SFX Pro", "licence": "Example SFX Pro single-seat licence",
         "licence_id": "INV-123", "url": "https://...", "attribution": "",
         "items": [{"file": "bow/draw_01.wav", "tags": ["draw", "bow"], "purpose": "draw"}]}
    """

    def __init__(self, route_id: str, library_root: Optional[str]):
        self.route = ROUTES[route_id]
        self.root = Path(library_root).expanduser() if library_root else None
        self.model = route_id
        self._jobs: dict[str, dict] = {}

    def manifest(self) -> dict:
        if self.root is None:
            raise AdapterUnavailable(f"{self.route.id}: no library configured ([assets.routes.{self.route.id}] path)")
        p = self.root / "library.json"
        if not p.exists():
            raise AdapterUnavailable(f"{self.route.id}: {p} missing (licence manifest required)")
        data = json.loads(p.read_text())
        if not data.get("licence"):
            raise AdapterUnavailable(f"{self.route.id}: library.json has no licence")
        return data

    def available(self) -> tuple[bool, str]:
        try:
            self.manifest()
        except AdapterUnavailable as e:
            return False, str(e)
        return True, ""

    def estimate_ceiling_micros(self, job: GenerationJob) -> Optional[int]:
        return 0  # already purchased; the purchase belongs in the cash ledger

    def submit(self, job: GenerationJob) -> str:
        m = self.manifest()
        want = set(job.params.get("tags") or [])
        purpose = job.params.get("purpose")
        items = [i for i in m.get("items", []) if (not purpose or i.get("purpose") == purpose)
                 and want <= set(i.get("tags") or [])]
        if not items:
            raise AdapterError(f"no library item matches purpose={purpose} tags={sorted(want)}")
        pick = sorted(items, key=lambda i: i["file"])[: int(job.params.get("count", 1))]
        jid = f"lib-{job.idempotency_key}"
        self._jobs[jid] = {"items": pick, "manifest": {k: m.get(k, "") for k in
                                                       ("library", "licence", "licence_id", "url", "attribution")}}
        return jid

    def poll(self, provider_job_id: str) -> JobStatus:
        j = self._jobs.get(provider_job_id)
        if j is None:
            return JobStatus("failed", provider_job_id, error="library job not held by this process")
        outs = {}
        for it in j["items"]:
            src = (self.root / it["file"]).resolve()
            if self.root.resolve() not in src.parents or not src.exists():
                return JobStatus("failed", provider_job_id, error=f"library file {it['file']} missing or outside")
            outs[Path(it["file"]).name] = str(src)
        return JobStatus("succeeded", provider_job_id, 1.0, "", outs, cost_micros=0, usage={"rights": j["manifest"]})

    def fetch(self, status: JobStatus, out_dir: Path) -> dict[str, Path]:
        out = {}
        for name, src in status.outputs.items():
            dest = out_dir / name
            shutil.copy2(src, dest)
            out[name] = dest
        return out

    def cancel(self, provider_job_id: str) -> str:
        return "confirmed"
