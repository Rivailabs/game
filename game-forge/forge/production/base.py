"""Shared lane report type for the R3 production lanes (UI, audio, localization, balance).

A lane report is evidence, not a decision. ``PASS`` means the lane's automated checks found no
error; owner checkpoints listed in ``human_checkpoints`` (visual approval, rights review,
fluent-speaker review, balance judgement) are still required and are never implied by a pass.
"""

from __future__ import annotations

import json
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any

EXIT_CODES = {"PASS": 0, "FAIL": 1, "NEEDS_INPUT": 2, "BLOCKED": 3, "INCOMPLETE": 3}
#: Lane status -> forge.models.EvidenceStatus value (NEEDS_INPUT is not a pass: INCOMPLETE).
EVIDENCE_STATUS = {"PASS": "PASS", "FAIL": "FAIL", "NEEDS_INPUT": "INCOMPLETE", "BLOCKED": "BLOCKED",
                   "INCOMPLETE": "INCOMPLETE"}


@dataclass
class Finding:
    severity: str  # error | warning | info
    code: str
    message: str
    where: str = ""


@dataclass
class LaneReport:
    lane: str
    status: str = "PASS"
    summary: str = ""
    findings: list[Finding] = field(default_factory=list)
    details: dict[str, Any] = field(default_factory=dict)
    human_checkpoints: list[str] = field(default_factory=list)
    evidence_class: str = "static"

    def add(self, severity: str, code: str, message: str, where: str = "") -> None:
        self.findings.append(Finding(severity, code, message, where))

    def errors(self) -> list[Finding]:
        return [f for f in self.findings if f.severity == "error"]

    def finalise(self, summary: str = "") -> "LaneReport":
        if self.status == "PASS" and self.errors():
            self.status = "FAIL"
        counts = {s: sum(1 for f in self.findings if f.severity == s) for s in ("error", "warning", "info")}
        self.summary = summary or (f"{self.lane}: {self.status} ({counts['error']} error(s), "
                                   f"{counts['warning']} warning(s))")
        return self

    @property
    def exit_code(self) -> int:
        return EXIT_CODES.get(self.status, 1)

    def to_dict(self) -> dict:
        return asdict(self)

    def to_json(self) -> str:
        return json.dumps(self.to_dict(), indent=2, sort_keys=True)

    def evidence_fields(self) -> dict:
        """Fields for a :class:`forge.models.Evidence` record."""
        return {"evidence_class": self.evidence_class, "status": EVIDENCE_STATUS.get(self.status, "FAIL"),
                "name": f"{self.lane}-lane", "summary": self.summary,
                "details": {"findings": [asdict(f) for f in self.findings], **self.details,
                            "human_checkpoints": self.human_checkpoints}}

    def write(self, path: str | Path) -> Path:
        p = Path(path)
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(self.to_json() + "\n")
        return p
