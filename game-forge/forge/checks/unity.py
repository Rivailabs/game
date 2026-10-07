"""Unity batch-mode build adapter (Android).

The editor path comes only from the toolchain manifest (frozen during preflight).
Absent editor -> BLOCKED with a clear reason; Forge never fakes a build pass.
One Unity build per workspace is enforced by the scheduler's workspace lease.
"""

from __future__ import annotations

from pathlib import Path

from ..models import EvidenceClass, EvidenceStatus
from .base import Check, CheckContext, CheckOutcome, run_proc


class UnityBuildCheck(Check):
    evidence_class = EvidenceClass.INTEGRATION
    requires = ("unity",)

    def __init__(self, name: str = "unity-android-build", *, project_dir: str = "unity",
                 build_method: str = "Forge.Build.BuildAndroid", output: str = "Builds/Android/game.apk",
                 timeout_s: float = 3600):
        self.name, self.project_dir, self.build_method = name, project_dir, build_method
        self.output, self.timeout_s = output, timeout_s

    def _editor(self, ctx: CheckContext) -> tuple[str | None, str]:
        m = ctx.manifest
        if m is None:
            return None, "no toolchain manifest; run `forge preflight` first"
        if not m.unity_editor_path:
            return None, "Unity editor not recorded in the toolchain manifest (not installed or not frozen)"
        if not Path(m.unity_editor_path).exists():
            return None, f"Unity editor path {m.unity_editor_path} does not exist on this worker"
        return m.unity_editor_path, ""

    def availability(self, ctx: CheckContext) -> tuple[bool, str]:
        editor, reason = self._editor(ctx)
        return editor is not None, reason

    def run(self, workdir: Path, ctx: CheckContext) -> CheckOutcome:
        editor, reason = self._editor(ctx)
        details = {"project_dir": self.project_dir, "build_method": self.build_method}
        if editor is None:
            return CheckOutcome(self.name, self.evidence_class, EvidenceStatus.BLOCKED, reason, details)
        proj = workdir / self.project_dir
        if not proj.exists():
            return CheckOutcome(self.name, self.evidence_class, EvidenceStatus.BLOCKED,
                                f"Unity project directory {self.project_dir} does not exist", details)
        log = proj / "Logs" / "forge-build.log"
        log.parent.mkdir(parents=True, exist_ok=True)
        r = run_proc([editor, "-batchmode", "-nographics", "-quit", "-projectPath", str(proj),
                      "-buildTarget", "Android", "-executeMethod", self.build_method, "-logFile", str(log)],
                     workdir, self.timeout_s)
        logs = {"unity-build.log": log.read_text(errors="replace") if log.exists() else r.stdout + r.stderr}
        out = proj / self.output
        details.update({"exit_code": r.returncode, "duration_s": round(r.duration_s, 1),
                        "output": str(out.relative_to(workdir)) if out.exists() else None,
                        "editor": editor, "editor_version": ctx.manifest.tools.get("unity").version
                        if ctx.manifest and ctx.manifest.tools.get("unity") else None})
        if r.timed_out:
            return CheckOutcome(self.name, self.evidence_class, EvidenceStatus.FAIL, "Unity build timed out", details, logs)
        if r.returncode != 0 or not out.exists():
            details["failure_category"] = "compile_error"
            return CheckOutcome(self.name, self.evidence_class, EvidenceStatus.FAIL,
                                f"Unity build failed (exit {r.returncode})", details, logs)
        return CheckOutcome(self.name, self.evidence_class, EvidenceStatus.PASS, "Android build produced", details, logs)
