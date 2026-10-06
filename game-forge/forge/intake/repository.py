"""Specification service: versioned specifications on disk, with owner decisions.

Layout (``<data_dir>/specs/<project>/``)::

    index.json            versions, approved version, issued requirement IDs, acceptance cases
    events.jsonl          append-only log of every ingest and owner decision
    v<N>/spec.json        one specification version
    v<N>/source.<ext>     the exact source document bytes it was derived from
    v<N>/changes.json|md  change report against the previous version

Rules (plan: "The owner's end to end workflow", steps 2-3):

* An ingest of a changed document creates a new version and a change report. An unchanged
  document creates nothing.
* Unspecified rules are never invented: gaps become NEEDS_INPUT questions; an owner answer is
  recorded as an owner requirement (``source = owner_answer:<question>``), in the owner's words.
* A version is immutable once approved. Until then the owner answers/dismisses questions and
  decides proposed changes on the latest version (every action is logged).
* An approved requirement is never silently rewritten: a later document that changes or drops it
  only *proposes* that change; the approved text stays effective until ``accept_change``.
* Initial acceptance cases are created at approval and are immutable; a changed requirement gets
  a new case and the old case is marked superseded (not edited).
"""

from __future__ import annotations

import json
import os
import re
import tempfile
import time
from pathlib import Path
from typing import Callable, Optional

from ..planning.template import Template
from .analysis import analyse
from .changes import ChangeReport, compute_changes
from .document import SourceDocument, read_document
from .extract import _next_id, assign_ids, extract_requirements
from .spec import (
    KIND_EVIDENCE,
    Question,
    QuestionStatus,
    Requirement,
    RequirementStatus,
    SpecAcceptanceCase,
    Specification,
    SpecStatus,
    text_hash,
)


class SpecError(Exception):
    pass


ModelPass = Callable[[Specification, str], list[Question]]

_COUNTRIES = re.compile(r"\b(?:intended\s+)?(?:release|distribution)\s+countries\s*[:=-]\s*(.+?)\.?$", re.I)


