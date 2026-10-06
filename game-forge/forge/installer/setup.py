"""``forge setup``: the setup assistant (owner workflow step 1, "Prepare").

1. **Prerequisites**: runs preflight and reports every item with its real status (see
   :mod:`forge.installer.prereqs`); nothing is hidden or assumed.
2. **Downloads and terms**: lists the versioned downloads manifest: manual steps that need an
   account or a licence decision, and checksum-verified automatic items.
3. **Sample minimal build first**: generates the supported template's sample project into the data
   directory and runs its rules tests with the real .NET SDK, *before* any customer project is
   imported. No dotnet -> BLOCKED with the reason, never a pass.
4. **Summary**: what this machine is ready for (rules loop, Android build, device evidence) and
   what is still missing; written to ``<data_dir>/setup-report.json``.

No game data is uploaded anywhere during setup (no provider is called).
"""

from __future__ import annotations

import json
import shutil
import subprocess
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, Optional

from ..credentials import scrubbed_env
from ..models import ToolchainManifest
from ..planning.scaffold import generate
from ..planning.template import load_template
from .downloads import DownloadsManifest
from .prereqs import PrereqReport, assess


@dataclass
class SampleBuild:
    status: str  # PASS | FAIL | BLOCKED
    detail: str
    path: Optional[str] = None
    duration_s: float = 0.0
    log_tail: str = ""


@dataclass
class SetupResult:
    prereqs: PrereqReport
    downloads: Optional[DownloadsManifest]
    sample: SampleBuild
    notes: list[str] = field(default_factory=list)

    def to_dict(self) -> dict:
        return {"prerequisites": self.prereqs.to_dict(),
                "manual_steps": [e.name for e in (self.downloads.manual_steps() if self.downloads else [])],
                "sample_build": self.sample.__dict__, "notes": self.notes}

    def markdown(self) -> str:
        out = ["# Forge setup report", "", "## Prerequisites", "", self.prereqs.markdown()]
        if self.downloads:
            out += ["## Manual steps (account or licence decision needed)", ""]
            for e in self.downloads.manual_steps():
                acct = " - needs an account" if e.account_required else ""
                out.append(f"- **{e.name}** {e.version}: {e.licence}{acct}. {e.note} Terms: {e.terms_url}")
            out.append("")
        out += ["## Sample minimal build", "", f"**{self.sample.status}**: {self.sample.detail}", ""]
        out += [f"- {n}" for n in self.notes]
        return "\n".join(out) + "\n"


def run_sample_build(data_dir: str | Path, *, template_id: str = "turn-duel-2p", dotnet: str = "dotnet",
                     timeout_s: float = 900, clock: Callable[[], float] = time.time) -> SampleBuild:
    exe = shutil.which(dotnet)
    if exe is None:
        return SampleBuild("BLOCKED", "the .NET SDK is not installed, so the template's rules library cannot be built")
    target = Path(data_dir) / "sample" / f"{template_id}-{int(clock())}"
    generate(load_template(template_id), target, game_name="Forge Sample Duel")
    start = clock()
    try:
        p = subprocess.run([exe, "test", "tests/Rules.Tests"], cwd=target, capture_output=True, text=True,
                           timeout=timeout_s, env=scrubbed_env())
    except subprocess.TimeoutExpired:
        return SampleBuild("FAIL", f"sample build timed out after {timeout_s:g}s", str(target))
    tail = (p.stdout + p.stderr)[-1500:]
    if p.returncode == 0 and "Passed!" in p.stdout:
        return SampleBuild("PASS", "template rules library built and its template checks passed (no Unity, no phone: "
                                   "this is not a playable Android build)", str(target), round(clock() - start, 1), tail)
    return SampleBuild("FAIL", f"dotnet test exited {p.returncode}", str(target), round(clock() - start, 1), tail)


def run_setup(data_dir: str | Path, *, manifest: Optional[ToolchainManifest] = None,
              preflight: Optional[Callable[[], ToolchainManifest]] = None,
              provider_credentials: Optional[dict[str, bool]] = None,
              sample: Optional[Callable[[], SampleBuild]] = None, skip_sample: bool = False) -> SetupResult:
    data_dir = Path(data_dir)
    if manifest is None:
        from ..preflight import run_preflight, write_manifest

        manifest = (preflight or (lambda: run_preflight(data_dir, provider_credentials=provider_credentials)))()
        write_manifest(manifest, data_dir)
    prereqs = assess(manifest, provider_credentials=provider_credentials)
    try:
        downloads = DownloadsManifest.load()
    except Exception as e:  # noqa: BLE001 - report, do not hide
        downloads = None
        notes = [f"downloads manifest could not be read: {e}"]
    else:
        notes = []
    if skip_sample:
        sb = SampleBuild("BLOCKED", "skipped at the owner's request; run `forge setup` again before importing a project")
    else:
        sb = (sample or (lambda: run_sample_build(data_dir)))()
    ready = prereqs.to_dict()["ready"]
    if not ready["android_build"]:
        notes.append("Android builds will report BLOCKED until Unity, its activation and the Android module are in place.")
    if not ready["device_evidence"]:
        notes.append("Device evidence will report INCOMPLETE until a physical phone is connected and authorised.")
    if sb.status != "PASS":
        notes.append("Do not import a full project until the sample build passes.")
    res = SetupResult(prereqs, downloads, sb, notes)
    data_dir.mkdir(parents=True, exist_ok=True)
    (data_dir / "setup-report.json").write_text(json.dumps(res.to_dict(), indent=2, default=str))
    return res
