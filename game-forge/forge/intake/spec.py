"""Versioned specification schema (the specification service's data).

A :class:`Specification` is one immutable version derived from one source document (plus owner
answers). Requirements carry stable IDs across versions so change reports and traceability keep
working when the brief is edited. Approved behaviour is protected: when a later document changes
an approved requirement, the new version records the change as *proposed* and keeps the approved
text effective until the owner explicitly accepts it.
"""

from __future__ import annotations

import hashlib
import re
from enum import Enum
from typing import Optional

from pydantic import BaseModel, Field


class SpecStatus(str, Enum):
    DRAFT = "DRAFT"  # no blocking question open; ready for owner approval
    NEEDS_INPUT = "NEEDS_INPUT"  # at least one blocking question is open
    APPROVED = "APPROVED"
    SUPERSEDED = "SUPERSEDED"  # a newer version was ingested before this one was approved


class RequirementStatus(str, Enum):
    ACTIVE = "ACTIVE"
    CHANGE_PROPOSED = "CHANGE_PROPOSED"  # source changed an approved requirement; approved text stays effective
    REMOVAL_PROPOSED = "REMOVAL_PROPOSED"  # source dropped an approved requirement; it stays effective
    OVERRIDDEN = "OVERRIDDEN"  # an owner answer supersedes it (e.g. one side of a contradiction)
    INFO = "INFO"  # context only (overview prose); never planned


class Requirement(BaseModel):
    id: str
    section: str
    text: str  # text from the current source document (or owner answer)
    kind: str  # rule | ui | audio | localization | balance | release | art | feature | info
    normative: str = "must"  # must | should | may | info
    source: str = "document"  # document | owner_answer:<question id>
    source_line: int = 0
    status: RequirementStatus = RequirementStatus.ACTIVE
    approved_text: Optional[str] = None  # text the owner last approved (protected)
    approved_in: Optional[int] = None
    overrides: list[str] = Field(default_factory=list)  # requirement ids an owner answer supersedes

    @property
    def effective_text(self) -> str:
        """What planning and acceptance use: the approved text until a change is accepted."""
        if self.status in (RequirementStatus.CHANGE_PROPOSED, RequirementStatus.REMOVAL_PROPOSED):
            return self.approved_text or self.text
        return self.text

    @property
    def plannable(self) -> bool:
        return self.status not in (RequirementStatus.OVERRIDDEN, RequirementStatus.INFO) and self.kind != "info"

    @property
    def text_hash(self) -> str:
        return text_hash(self.effective_text)


class QuestionStatus(str, Enum):
    OPEN = "OPEN"
    ANSWERED = "ANSWERED"
    DISMISSED = "DISMISSED"  # owner says the finding is not a problem (reason recorded)


class Question(BaseModel):
    """A NEEDS_INPUT item. Forge asks; it never answers its own question."""

    id: str
    category: str  # missing_rule | contradiction | ambiguous | unsupported_feature | undefined_term | model_finding
    text: str
    requirement_ids: list[str] = Field(default_factory=list)
    blocking: bool = True
    status: QuestionStatus = QuestionStatus.OPEN
    answer: str = ""
    answered_by: str = ""
    answered_at: float = 0.0
    source: str = "rules"  # rules | model:<provider name>
    detail: dict = Field(default_factory=dict)


class SpecAcceptanceCase(BaseModel):
    """Immutable once created. A changed requirement gets a *new* case; the old one is superseded."""

    id: str
    requirement_id: str
    description: str
    evidence_class: str
    created_in: int
    requirement_hash: str


class Specification(BaseModel):
    project_id: str
    template_id: str
    template_version: str
    version: int
    status: SpecStatus = SpecStatus.DRAFT
    title: str = ""
    source_path: str = ""
    source_format: str = ""
    source_sha256: str = ""
    previous_version: Optional[int] = None
    requirements: list[Requirement] = Field(default_factory=list)
    questions: list[Question] = Field(default_factory=list)
    created_at: float = 0.0
    approved_at: Optional[float] = None
    approved_by: Optional[str] = None
    release_countries: list[str] = Field(default_factory=list)
    model_passes: list[dict] = Field(default_factory=list)  # provider, cost, findings count
    #: Owner decisions on proposed changes to approved requirements:
    #: {requirement id: {"decision": "accepted"|"rejected", "text_hash": ..., "by": ..., "at": ...}}
    change_decisions: dict[str, dict] = Field(default_factory=dict)

    def requirement(self, rid: str) -> Requirement:
        for r in self.requirements:
            if r.id == rid:
                return r
        raise KeyError(rid)

    def question(self, qid: str) -> Question:
        for q in self.questions:
            if q.id == qid:
                return q
        raise KeyError(qid)

    def open_blocking(self) -> list[Question]:
        return [q for q in self.questions if q.status == QuestionStatus.OPEN and q.blocking]

    def plannable(self) -> list[Requirement]:
        return [r for r in self.requirements if r.plannable]

    @property
    def digest(self) -> str:
        """Hash of the effective content (what an approval covers)."""
        h = hashlib.sha256()
        h.update(f"{self.template_id}@{self.template_version}\n".encode())
        for r in sorted(self.requirements, key=lambda x: x.id):
            h.update(f"{r.id}|{r.kind}|{r.status.value}|{r.effective_text}\n".encode())
        return h.hexdigest()


_WS = re.compile(r"\s+")


def normalise(text: str) -> str:
    return _WS.sub(" ", re.sub(r"[^\w\s%]", " ", text.lower())).strip()


def text_hash(text: str) -> str:
    return hashlib.sha256(normalise(text).encode()).hexdigest()[:16]


KIND_EVIDENCE = {
    "rule": "rules",
    "feature": "rules",
    "ui": "human",
    "art": "human",
    "audio": "human",
    "localization": "static",
    "balance": "replay",
    "release": "integration",
}
