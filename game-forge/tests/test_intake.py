"""R3 document intake: brief -> versioned specification, questions, change reports, protection."""

from __future__ import annotations

from pathlib import Path

import pytest

from forge.intake import QuestionStatus, RequirementStatus, SpecError, SpecRepository, SpecStatus, read_document
from forge.intake.analysis import analyse
from forge.intake.document import IntakeError, parse_markdown, parse_plain_text
from forge.intake.extract import assign_ids, extract_requirements
from forge.intake.model_pass import ModelPassUnavailable, run_model_pass
from forge.models import ProjectPolicy
from forge.planning.template import load_template
from forge.providers import FakeProvider
from forge.providers.base import PolicyViolation

ROOT = Path(__file__).resolve().parent.parent
RUNE = ROOT / "templates" / "turn-duel-2p" / "examples" / "rune-duel-brief.md"
FLAWED = Path(__file__).resolve().parent / "data" / "flawed-brief.md"


@pytest.fixture
def template():
    return load_template("turn-duel-2p")


@pytest.fixture
def repo(tmp_path, template):
    t = [1000.0]

    def clock():
        t[0] += 1
        return t[0]

    return SpecRepository(tmp_path / "specs", "game", template, clock=clock)


def write(tmp_path: Path, name: str, text: str) -> Path:
    p = tmp_path / name
    p.write_text(text)
    return p


# ------------------------------------------------------------------ parsing


def test_markdown_blocks_keep_section_path_and_line():
    title, blocks = parse_markdown("# Game\n\nIntro text.\n\n## Rules\n\n### Damage\n\n- Hits deal 2.\n  continued\n"
                                   "\n| a | b |\n|---|---|\n| x | y |\n")
    assert title == "Game"
    assert blocks[0].section == () and blocks[0].kind == "paragraph"
    bullet = blocks[1]
    assert bullet.section == ("Rules", "Damage") and bullet.text == "Hits deal 2. continued" and bullet.line == 9
    assert [b.text for b in blocks if b.kind == "table_row"] == ["a | b", "x | y"]


def test_plain_text_headings_are_detected():
    title, blocks = parse_plain_text("Star Duel\n\nPlayers\n- Two players take part.\n\nWinning\nA player wins at 0.\n")
    assert title == "Star Duel"
    assert {b.section for b in blocks} == {("Players",), ("Winning",)}


def test_docx_brief_is_read(tmp_path):
    docx = pytest.importorskip("docx")
    d = docx.Document()
    d.add_heading("Docx Duel", level=1)
    d.add_heading("Players", level=2)
    d.add_paragraph("Two players take part.", style="List Bullet")
    d.add_heading("Winning", level=2)
    d.add_paragraph("A player wins when the opponent reaches 0 health.")
    path = tmp_path / "brief.docx"
    d.save(str(path))
    doc = read_document(path)
    assert doc.fmt == "docx" and doc.title == "Docx Duel"
    assert [(b.section, b.kind) for b in doc.blocks] == [(("Players",), "bullet"), (("Winning",), "paragraph")]


def test_missing_or_empty_brief_is_an_error(tmp_path):
    with pytest.raises(IntakeError):
        read_document(tmp_path / "nope.md")
    with pytest.raises(IntakeError):
        read_document(write(tmp_path, "empty.md", "# Only a title\n"))


def test_extraction_kinds_normative_and_explicit_ids(tmp_path):
    doc = read_document(write(tmp_path, "b.md", "# G\n\nContext only.\n\n## Screens\n\n- [UI-1] Menu has a play button.\n"
                                                 "\n## Rules\n\nA player may pass. Players should be polite.\n"))
    reqs = extract_requirements(doc)
    by_text = {r.text: r for r in reqs}
    assert by_text["Context only."].kind == "info" and by_text["Context only."].status == RequirementStatus.INFO
    assert by_text["Menu has a play button."].id == "UI-1" and by_text["Menu has a play button."].kind == "ui"
    assert by_text["A player may pass."].normative == "may"
    assert by_text["Players should be polite."].normative == "should"


