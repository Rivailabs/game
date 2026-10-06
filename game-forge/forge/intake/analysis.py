"""Rule-based ambiguity checks: missing rules, contradictions, vague wording, unsupported features.

Every finding becomes a :class:`~forge.intake.spec.Question` for the owner. The checks never
choose an answer: a contradiction lists both statements, a missing rule asks the template's
question. The checks are heuristics, so they can miss a contradiction or raise a false one; the
owner dismisses a false finding with a recorded reason (never silently). An optional model pass
(:mod:`forge.intake.model_pass`) can add findings but cannot resolve any.

Checks:

* **missing_rule**: a template ``required_topics`` entry has no matching word in any rule
  requirement; or an enumerated item ("three rune types: Fire, Water, Earth") is never used by
  any other rule ("the rule for Earth is undefined").
* **contradiction**: two requirements give different numbers for the same quantity (same unit,
  similar statement); a requirement references a round/turn beyond a stated maximum; an
  enumeration's count disagrees with its list; one requirement permits what another forbids.
* **ambiguous**: TBD/TODO/"etc."/"maybe"/question marks and similar unresolved wording.
* **unsupported_feature**: the brief asks for something the template declares out of scope.
"""

from __future__ import annotations

import hashlib
import re
from itertools import combinations

from ..planning.template import Template
from .spec import Question, Requirement, normalise

NUMBER_WORDS = {"zero": 0, "one": 1, "two": 2, "three": 3, "four": 4, "five": 5, "six": 6, "seven": 7,
                "eight": 8, "nine": 9, "ten": 10, "eleven": 11, "twelve": 12}
_NUM = r"(\d+|zero|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)"

STOPWORDS = frozenset("""a an the and or of to in on at by for with from is are be been was were it its this that
these those each every any all their his her they them there then than as into onto per can cannot may must shall
will would should could not no do does did has have had when if after before while unless until during so such
also only just which who whom what where how up down out over under again same own one more most other some""".split())

#: Words treated as the same concept when comparing statements.
SYNONYMS = {"begin": "start", "begins": "start", "starts": "start", "starting": "start", "initial": "start",
            "initially": "start", "has": "have", "holds": "hold", "players": "player", "loses": "lose",
            "lost": "lose", "rounds": "round", "turns": "turn", "seconds": "second", "secs": "second",
            "hp": "health", "hit": "health", "points": "point", "gains": "gain", "draws": "draw", "wins": "win"}

_CONDITIONAL = re.compile(r"\b(when|if|after|before|unless|while|until|whenever|once)\b", re.I)
_NUM_UNIT = re.compile(r"\b" + _NUM + r"\s*(%|[a-z]+)", re.I)
_MAX = re.compile(r"\b(?:at most|up to|maximum of|a maximum of|no more than)\s+" + _NUM + r"\s+([a-z]+)", re.I)
_ORDINAL_REF = re.compile(r"\b(round|turn)\s+(\d+)\b", re.I)
_ENUM = re.compile(r"\b" + _NUM + r"\s+((?:[a-z]+\s+){0,2}?(?:types|kinds|classes|elements|weapons|cards|runes|"
                   r"pieces|units|actions|moves|modes|colours|colors|suits|categories))\s*:\s*(.+)$", re.I)
_VAGUE = re.compile(r"\b(TBD|TBC|TODO|to be decided|to be determined|to be confirmed|etc\.?|and so on|somehow|"
                    r"maybe|probably|perhaps|some kind of|as appropriate|as needed|something like|roughly|"
                    r"approximately|reasonable|nice|fun)\b|\?\s*$", re.I)
_NEGATIVE = re.compile(r"\b(cannot|can not|can't|may not|must not|mustn't|is not allowed to|are not allowed to|"
                       r"never|is forbidden to|are forbidden to)\b", re.I)
_POSITIVE = re.compile(r"\b(can|may|must|is allowed to|are allowed to|always)\b", re.I)

#: Units that are not quantities of the game (avoid "3 dp" style false contradictions where harmless).
_IGNORED_UNITS = frozenset({"by", "and", "or", "of", "to", "x", "dp", "px", "pt", "ms", "the", "a"})


def _num(s: str) -> int:
    s = s.lower()
    return NUMBER_WORDS[s] if s in NUMBER_WORDS else int(s)


