"""Privacy controls: telemetry off by default, opt-in operational counts only; redacted,
previewable diagnostic export.

Plan: "Product telemetry is off by default. Optional metrics are limited to operational counts and
timings with no prompts, code, images, audio or video. Diagnostic sharing is separate, previewable
and redacted." and "Code snippets, logs and video receive the same classification as the project
they reveal. Debug logs are not assumed harmless."

Telemetry here never sends anything by itself: :meth:`Telemetry.send` needs an explicit sender and
the owner's opt-in. Diagnostics contain environment facts, versions, event *types* and states with
redacted reasons; never event payload text, prompts, code, logs or media. The owner sees the exact
bytes first; :func:`export_diagnostics` refuses unless the preview digest is confirmed.
"""

from __future__ import annotations

import hashlib
import io
import json
import os
import platform
import re
import secrets
import time
import zipfile
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable

from ..credentials import redact

#: Metric name -> kind. Anything else is rejected, not silently dropped into a payload.
ALLOWED_METRICS = {
    "setup_completed": "count", "sample_build_passed": "count", "tasks_started": "count",
    "tasks_accepted": "count", "tasks_failed": "count", "checks_run": "count", "updates_applied": "count",
    "check_duration_s": "timing", "build_duration_s": "timing", "time_to_first_build_s": "timing",
}


class TelemetryRejected(Exception):
    pass


class PrivacySettings:
    """Stored in ``<data_dir>/privacy.json``. Absent file = telemetry off."""

    def __init__(self, data_dir: str | Path):
        self.path = Path(data_dir) / "privacy.json"

    def load(self) -> dict:
        if self.path.exists():
            return json.loads(self.path.read_text())
        return {"telemetry_enabled": False, "install_id": None}

    def set_telemetry(self, enabled: bool, *, by: str) -> dict:
        s = self.load()
        s["telemetry_enabled"] = bool(enabled)
        s["changed_by"], s["changed_at"] = by, time.time()
        if enabled and not s.get("install_id"):
            s["install_id"] = secrets.token_hex(8)  # random; not derived from the user, machine or email
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.path.write_text(json.dumps(s, indent=2, sort_keys=True))
        return s


class Telemetry:
    def __init__(self, data_dir: str | Path):
        self.settings = PrivacySettings(data_dir)
        self.queue = Path(data_dir) / "telemetry-queue.json"

    @property
    def enabled(self) -> bool:
        return bool(self.settings.load().get("telemetry_enabled"))

    def record(self, name: str, value: float = 1) -> bool:
        if name not in ALLOWED_METRICS:
            raise TelemetryRejected(f"{name!r} is not an allowed operational metric")
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            raise TelemetryRejected("telemetry values are numbers only")
        if not self.enabled:
            return False  # off: nothing is stored
        q = json.loads(self.queue.read_text()) if self.queue.exists() else {}
        e = q.setdefault(name, {"count": 0, "sum": 0.0})
        e["count"] += 1
        e["sum"] += float(value)
        self.queue.write_text(json.dumps(q, sort_keys=True))
        return True

    def payload(self) -> dict:
        q = json.loads(self.queue.read_text()) if self.queue.exists() else {}
        return {"install_id": self.settings.load().get("install_id"), "metrics": q,
                "schema": "forge-operational-metrics/1"}

    def preview(self) -> str:
        return json.dumps(self.payload(), indent=2, sort_keys=True)

    def send(self, sender: Callable[[dict], None]) -> bool:
        if not self.enabled:
            return False
        sender(self.payload())
        self.queue.unlink(missing_ok=True)
        return True


# --------------------------------------------------------------------------- diagnostics

_EMAIL = re.compile(r"[\w.+-]+@[\w-]+\.[\w.]+")
_IPV4 = re.compile(r"\b(?:\d{1,3}\.){3}\d{1,3}\b")


def scrub(text: str) -> str:
    text = redact(text)
    home = os.path.expanduser("~")
    if home and home != "/":
        text = text.replace(home, "~")
    text = _EMAIL.sub("[email]", text)
    return _IPV4.sub("[ip]", text)


@dataclass
class DiagnosticBundle:
    files: dict[str, str] = field(default_factory=dict)

    @property
    def digest(self) -> str:
        h = hashlib.sha256()
        for name in sorted(self.files):
            h.update(name.encode() + b"\0" + self.files[name].encode() + b"\0")
        return h.hexdigest()

    def preview(self) -> str:
        parts = [f"Diagnostic bundle preview (digest {self.digest}). Nothing has been sent or written.", ""]
        for name in sorted(self.files):
            parts += [f"===== {name} =====", self.files[name], ""]
        return "\n".join(parts)


#: Event payload fields that are safe enough to include (after scrubbing): states and short reasons.
_EVENT_FIELDS = ("state", "from_state", "to_state", "status", "check", "category")


def collect_diagnostics(data_dir: str | Path, *, store=None, event_limit: int = 200,
                        project_file: str | Path | None = None) -> DiagnosticBundle:
    data_dir = Path(data_dir)
    b = DiagnosticBundle()
    from .. import __version__ as forge_version  # type: ignore[attr-defined]

    b.files["versions.json"] = json.dumps({"forge": forge_version, "python": platform.python_version(),
                                           "platform": platform.platform()}, indent=2)
    tm = data_dir / "toolchain-manifest.json"
    if tm.exists():
        b.files["toolchain-manifest.json"] = scrub(tm.read_text())
    if project_file and Path(project_file).exists():
        lines = []
        for line in Path(project_file).read_text().splitlines():
            if re.search(r"(?i)(key|token|secret|password)\s*=", line) and not line.strip().startswith("#"):
                line = line.split("=", 1)[0] + "= [REDACTED]"
            lines.append(line)
        b.files["project.toml"] = scrub("\n".join(lines))
    if store is not None:
        events = []
        for ev in store.events(limit=event_limit):
            payload = ev.get("payload") or {}
            events.append({"seq": ev["seq"], "type": ev["type"], "root_id": ev.get("root_id"),
                           **{k: scrub(str(payload[k]))[:200] for k in _EVENT_FIELDS if k in payload}})
        b.files["events.json"] = json.dumps(events, indent=2)
        counts: dict[str, int] = {}
        for r in store.list_roots():
            counts[r.state.value] = counts.get(r.state.value, 0) + 1
        b.files["task-states.json"] = json.dumps(counts, indent=2, sort_keys=True)
    b.files["privacy.json"] = json.dumps({"telemetry_enabled": PrivacySettings(data_dir).load()["telemetry_enabled"],
                                          "excluded": ["prompts", "code", "event payload text", "logs", "images",
                                                       "audio", "video", "credentials"]}, indent=2)
    return b


def export_diagnostics(bundle: DiagnosticBundle, out_zip: str | Path, *, confirm_digest: str) -> Path:
    if confirm_digest != bundle.digest:
        raise PermissionError("preview the bundle and confirm its digest before exporting")
    out = Path(out_zip)
    out.parent.mkdir(parents=True, exist_ok=True)
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        for name in sorted(bundle.files):
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            z.writestr(info, bundle.files[name])
    out.write_bytes(buf.getvalue())
    return out