# ------------------------------------------------------------------ analysis


def test_complete_sample_brief_has_no_questions(repo):
    spec, report = repo.ingest(RUNE)
    assert spec.status == SpecStatus.DRAFT
    assert spec.questions == []
    assert spec.release_countries == ["India"]
    assert report.from_version is None and len(report.added) == len(spec.requirements)


def test_flawed_brief_raises_every_class_of_question(repo):
    spec, _ = repo.ingest(FLAWED)
    assert spec.status == SpecStatus.NEEDS_INPUT
    cats = {}
    for q in spec.questions:
        cats.setdefault(q.category, []).append(q)
    topics = {q.detail.get("topic") for q in cats["missing_rule"]}
    assert {"draw_rule", "turn_timer", "randomness", "rematch"} <= topics
    texts = " ".join(q.text for q in cats["contradiction"])
    assert "disagree about health" in texts  # 20 vs 25 starting health
    assert "refers to round 12" in texts  # beyond "at most 8 rounds"
    assert "says 4 glyph types but lists 3" in texts
    assert "permits what" in texts  # can skip vs cannot skip
    assert cats["ambiguous"][0].detail["term"] == "TBD"
    assert cats["unsupported_feature"][0].detail["unsupported"] == "realtime"
    # Forge asked; it invented nothing: no requirement came from anywhere but the document.
    assert all(r.source == "document" for r in spec.requirements)


def test_enumerated_item_without_a_rule_is_a_missing_rule(tmp_path, template):
    doc = read_document(write(tmp_path, "b.md", "# G\n\n## Runes\n\n- There are three rune types: Fire, Water, Earth.\n"
                                                 "- Fire beats Earth.\n- Earth beats Fire.\n"))
    qs = analyse(assign_ids(extract_requirements(doc), None), template)
    undefined = [q for q in qs if q.detail.get("item")]
    assert [q.detail["item"] for q in undefined] == ["Water"]
    assert "What is the rule for Water?" in undefined[0].text


def test_prose_counts_are_not_false_enumerations(tmp_path, template):
    doc = read_document(write(tmp_path, "b.md", "# G\n\n## Setup\n\n- A bag holds 12 runes: four of each rune type.\n"))
    qs = analyse(assign_ids(extract_requirements(doc), None), template)
    assert not [q for q in qs if q.category == "contradiction"]


# ------------------------------------------------------------------ answers, dismissals, approval


def test_answers_become_owner_requirements_and_overrides(repo):
    spec, _ = repo.ingest(FLAWED)
    health = next(q for q in spec.questions if "disagree about health" in q.text)
    spec = repo.answer(health.id, "Each player starts with 20 health.", by="owner", overrides=health.requirement_ids)
    owner = [r for r in spec.requirements if r.source == f"owner_answer:{health.id}"]
    assert len(owner) == 1 and owner[0].text == "Each player starts with 20 health."
    assert all(spec.requirement(r).status == RequirementStatus.OVERRIDDEN for r in health.requirement_ids)
    assert spec.question(health.id).status == QuestionStatus.ANSWERED
    with pytest.raises(SpecError):
        repo.answer(health.id, "again", by="owner")
    with pytest.raises(SpecError):
        repo.answer(spec.open_blocking()[0].id, "   ", by="owner")
    assert any(e["event"] == "question_answered" for e in repo.events())