def _singular(w: str) -> str:
    w = SYNONYMS.get(w, w)
    if len(w) > 3 and w.endswith("s") and not w.endswith("ss"):
        w = w[:-1]
    return SYNONYMS.get(w, w)


def content_words(text: str) -> set[str]:
    return {_singular(w) for w in normalise(text).split() if w not in STOPWORDS and not w.isdigit()
            and w not in NUMBER_WORDS}


def jaccard(a: set[str], b: set[str]) -> float:
    return len(a & b) / len(a | b) if a | b else 0.0


def qid(category: str, key: str) -> str:
    return f"Q-{category[:4]}-{hashlib.sha256(f'{category}|{key}'.encode()).hexdigest()[:8]}"


def _word_in(word: str, text: str) -> bool:
    return re.search(r"(?<![a-z])" + re.escape(word.lower()) + r"(?![a-z])", text.lower()) is not None


def _quantity_facts(r: Requirement) -> list[tuple[str, int]]:
    facts = []
    for m in _NUM_UNIT.finditer(r.effective_text):
        unit = _singular(m.group(2).lower())
        if unit in _IGNORED_UNITS or unit in STOPWORDS:
            continue
        facts.append((unit, _num(m.group(1))))
    return facts


def check_missing_topics(reqs: list[Requirement], template: Template) -> list[Question]:
    corpus = " ".join(r.effective_text for r in reqs if r.plannable and r.kind in ("rule", "feature"))
    out = []
    for topic in template.required_topics:
        if not any(_word_in(w, corpus) for w in topic.any):
            out.append(Question(id=qid("missing_rule", topic.id), category="missing_rule", text=topic.question,
                                detail={"topic": topic.id, "searched_for": list(topic.any)}))
    return out


def check_unsupported(reqs: list[Requirement], template: Template) -> list[Question]:
    out = []
    for u in template.unsupported:
        hits = [r for r in reqs if u.pattern.search(r.effective_text)]
        if hits:
            quoted = "; ".join(f'{r.id}: "{r.effective_text}"' for r in hits[:3])
            out.append(Question(
                id=qid("unsupported_feature", u.id), category="unsupported_feature",
                text=f"The brief asks for something outside the {template.id} template ({u.reason}) - {quoted}. "
                     "Remove it from this game, or record it as out of scope for a separate expansion?",
                requirement_ids=[r.id for r in hits], detail={"unsupported": u.id}))
    return out


def check_vague(reqs: list[Requirement]) -> list[Question]:
    out = []
    for r in reqs:
        if not r.plannable:
            continue
        m = _VAGUE.search(r.effective_text)
        if m:
            term = m.group(0).strip()
            out.append(Question(
                id=qid("ambiguous", f"{r.id}|{term.lower()}"), category="ambiguous",
                text=f'{r.id} is not specific enough to test ("{term}"): "{r.effective_text}". '
                     "What exactly should happen?",
                requirement_ids=[r.id], blocking=r.kind in ("rule", "feature"), detail={"term": term}))
    return out


