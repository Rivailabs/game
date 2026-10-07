"""Release candidates, release records and signed release approvals.

Plan ("Release and rollback procedure"): a release candidate is one immutable combination of
source commit, rules and economy versions, accepted assets, dependency manifest, package
identifier, build version and test evidence. The owner approves that combination. A later change
creates a new candidate; signing or uploading a different file with the same name is not allowed.

The candidate hash is the sha256 of the candidate's canonical JSON, so any changed field (a single
asset byte, the unsigned artifact, a test result) produces a different hash and needs a fresh
approval.
"""

from __future__ import annotations

import hashlib
import time
from typing import Optional

from pydantic import BaseModel, Field

from .keys import canonical, sign_record

#: Evidence statuses that count as passed for release. INCOMPLETE/BLOCKED never do.
PASSING = {"PASS"}


class TestResult(BaseModel):
    __test__ = False  # not a pytest test class

    name: str
    status: str  # PASS | FAIL | INCOMPLETE | BLOCKED | INFO
    sha256: str = ""  # hash of the evidence/log artifact
    evidence_class: str = "rules"


class ReleaseCandidate(BaseModel):
    project_id: str
    package_id: str
    version_name: str
    version_code: int
    source_commit: str
    source_archive_sha256: str
    rules_version: str
    economy_version: Optional[str] = None
    spec_version: int
    spec_digest: str
    assets: dict[str, str] = Field(default_factory=dict)  # path -> sha256 (accepted, provider-independent)
    dependency_manifest_sha256: str
    toolchain_manifest_sha256: str = ""
    artifact_name: str
    artifact_sha256: str  # unsigned (or development-signed) build
    provenance_sha256: str
    tests: list[TestResult] = Field(default_factory=list)

    @property
    def candidate_hash(self) -> str:
        return hashlib.sha256(canonical(self.model_dump(mode="json"))).hexdigest()


class ReleaseRecord(BaseModel):
    """What must be written down before signing (plan: "Before signing, the release record contains...")."""

    candidate: ReleaseCandidate
    scope: str = ""
    known_issues: list[str] = Field(default_factory=list)
    device_results: list[str] = Field(default_factory=list)
    purchase_ad_results: Optional[list[str]] = None  # None = not relevant to this release (stated explicitly)
    data_declarations: str = ""
    provenance_exceptions: list[str] = Field(default_factory=list)
    support_contact: str = ""
    rollback_method: str = ""
    migration_notes: str = ""
    last_restore_drill: str = ""
    destination: str = "signing"

    def missing_fields(self) -> list[str]:
        missing = [f for f in ("scope", "data_declarations", "support_contact", "rollback_method")
                   if not getattr(self, f).strip()]
        if not self.device_results:
            missing.append("device_results")
        if not self.last_restore_drill.strip():
            missing.append("last_restore_drill")
        return missing


class ApprovalRefused(Exception):
    pass


def approval_blockers(record: ReleaseRecord, *, provenance_complete: bool, accepted_exceptions: list[str] = ()
                      ) -> list[str]:
    c = record.candidate
    out = [f"release record field missing: {f}" for f in record.missing_fields()]
    for t in c.tests:
        if t.status not in PASSING and t.status != "INFO" and t.name not in accepted_exceptions:
            out.append(f"test {t.name} is {t.status} (INCOMPLETE/BLOCKED never counts as a pass)")
    if not any(t.evidence_class == "device" and t.status == "PASS" for t in c.tests):
        out.append("no passing physical-device evidence")
    if not provenance_complete and not record.provenance_exceptions:
        out.append("asset provenance is incomplete and no exception is recorded")
    return out


def approve_release(record: ReleaseRecord, *, approver: str, approver_key: str, provenance_complete: bool,
                    accepted_exceptions: list[str] | None = None, clock=time.time) -> dict:
    """Owner approval of one exact candidate for signing. Returns the signed approval record."""
    blockers = approval_blockers(record, provenance_complete=provenance_complete,
                                 accepted_exceptions=accepted_exceptions or [])
    if blockers:
        raise ApprovalRefused("; ".join(blockers))
    c = record.candidate
    body = {
        "kind": "release_approval", "decision": "APPROVED", "destination": record.destination,
        "candidate_hash": c.candidate_hash, "artifact_sha256": c.artifact_sha256, "artifact_name": c.artifact_name,
        "package_id": c.package_id, "version_code": c.version_code, "version_name": c.version_name,
        "project_id": c.project_id, "record_sha256": hashlib.sha256(canonical(record.model_dump(mode="json"))).hexdigest(),
        "accepted_exceptions": list(accepted_exceptions or []), "approved_by": approver, "approved_at": clock(),
    }
    return sign_record(body, approver_key)