def test_approval_needs_every_blocking_question_resolved_and_countries(repo):
    spec, _ = repo.ingest(FLAWED)
    with pytest.raises(SpecError, match="open questions"):
        repo.approve(spec.version, by="owner")
    for q in spec.open_blocking():
        if q.category == "unsupported_feature":
            repo.answer(q.id, "Out of scope: no real-time dodging.", by="owner", overrides=q.requirement_ids)
        elif q.category == "ambiguous":
            repo.dismiss(q.id, "Bonus damage is cut from this game.", by="owner")
        else:
            repo.answer(q.id, f"Owner rule for {q.id}.", by="owner", overrides=[])
    spec = repo.latest()
    assert spec.status == SpecStatus.DRAFT
    with pytest.raises(SpecError, match="release countries"):
        repo.approve(spec.version, by="owner")
    approved = repo.approve(spec.version, by="owner", release_countries=["IN"])
    assert approved.status == SpecStatus.APPROVED and approved.approved_by == "owner"
    with pytest.raises(SpecError, match="immutable"):
        repo.answer("Q-x", "y", by="owner")


def test_dismiss_requires_a_reason(repo):
    spec, _ = repo.ingest(FLAWED)
    with pytest.raises(SpecError):
        repo.dismiss(spec.questions[0].id, " ", by="owner")


def test_acceptance_cases_are_frozen_at_approval(repo):
    spec, _ = repo.ingest(RUNE)
    assert repo.acceptance_cases() == []
    spec = repo.approve(spec.version, by="owner")
    cases = repo.acceptance_cases()
    assert len(cases) == len(spec.plannable())
    assert {c.id for c in cases} == {f"AC-{r.id}" for r in spec.plannable()}
    assert all(c.created_in == 1 for c in cases)


# ------------------------------------------------------------------ change reports and protection


def test_unchanged_document_creates_no_new_version(repo):
    s1, _ = repo.ingest(RUNE)
    s2, report = repo.ingest(RUNE)
    assert s2.version == s1.version and report.empty and not report.source_changed
    assert repo.versions() == [1]


def test_change_report_and_stable_ids(repo, tmp_path):
    s1, _ = repo.ingest(RUNE)
    text = RUNE.read_text()
    edited = text.replace("Each player starts with 20 health.", "Each player starts with 30 health.") \
        .replace("- A bag holds 12 runes: four of each rune type.\n", "") \
        .replace("## Rematch\n", "## Rematch\n\n- A rematch keeps the same bot difficulty.\n")
    s2, report = repo.ingest(write(tmp_path, "brief.md", edited))
    assert s2.version == 2 and s2.previous_version == 1
    old_health = next(r for r in s1.requirements if r.text == "Each player starts with 20 health.")
    assert [c.id for c in report.modified] == [old_health.id]
    assert report.modified[0].after == "Each player starts with 30 health."
    assert [c.before for c in report.removed] == ["A bag holds 12 runes: four of each rune type."]
    assert [c.after for c in report.added] == ["A rematch keeps the same bot difficulty."]
    # The new requirement gets a fresh ID that was never used before.
    assert report.added[0].id not in {r.id for r in s1.requirements}
    unchanged = {r.id: r.text for r in s1.requirements if r.text in edited}
    assert all(s2.requirement(i).text == t for i, t in unchanged.items())
    md = (repo.root / "v2" / "changes.md").read_text()
    assert "Modified" in md and "30 health" in md
    assert s1.status == SpecStatus.DRAFT and repo.get(1).status == SpecStatus.SUPERSEDED