def check_contradictions(reqs: list[Requirement]) -> list[Question]:
    out: list[Question] = []
    plannable = [r for r in reqs if r.plannable]
    words = {r.id: content_words(r.effective_text) for r in plannable}

    # 1. Different numbers for the same quantity in similar statements.
    for a, b in combinations(plannable, 2):
        fa, fb = _quantity_facts(a), _quantity_facts(b)
        if not fa or not fb:
            continue
        sim = jaccard(words[a.id], words[b.id])
        cond_a, cond_b = bool(_CONDITIONAL.search(a.effective_text)), bool(_CONDITIONAL.search(b.effective_text))
        comparable = sim >= 0.6 or (a.section == b.section and not cond_a and not cond_b and sim >= 0.3)
        if not comparable:
            continue
        for unit, va in fa:
            vb = [v for u, v in fb if u == unit]
            if vb and va not in vb:
                out.append(Question(
                    id=qid("contradiction", f"{a.id}|{b.id}|{unit}"), category="contradiction",
                    text=f'{a.id} and {b.id} disagree about {unit}: "{a.effective_text}" vs "{b.effective_text}". '
                         "Which value is correct?",
                    requirement_ids=[a.id, b.id], detail={"unit": unit, "values": [va, vb[0]]}))
                break

    # 2. A reference to round/turn N beyond a stated maximum.
    maxima: dict[str, tuple[int, Requirement]] = {}
    for r in plannable:
        for m in _MAX.finditer(r.effective_text):
            maxima[_singular(m.group(2).lower())] = (_num(m.group(1)), r)
    for r in plannable:
        for m in _ORDINAL_REF.finditer(r.effective_text):
            unit, n = _singular(m.group(1).lower()), int(m.group(2))
            if unit in maxima and n > maxima[unit][0] and maxima[unit][1].id != r.id:
                limit, src = maxima[unit]
                out.append(Question(
                    id=qid("contradiction", f"{src.id}|{r.id}|{unit}-max"), category="contradiction",
                    text=f'{r.id} refers to {unit} {n}, but {src.id} allows at most {limit}: '
                         f'"{r.effective_text}" vs "{src.effective_text}". Which is correct?',
                    requirement_ids=[src.id, r.id], detail={"unit": unit, "max": limit, "referenced": n}))

    # 3. Enumeration count disagrees with the listed items.
    for r in plannable:
        e = _enumeration(r)
        if e and e[0] != len(e[2]):
            out.append(Question(
                id=qid("contradiction", f"{r.id}|enum-count"), category="contradiction",
                text=f'{r.id} says {e[0]} {e[1]} but lists {len(e[2])} ({", ".join(e[2])}): "{r.effective_text}". '
                     "Which is correct, and what is missing or extra?",
                requirement_ids=[r.id], detail={"stated": e[0], "listed": e[2]}))

    # 4. One requirement permits what another forbids.
    for a, b in combinations(plannable, 2):
        na, nb = bool(_NEGATIVE.search(a.effective_text)), bool(_NEGATIVE.search(b.effective_text))
        if na == nb:
            continue
        pos, neg = (b, a) if na else (a, b)
        if not _POSITIVE.search(pos.effective_text):
            continue
        strip = lambda t: content_words(_NEGATIVE.sub(" ", _POSITIVE.sub(" ", t)))  # noqa: E731
        if jaccard(strip(pos.effective_text), strip(neg.effective_text)) >= 0.75:
            out.append(Question(
                id=qid("contradiction", f"{a.id}|{b.id}|polarity"), category="contradiction",
                text=f'{pos.id} permits what {neg.id} forbids: "{pos.effective_text}" vs "{neg.effective_text}". '
                     "Which rule applies?",
                requirement_ids=[a.id, b.id], detail={"kind": "polarity"}))
    return out


def _enumeration(r: Requirement) -> tuple[int, str, list[str]] | None:
    m = _ENUM.search(r.effective_text)
    if not m:
        return None
    raw = re.sub(r"[.;]\s*$", "", m.group(3))
    items = [i.strip() for i in re.split(r",|\band\b|\bor\b", raw) if i.strip()]
    items = [re.sub(r"^(the|a|an)\s+", "", i, flags=re.I) for i in items]
    # Only a list of names counts ("Fire, Water, Earth"), not prose ("four of each rune type").
    if len(items) < 2 or any(len(i.split()) > 3 for i in items):
        return None
    return _num(m.group(1)), m.group(2).strip(), items


def check_undefined_items(reqs: list[Requirement]) -> list[Question]:
    """Each enumerated item (rune type, weapon, ...) needs at least one rule that uses it."""
    out = []
    plannable = [r for r in reqs if r.plannable and r.kind in ("rule", "feature")]
    for r in plannable:
        e = _enumeration(r)
        if not e:
            continue
        for item in e[2]:
            used = any(_word_in(item, o.effective_text) for o in plannable if o.id != r.id)
            if not used:
                out.append(Question(
                    id=qid("missing_rule", f"{r.id}|{item.lower()}"), category="missing_rule",
                    text=f'{r.id} lists "{item}" among the {e[1]}, but no rule says what {item} does. '
                         f"What is the rule for {item}?",
                    requirement_ids=[r.id], detail={"item": item}))
    return out


def analyse(reqs: list[Requirement], template: Template) -> list[Question]:
    """All rule-based findings, de-duplicated by question id (stable across versions)."""
    found: dict[str, Question] = {}
    for q in (check_unsupported(reqs, template) + check_missing_topics(reqs, template) + check_contradictions(reqs)
              + check_undefined_items(reqs) + check_vague(reqs)):
        found.setdefault(q.id, q)
    return list(found.values())
