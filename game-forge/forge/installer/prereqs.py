"""Prerequisite assessment for the setup assistant.

Plan ("Installer support updates backups and telemetry"): the installer *verifies rather than
conceals* prerequisites. It reports OS/toolchain compatibility, editor activation, build modules,
disk capacity, GPU profile, provider authentication and a real-phone connection, and documents
which steps need a user account or a manual licence decision.

Every item has one of these statuses and nothing is upgraded to OK without evidence:

* ``OK``           verified on this machine;
* ``MISSING``      required and absent (with the remedy);
* ``MANUAL``       needs a person: an account, a licence decision or a device action;
* ``NOT_VERIFIED`` present but not provable from here (e.g. Unity activation);
* ``OPTIONAL``     not required for the supported profile's first workflow.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Optional

from ..models import ToolchainManifest

MIN_DISK_GB = 60.0  # Unity + Android modules + one project workspace + caches (proposed; measure in preflight)


@dataclass
class PrereqItem:
    id: str
    title: str
    status: str
    detail: str = ""
    remedy: str = ""
    needs_account: bool = False
    manual_licence_decision: bool = False
    required_for: list[str] = field(default_factory=list)


@dataclass
class PrereqReport:
    items: list[PrereqItem]

    def by_id(self, iid: str) -> PrereqItem:
        return next(i for i in self.items if i.id == iid)

    @property
    def blocking(self) -> list[PrereqItem]:
        return [i for i in self.items if i.status == "MISSING"]

    def ready_for(self, capability: str) -> bool:
        return all(i.status == "OK" for i in self.items if capability in i.required_for)

    def to_dict(self) -> dict:
        return {"items": [asdict(i) for i in self.items],
                "ready": {c: self.ready_for(c) for c in ("rules_loop", "android_build", "device_evidence")}}

    def markdown(self) -> str:
        out = ["| Check | Status | Detail | What to do |", "|---|---|---|---|"]
        for i in self.items:
            extra = []
            if i.needs_account:
                extra.append("needs an account")
            if i.manual_licence_decision:
                extra.append("needs your licence decision")
            remedy = i.remedy + (f" ({', '.join(extra)})" if extra else "")
            out.append(f"| {i.title} | **{i.status}** | {i.detail} | {remedy or '-'} |")
        ready = self.to_dict()["ready"]
        out += ["", "Ready for: " + ", ".join(f"{k} = {'yes' if v else 'no'}" for k, v in ready.items())]
        return "\n".join(out) + "\n"


def _android_modules(editor: Optional[str]) -> tuple[str, str]:
    if not editor:
        return "MISSING", "no Unity editor"
    root = Path(editor).resolve().parent  # .../Editor
    cand = [root / "Data" / "PlaybackEngines" / "AndroidPlayer", root.parent / "Editor" / "Data" / "PlaybackEngines" / "AndroidPlayer"]
    for c in cand:
        if c.is_dir():
            return "OK", f"AndroidPlayer module at {c}"
    return "MISSING", "Android Build Support module not found next to the editor"


def assess(m: ToolchainManifest, *, min_disk_gb: float = MIN_DISK_GB,
           provider_credentials: Optional[dict[str, bool]] = None) -> PrereqReport:
    items: list[PrereqItem] = []
    osd = m.os or {}
    linux = osd.get("system") == "Linux" and (m.cpu or {}).get("machine") in ("x86_64", "AMD64")
    ubuntu = osd.get("distribution_id") == "ubuntu"
    items.append(PrereqItem(
        "os_profile", "Supported profile (Linux x86-64, Ubuntu LTS)",
        "OK" if linux and ubuntu else "NOT_VERIFIED",
        f"{osd.get('distribution') or osd.get('system', '?')} on {(m.cpu or {}).get('machine', '?')}",
        "" if linux and ubuntu else "Other systems work at your own risk until they have their own validation.",
        required_for=["supported_badge"]))
    for tid, title, req in (("git", "Git", ["rules_loop", "android_build"]),
                            ("dotnet", ".NET SDK (rules library and tests)", ["rules_loop"]),
                            ("adb", "Android platform tools (adb)", ["device_evidence"])):
        t = m.tools.get(tid)
        ok = bool(t and t.found and t.verified)
        items.append(PrereqItem(tid, title, "OK" if ok else "MISSING",
                                f"{t.version} at {t.path}" if ok and t else (t.note if t else "not checked"),
                                "" if ok else f"Install {title} and re-run `forge setup`.", required_for=req))
    unity = m.tools.get("unity")
    if unity and unity.found:
        items.append(PrereqItem("unity", "Unity 6 LTS editor (pinned patch)", "OK", f"{unity.version} at {unity.path}",
                                required_for=["android_build"]))
    else:
        items.append(PrereqItem("unity", "Unity 6 LTS editor (pinned patch)", "MISSING", "editor not found",
                                "Install the pinned editor via Unity Hub, or set FORGE_UNITY_EDITOR.",
                                needs_account=True, required_for=["android_build"]))
    items.append(PrereqItem(
        "unity_activation", "Unity licence activation", "MANUAL" if unity and unity.found else "MISSING",
        "Forge cannot see whether this editor is activated, or which Unity plan applies to you.",
        "Sign in to Unity Hub, activate a licence that fits your revenue/funding, then run a batch-mode build once.",
        needs_account=True, manual_licence_decision=True, required_for=["android_build"]))
    st, detail = _android_modules(m.unity_editor_path)
    items.append(PrereqItem("android_modules", "Unity Android Build Support (SDK/NDK/OpenJDK)", st, detail,
                            "" if st == "OK" else "Add the Android Build Support module in Unity Hub.",
                            required_for=["android_build"]))
    disk = m.disk_free_gb
    items.append(PrereqItem(
        "disk", f"Free disk (at least {min_disk_gb:g} GB proposed)",
        "OK" if disk is not None and disk >= min_disk_gb else ("NOT_VERIFIED" if disk is None else "MISSING"),
        f"{disk} GB free" if disk is not None else "unknown",
        "" if disk is not None and disk >= min_disk_gb else "Free space or point the data directory elsewhere.",
        required_for=["android_build"]))
    gpus = m.gpus or []
    items.append(PrereqItem(
        "gpu", "GPU profile (asset lanes only)", "OK" if gpus else "OPTIONAL",
        ", ".join(f"{g['name']} {g['memory_total_gb']} GB" for g in gpus) or "no NVIDIA GPU detected",
        "" if gpus else "Code, build and review work without a GPU; local asset generation needs a qualified GPU "
        "or a rented worker. VRAM under load is benchmarked per route, not assumed."))
    creds = provider_credentials or {}
    if not creds:
        items.append(PrereqItem("providers", "Coding provider authentication", "MANUAL",
                                "no provider route configured", "Add an approved provider and your own API key "
                                "(BYOK; billed by the provider to you).", needs_account=True,
                                manual_licence_decision=True, required_for=["agent_runs"]))
    for name, has in sorted(creds.items()):
        items.append(PrereqItem(f"provider:{name}", f"Provider credential: {name}", "OK" if has else "MISSING",
                                "credential present (not called during setup)" if has else "no credential found",
                                "" if has else "Set the key in your secret store or key file (chmod 600).",
                                needs_account=True, required_for=["agent_runs"]))
    phones = [d for d in (m.devices or []) if d.get("state") == "device" and not d.get("emulator")]
    unauthorised = [d for d in (m.devices or []) if d.get("state") == "unauthorized"]
    if phones:
        items.append(PrereqItem("phone", "Physical Android test phone", "OK",
                                ", ".join(f"{d.get('model') or '?'} ({d['serial']})" for d in phones),
                                required_for=["device_evidence"]))
    else:
        items.append(PrereqItem("phone", "Physical Android test phone", "MANUAL",
                                "phone connected but not authorised" if unauthorised else "no physical phone connected",
                                "Enable USB debugging, connect the phone and accept the RSA prompt. Emulators do not "
                                "count (Unity does not support them).", required_for=["device_evidence"]))
    return PrereqReport(items)