def test_approved_behaviour_is_never_silently_rewritten(repo, tmp_path):
    s1, _ = repo.ingest(RUNE)
    repo.approve(1, by="owner")
    hid = next(r.id for r in s1.requirements if r.text == "Each player starts with 20 health.")
    bag = next(r.id for r in s1.requirements if r.text.startswith("A bag holds 12 runes"))
    edited = RUNE.read_text().replace("starts with 20 health", "starts with 30 health") \
        .replace("- A bag holds 12 runes: four of each rune type.\n", "")
    s2, report = repo.ingest(write(tmp_path, "brief.md", edited))
    r = s2.requirement(hid)
    assert r.status == RequirementStatus.CHANGE_PROPOSED
    assert r.effective_text == "Each player starts with 20 health."  # approved text stays effective
    assert r.text == "Each player starts with 30 health."
    gone = s2.requirement(bag)
    assert gone.status == RequirementStatus.REMOVAL_PROPOSED and gone.effective_text.startswith("A bag holds 12")
    assert set(report.approved_changes_pending) == {hid, bag}
    with pytest.raises(SpecError, match="proposed changes"):
        repo.approve(2, by="owner")

    repo.accept_change(hid, by="owner")
    repo.reject_change(bag, by="owner")
    s2 = repo.latest()
    assert s2.requirement(hid).effective_text == "Each player starts with 30 health."
    assert s2.requirement(bag).status == RequirementStatus.ACTIVE  # kept: removal rejected
    approved = repo.approve(2, by="owner")
    assert repo.changed_since_approval(2) == [hid]
    cases = repo.acceptance_cases()
    new_case = next(c for c in cases if c.requirement_id == hid)
    assert new_case.id == f"AC-{hid}.2" and new_case.created_in == 2
    assert repo.superseded_cases() == {f"AC-{hid}": 2}
    old_case = next(c for c in repo.acceptance_cases(include_superseded=True) if c.id == f"AC-{hid}")
    assert old_case.description == "Each player starts with 20 health."  # never edited
    assert approved.digest != s1.digest

    # The same document again keeps the earlier decisions (no repeated prompts).
    edited2 = edited + "\n"
    s3, _ = repo.ingest(write(tmp_path, "brief2.md", edited2))
    assert s3.requirement(hid).status == RequirementStatus.ACTIVE
    assert s3.requirement(bag).status == RequirementStatus.ACTIVE


def test_answers_carry_forward_to_the_next_version(repo, tmp_path):
    spec, _ = repo.ingest(FLAWED)
    q = next(q for q in spec.questions if q.detail.get("topic") == "rematch")
    repo.answer(q.id, "A rematch swaps the first player.", by="owner")
    s2, _ = repo.ingest(write(tmp_path, "b.md", FLAWED.read_text() + "\n## Extra\n\n- Glyphs glow.\n"))
    assert s2.question(q.id).status == QuestionStatus.ANSWERED
    assert any(r.source == f"owner_answer:{q.id}" for r in s2.requirements)


# ------------------------------------------------------------------ optional model pass


def _policy(**kw):
    return ProjectPolicy(allowed_vendors=["fake"], allowed_data_classes=["prompt", "code", "design_document"], **kw)


def test_model_pass_adds_questions_but_never_rules(repo):
    provider = FakeProvider(review_verdict="reject")
    spec, _ = repo.ingest(RUNE, model_pass=lambda s, text: run_model_pass(s, text, provider, _policy()).questions)
    model_qs = [q for q in spec.questions if q.category == "model_finding"]
    assert len(model_qs) == 1 and model_qs[0].source == "model:fake"
    assert model_qs[0].requirement_ids  # cites a real requirement
    assert not model_qs[0].blocking  # an uncategorised model finding informs; it does not block
    assert all(r.source == "document" for r in spec.requirements)
    req = provider.reviews[0]
    assert "Rune Duel" in req.diff and req.idempotency_key.startswith("intake-game-")


def test_model_pass_respects_policy_and_ceiling(repo):
    spec, _ = repo.ingest(RUNE)
    with pytest.raises(PolicyViolation, match="design_document"):
        run_model_pass(spec, "text", FakeProvider(), ProjectPolicy(allowed_vendors=["fake"]))
    with pytest.raises(ModelPassUnavailable):
        run_model_pass(spec, "text", FakeProvider(ceiling_micros=None), _policy())


def test_model_pass_reserves_and_settles_budget(repo):
    spec, _ = repo.ingest(RUNE)
    calls = []

    class Hook:
        def reserve(self, amount, purpose):
            calls.append(("reserve", amount))
            return "res1"

        def settle(self, rid, actual):
            calls.append(("settle", actual))

    run_model_pass(spec, "text", FakeProvider(), _policy(), budget=Hook())
    assert calls == [("reserve", 50_000), ("settle", 2_000)]
