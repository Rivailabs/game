"""Change report between two specification versions.

Produced every time the source document changes. Approved requirements are called out
separately: their change is only *proposed* in the new version (the approved text stays
effective) until the owner accepts it with ``accept_change``.
"""

from __future__ import annotations

from typing import Optional

from pydantic import BaseModel, Field

from .spec import QuestionStatus, RequirementStatus, Specification, normalise


class RequirementChange(BaseModel):
    id: str
    section: str
    before: str = ""
    after: str = ""
    approved: bool = False  # the 'before' text was owner-approved
    status: str = ""


class ChangeReport(BaseModel):
    project_id: str
    from_version: Optional[int]
    to_version: int
    source_changed: bool
    added: list[RequirementChange] = Field(default_factory=list)
    removed: list[RequirementChange] = Field(default_factory=list)
    modified: list[RequirementChange] = Field(default_factory=list)
    unchanged: int = 0
    approved_changes_pending: list[str] = Field(default_factory=list)
    questions_opened: list[str] = Field(default_factory=list)
    questions_closed: list[str] = Field(default_factory=list)

    @property
    def empty(self) -> bool:
        return not (self.added or self.removed or self.modified)

    def markdown(self) -> str:
        frm = f"v{self.from_version}" if self.from_version else "(none)"
        lines = [f"# Specification change report: {self.project_id} {frm} -> v{self.to_version}", ""]
        if self.from_version is None:
            lines.append(f"First version: {len(self.added)} requirement(s).")
        elif self.empty:
            lines.append("No requirement changed." + ("" if self.source_changed else " The source is identical."))
        if self.approved_changes_pending:
            lines += ["", "**Approved behaviour is NOT rewritten.** These approved requirements changed in the "
                      "source; the approved text stays effective until you accept or reject each change:", ""]
            lines += [f"- {rid}" for rid in self.approved_changes_pending]
        for title, items in (("Added", self.added), ("Removed", self.removed), ("Modified", self.modified)):
            if not items:
                continue
            lines += ["", f"## {title}", ""]
            for c in items:
                tag = " (approved)" if c.approved else ""
                if title == "Modified":
                    lines.append(f"- **{c.id}**{tag} [{c.section}]\n  - before: {c.before}\n  - after: {c.after}")
                else:
                    lines.append(f"- **{c.id}**{tag} [{c.section}] {c.after or c.before}")
        lines += ["", f"Unchanged: {self.unchanged}"]
        if self.questions_opened:
            lines += ["", "New open questions: " + ", ".join(self.questions_opened)]
        if self.questions_closed:
            lines += ["", "Questions no longer raised: " + ", ".join(self.questions_closed)]
        return "\n".join(lines) + "\n"


def compute_changes(prev: Optional[Specification], new: Specification) -> ChangeReport:
    rep = ChangeReport(project_id=new.project_id, from_version=prev.version if prev else None, to_version=new.version,
                       source_changed=(prev is None or prev.source_sha256 != new.source_sha256))
    old = {r.id: r for r in (prev.requirements if prev else [])}
    cur = {r.id: r for r in new.requirements}
    for rid, r in cur.items():
        if rid not in old:
            rep.added.append(RequirementChange(id=rid, section=r.section, after=r.text, status=r.status.value))
        elif normalise(old[rid].text) != normalise(r.text):
            rep.modified.append(RequirementChange(id=rid, section=r.section, before=old[rid].text, after=r.text,
                                                  approved=r.approved_text is not None, status=r.status.value))
        else:
            rep.unchanged += 1
    for rid, r in old.items():
        if rid not in cur:
            rep.removed.append(RequirementChange(id=rid, section=r.section, before=r.text,
                                                 approved=r.approved_text is not None))
    rep.approved_changes_pending = [r.id for r in new.requirements
                                    if r.status in (RequirementStatus.CHANGE_PROPOSED, RequirementStatus.REMOVAL_PROPOSED)]
    old_open = {q.id for q in (prev.questions if prev else []) if q.status == QuestionStatus.OPEN}
    new_open = {q.id for q in new.questions if q.status == QuestionStatus.OPEN}
    new_all = {q.id for q in new.questions}
    rep.questions_opened = sorted(new_open - old_open)
    rep.questions_closed = sorted(q for q in old_open if q not in new_all)
    return rep