def _atomic_write(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(dir=path.parent, prefix=".tmp-")
    with os.fdopen(fd, "w", encoding="utf-8") as fh:
        fh.write(text)
    os.replace(tmp, path)


def release_countries_from(reqs: list[Requirement]) -> list[str]:
    for r in reqs:
        m = _COUNTRIES.search(r.effective_text)
        if m:
            return [c.strip() for c in re.split(r",|\band\b", m.group(1)) if c.strip()]
    return []


class SpecRepository:
    def __init__(self, root: str | Path, project_id: str, template: Template, *, clock: Callable[[], float] = time.time):
        self.root = Path(root)
        self.project_id = project_id
        self.template = template
        self.clock = clock
        self.root.mkdir(parents=True, exist_ok=True)

    # ------------------------------------------------------------------ storage
    @property
    def index_path(self) -> Path:
        return self.root / "index.json"

    def _index(self) -> dict:
        if self.index_path.exists():
            return json.loads(self.index_path.read_text())
        return {"project_id": self.project_id, "template_id": self.template.id, "versions": [],
                "approved_version": None, "issued_ids": [], "acceptance_cases": [], "superseded_cases": {}}

    def _save_index(self, idx: dict) -> None:
        _atomic_write(self.index_path, json.dumps(idx, indent=2, sort_keys=True))

    def _log(self, event: str, **data) -> None:
        self.root.mkdir(parents=True, exist_ok=True)
        with open(self.root / "events.jsonl", "a", encoding="utf-8") as fh:
            fh.write(json.dumps({"at": self.clock(), "event": event, **data}, sort_keys=True) + "\n")

    def events(self) -> list[dict]:
        p = self.root / "events.jsonl"
        return [json.loads(line) for line in p.read_text().splitlines()] if p.exists() else []

    def _vdir(self, version: int) -> Path:
        return self.root / f"v{version}"

    def _save(self, spec: Specification) -> None:
        if self._is_frozen(spec.version) and spec.status != SpecStatus.APPROVED:
            raise SpecError(f"v{spec.version} is approved and immutable")
        _atomic_write(self._vdir(spec.version) / "spec.json", spec.model_dump_json(indent=2))

    def _is_frozen(self, version: int) -> bool:
        p = self._vdir(version) / "spec.json"
        return p.exists() and json.loads(p.read_text()).get("status") == SpecStatus.APPROVED.value

    def versions(self) -> list[int]:
        return list(self._index()["versions"])

    def get(self, version: int) -> Specification:
        p = self._vdir(version) / "spec.json"
        if not p.exists():
            raise SpecError(f"no specification v{version} for {self.project_id}")
        return Specification.model_validate_json(p.read_text())

    def latest(self) -> Optional[Specification]:
        vs = self.versions()
        return self.get(vs[-1]) if vs else None

    def approved(self) -> Optional[Specification]:
        v = self._index()["approved_version"]
        return self.get(v) if v else None

    def change_report(self, version: int) -> ChangeReport:
        return ChangeReport.model_validate_json((self._vdir(version) / "changes.json").read_text())

    def _editable_latest(self) -> Specification:
        spec = self.latest()
        if spec is None:
            raise SpecError("no specification yet: ingest a brief first")
        if spec.status == SpecStatus.APPROVED:
            raise SpecError(f"v{spec.version} is approved and immutable; ingest a changed brief for a new version")
        return spec

    # ------------------------------------------------------------------ ingest
    def ingest(self, path: str | Path, *, model_pass: Optional[ModelPass] = None
               ) -> tuple[Specification, ChangeReport]:
        doc = read_document(path)
        prev = self.latest()
        if prev is not None and prev.source_sha256 == doc.sha256:
            return prev, compute_changes(prev, prev)
        idx = self._index()
        version = (idx["versions"][-1] + 1) if idx["versions"] else 1
        reqs = assign_ids(extract_requirements(doc),
                          [r for r in prev.requirements if r.source == "document"] if prev else None,
                          issued=set(idx["issued_ids"]))
        if prev is not None:  # owner answers are owner decisions: they carry forward
            reqs += [r.model_copy() for r in prev.requirements if r.source.startswith("owner_answer:")]
        self._apply_overrides(reqs)
        decisions = dict(prev.change_decisions) if prev else {}
        self._protect_approved(reqs, decisions)

        spec = Specification(
            project_id=self.project_id, template_id=self.template.id, template_version=self.template.version,
            version=version, title=doc.title, source_path=str(doc.path), source_format=doc.fmt,
            source_sha256=doc.sha256, previous_version=prev.version if prev else None, requirements=reqs,
            created_at=self.clock(), change_decisions=decisions,
        )
        spec.questions = self._carry_questions(analyse(reqs, self.template), prev)
        if model_pass is not None:
            text = doc.raw.decode("utf-8", errors="replace") if doc.fmt != "docx" else \
                "\n".join(b.text for b in doc.blocks)
            extra = self._carry_questions(model_pass(spec, text), prev, carry_unraised=False)
            spec.questions += [q for q in extra if q.id not in {x.id for x in spec.questions}]
        spec.release_countries = release_countries_from(reqs)
        self._refresh_status(spec)

        report = compute_changes(prev, spec)
        vdir = self._vdir(version)
        vdir.mkdir(parents=True, exist_ok=True)
        ext = {"markdown": ".md", "docx": ".docx"}.get(doc.fmt, ".txt")
        (vdir / f"source{ext}").write_bytes(doc.raw)
        _atomic_write(vdir / "changes.json", report.model_dump_json(indent=2))
        _atomic_write(vdir / "changes.md", report.markdown())
        self._save(spec)
        if prev is not None and prev.status != SpecStatus.APPROVED:
            prev.status = SpecStatus.SUPERSEDED
            self._save(prev)
        idx["versions"].append(version)
        idx["issued_ids"] = sorted(set(idx["issued_ids"]) | {r.id for r in reqs})
        self._save_index(idx)
        self._log("spec_ingested", version=version, source_sha256=doc.sha256, requirements=len(reqs),
                  open_questions=len(spec.open_blocking()), approved_changes_pending=report.approved_changes_pending)
        return spec, report

    def _apply_overrides(self, reqs: list[Requirement]) -> None:
        overridden = {o for r in reqs if r.source.startswith("owner_answer:") for o in r.overrides}
        for r in reqs:
            if r.id in overridden and r.status == RequirementStatus.ACTIVE:
                r.status = RequirementStatus.OVERRIDDEN

    def _protect_approved(self, reqs: list[Requirement], decisions: dict[str, dict]) -> None:
        base = self.approved()
        if base is None:
            return
        by_id = {r.id: r for r in reqs}
        for b in base.requirements:
            if not b.plannable:
                continue
            approved = b.effective_text
            r = by_id.get(b.id)
            if r is None:  # the document dropped an approved requirement: propose, do not drop
                d = decisions.get(b.id)
                if d and d["decision"] == "accepted" and d["text_hash"] == "":
                    continue
                keep = b.model_copy(update={"status": RequirementStatus.REMOVAL_PROPOSED, "approved_text": approved,
                                            "approved_in": base.version, "text": ""})
                if d and d["decision"] == "rejected" and d["text_hash"] == "":
                    keep.status, keep.text = RequirementStatus.ACTIVE, approved
                reqs.append(keep)
                continue
            r.approved_text, r.approved_in = approved, base.version
            if text_hash(r.text) != text_hash(approved) and r.status == RequirementStatus.ACTIVE:
                d = decisions.get(r.id)
                if d and d["text_hash"] == text_hash(r.text):
                    if d["decision"] == "rejected":
                        r.text = approved
                    continue  # an earlier explicit decision on exactly this text still applies
                r.status = RequirementStatus.CHANGE_PROPOSED

    @staticmethod
    def _carry_questions(found: list[Question], prev: Optional[Specification], carry_unraised: bool = True
                         ) -> list[Question]:
        """Keep the owner's answers and dismissals for findings that are raised again."""
        old = {q.id: q for q in (prev.questions if prev else [])}
        out = []
        for q in found:
            o = old.get(q.id)
            if o is not None and o.status != QuestionStatus.OPEN:
                q = q.model_copy(update={"status": o.status, "answer": o.answer, "answered_by": o.answered_by,
                                         "answered_at": o.answered_at})
            out.append(q)
        if carry_unraised:
            raised = {q.id for q in out}
            for o in old.values():
                if o.id not in raised and o.status != QuestionStatus.OPEN:
                    out.append(o.model_copy(update={"detail": {**o.detail, "no_longer_raised": True}}))
        return out

    @staticmethod
    def _refresh_status(spec: Specification) -> None:
        if spec.status in (SpecStatus.APPROVED, SpecStatus.SUPERSEDED):
            return
        spec.status = SpecStatus.NEEDS_INPUT if spec.open_blocking() else SpecStatus.DRAFT

    # ------------------------------------------------------------------ owner decisions
    def answer(self, question_id: str, answer: str, *, by: str, overrides: list[str] | None = None,
               kind: str | None = None) -> Specification:
        """Record the owner's answer. For a rule question it becomes an owner requirement (owner's words)."""
        answer = answer.strip()
        if not answer:
            raise SpecError("an answer cannot be empty")
        spec = self._editable_latest()
        try:
            q = spec.question(question_id)
        except KeyError:
            raise SpecError(f"no question {question_id} in v{spec.version}") from None
        if q.status != QuestionStatus.OPEN:
            raise SpecError(f"{question_id} is already {q.status.value}")
        overrides = list(overrides or [])
        for rid in overrides:
            try:
                target = spec.requirement(rid)
            except KeyError:
                raise SpecError(f"cannot override unknown requirement {rid}") from None
            if target.approved_text is not None:
                raise SpecError(f"{rid} is approved; change it through the document and accept_change, "
                                "not by overriding it from an answer")
        q.status, q.answer, q.answered_by, q.answered_at = QuestionStatus.ANSWERED, answer, by, self.clock()
        if q.category != "unsupported_feature" or not overrides:
            first = next((spec.requirement(r) for r in q.requirement_ids if r in {x.id for x in spec.requirements}),
                         None)
            idx = self._index()
            rid = _next_id(set(idx["issued_ids"]) | {r.id for r in spec.requirements})
            spec.requirements.append(Requirement(
                id=rid, section="Owner answers", text=answer, kind=kind or (first.kind if first else "rule"),
                normative="must", source=f"owner_answer:{question_id}", overrides=overrides))
            idx["issued_ids"] = sorted(set(idx["issued_ids"]) | {rid})
            self._save_index(idx)
        else:  # an unsupported feature the owner removes from scope: no new rule, the requirement is overridden
            spec.requirements.append(Requirement(
                id=_next_id(set(self._index()["issued_ids"]) | {r.id for r in spec.requirements}),
                section="Owner answers", text=answer, kind="info", normative="info",
                source=f"owner_answer:{question_id}", overrides=overrides, status=RequirementStatus.INFO))
        self._apply_overrides(spec.requirements)
        self._refresh_status(spec)
        self._save(spec)
        self._log("question_answered", version=spec.version, question=question_id, by=by, overrides=overrides)
        return spec

    def dismiss(self, question_id: str, reason: str, *, by: str) -> Specification:
        """The owner states a finding is not a problem. The reason is recorded; nothing is invented."""
        if not reason.strip():
            raise SpecError("dismissing a finding needs a reason")
        spec = self._editable_latest()
        try:
            q = spec.question(question_id)
        except KeyError:
            raise SpecError(f"no question {question_id} in v{spec.version}") from None
        q.status, q.answer, q.answered_by, q.answered_at = QuestionStatus.DISMISSED, reason.strip(), by, self.clock()
        self._refresh_status(spec)
        self._save(spec)
        self._log("question_dismissed", version=spec.version, question=question_id, by=by, reason=reason)
        return spec

    def _decide(self, rid: str, decision: str, by: str) -> Specification:
        spec = self._editable_latest()
        try:
            r = spec.requirement(rid)
        except KeyError:
            raise SpecError(f"no requirement {rid} in v{spec.version}") from None
        if r.status not in (RequirementStatus.CHANGE_PROPOSED, RequirementStatus.REMOVAL_PROPOSED):
            raise SpecError(f"{rid} has no proposed change (status {r.status.value})")
        removal = r.status == RequirementStatus.REMOVAL_PROPOSED
        spec.change_decisions[rid] = {"decision": decision, "text_hash": "" if removal else text_hash(r.text),
                                      "by": by, "at": self.clock(), "approved_text": r.approved_text}
        if decision == "accepted":
            if removal:
                spec.requirements = [x for x in spec.requirements if x.id != rid]
            else:
                r.status = RequirementStatus.ACTIVE
        else:
            r.status, r.text = RequirementStatus.ACTIVE, r.approved_text or r.text
        self._refresh_status(spec)
        self._save(spec)
        self._log(f"change_{decision}", version=spec.version, requirement=rid, by=by, removal=removal)
        return spec

    def accept_change(self, rid: str, *, by: str) -> Specification:
        return self._decide(rid, "accepted", by)

    def reject_change(self, rid: str, *, by: str) -> Specification:
        return self._decide(rid, "rejected", by)

    def approve(self, version: int, *, by: str, release_countries: list[str] | None = None) -> Specification:
        spec = self.get(version)
        latest = self.latest()
        if latest is None or latest.version != version:
            raise SpecError(f"only the latest version can be approved (latest is v{latest.version if latest else '-'})")
        if spec.status == SpecStatus.APPROVED:
            raise SpecError(f"v{version} is already approved")
        if spec.open_blocking():
            raise SpecError("open questions must be answered or dismissed first: "
                            + ", ".join(q.id for q in spec.open_blocking()))
        pending = [r.id for r in spec.requirements
                   if r.status in (RequirementStatus.CHANGE_PROPOSED, RequirementStatus.REMOVAL_PROPOSED)]
        if pending:
            raise SpecError("approved requirements have proposed changes; accept or reject each first: "
                            + ", ".join(pending))
        countries = release_countries or spec.release_countries
        if not countries:
            raise SpecError("intended release countries are not declared (brief 'Release countries: ...' or "
                            "--release-countries)")
        spec.release_countries = list(countries)
        now = self.clock()
        for r in spec.requirements:
            if r.plannable:
                r.approved_text, r.approved_in = r.effective_text, version
        spec.status, spec.approved_at, spec.approved_by = SpecStatus.APPROVED, now, by
        idx = self._index()
        new_cases = self._freeze_cases(spec, idx)
        _atomic_write(self._vdir(version) / "spec.json", spec.model_dump_json(indent=2))
        idx["approved_version"] = version
        self._save_index(idx)
        self._log("spec_approved", version=version, by=by, digest=spec.digest, new_cases=new_cases,
                  release_countries=spec.release_countries)
        return spec

    def _freeze_cases(self, spec: Specification, idx: dict) -> list[str]:
        cases = [SpecAcceptanceCase.model_validate(c) for c in idx["acceptance_cases"]]
        superseded: dict[str, int] = idx["superseded_cases"]
        active = {c.requirement_id: c for c in cases if c.id not in superseded}
        created = []
        live = {r.id for r in spec.plannable()}
        for r in spec.plannable():
            cur = active.get(r.id)
            if cur is not None and cur.requirement_hash == r.text_hash:
                continue
            n = sum(1 for c in cases if c.requirement_id == r.id)
            case = SpecAcceptanceCase(
                id=f"AC-{r.id}" if n == 0 else f"AC-{r.id}.{n + 1}", requirement_id=r.id,
                description=r.effective_text, evidence_class=KIND_EVIDENCE.get(r.kind, "rules"),
                created_in=spec.version, requirement_hash=r.text_hash)
            if cur is not None:
                superseded[cur.id] = spec.version
            cases.append(case)
            created.append(case.id)
        for rid, c in active.items():  # requirement no longer in the approved spec
            if rid not in live and c.id not in superseded:
                superseded[c.id] = spec.version
        idx["acceptance_cases"] = [c.model_dump() for c in cases]
        idx["superseded_cases"] = superseded
        return created

    def acceptance_cases(self, *, include_superseded: bool = False) -> list[SpecAcceptanceCase]:
        idx = self._index()
        cases = [SpecAcceptanceCase.model_validate(c) for c in idx["acceptance_cases"]]
        if include_superseded:
            return cases
        return [c for c in cases if c.id not in idx["superseded_cases"]]

    def superseded_cases(self) -> dict[str, int]:
        return dict(self._index()["superseded_cases"])

    def changed_since_approval(self, version: int) -> list[str]:
        """Requirement IDs whose approved text differs between ``version`` and the previous approval."""
        cur = self.get(version)
        prev_approved = None
        for v in reversed(self.versions()):
            if v < version and self._is_frozen(v):
                prev_approved = self.get(v)
                break
        if prev_approved is None:
            return []
        old = {r.id: r.text_hash for r in prev_approved.plannable()}
        return sorted(r.id for r in cur.plannable() if r.id in old and old[r.id] != r.text_hash) + \
            sorted(rid for rid in old if rid not in {r.id for r in cur.plannable()})


def load_source(spec: Specification, repo: SpecRepository) -> SourceDocument:
    vdir = repo._vdir(spec.version)
    src = next(vdir.glob("source.*"))
    return read_document(src)
