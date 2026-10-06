"""Review-page section for asset stages: before/after renders, budgets, provenance and the gate checklist.

Rendered inside panel 4 ("Visual / play evidence") of the existing five-panel review page from the
``generation-lane`` evidence (``details.asset_stage``). Images are served from the content-addressed
artifact store (``/artifact-image/<sha256>``); nothing is loaded from outside 127.0.0.1.
"""

from __future__ import annotations

import html
from typing import Iterable

from ..models import Evidence

e = html.escape


def _imgs(title: str, refs: dict[str, str]) -> str:
    if not refs:
        return f"<p class=mute>{e(title)}: none</p>"
    figs = "".join(f"<figure style='display:inline-block;margin:4px'><img src='/artifact-image/{e(h)}' width=150 "
                   f"height=150 alt='{e(name)}'><figcaption class=mute>{e(name)}</figcaption></figure>"
                   for name, h in sorted(refs.items()))
    return f"<h4>{e(title)}</h4><div>{figs}</div>"


def _budget_table(lines: list[dict]) -> str:
    if not lines:
        return ""
    rows = "".join(f"<tr><td>{e(str(x.get('metric')))}</td><td>{e(str(x.get('value')))}</td>"
                   f"<td>{e(str(x.get('ceiling')))}</td><td><span class='pill {'PASS' if x.get('ok') else 'FAIL'}'>"
                   f"{'within' if x.get('ok') else 'OVER'}</span> <span class=mute>{e(x.get('note') or '')}</span>"
                   f"</td></tr>" for x in lines)
    return ("<h4>Budgets (plan ceilings; the phone build remains the authority)</h4><table><tr><th>Metric</th>"
            f"<th>Value</th><th>Ceiling</th><th></th></tr>{rows}</table>")


def _provenance(p: dict) -> str:
    if not p:
        return ""
    probs = "".join(f"<li>{e(x)}</li>" for x in p.get("problems") or [])
    excl = ", ".join(p.get("excluded_territories") or []) or "none"
    return (f"<h4>Provenance</h4><p>Route <b>{e(str(p.get('route')))}</b> · model {e(str(p.get('model')))} "
            f"{e(str(p.get('model_version') or ''))} · service {e(str(p.get('service')))}</p>"
            f"<p>Terms: {e(', '.join(p.get('terms') or []) or 'none recorded')} · territory exclusions: {e(excl)} · "
            f"distribution: {e(', '.join(p.get('distribution') or []))} · failed attempts: "
            f"{e(str(p.get('failed_attempts', 0)))}</p>"
            f"<p class=mute>Inputs: {e('; '.join(p.get('inputs') or []))}</p>"
            + (f"<ul class=mute>{probs}</ul>" if probs else ""))


def asset_stage_html(evidence: Iterable[Evidence]) -> str:
    out = []
    for ev in evidence:
        st = (ev.details or {}).get("asset_stage")
        if not st:
            continue
        review = st.get("review") or {}
        gate = st.get("gate")
        renders = st.get("renders") or {}
        head = (f"<h3>Asset <code>{e(str(st.get('asset_id')))}</code> v{e(str(st.get('version')))} · "
                + (f"gate <span class='pill PENDING'>{e(gate.replace('_', ' ').upper())}</span>" if gate else
                   "<span class='pill INFO'>no gate reached</span>") + "</h3>")
        stages = "".join(f"<tr><td>{e(k)}</td><td>{e(v)}</td></tr>"
                         for k, v in ((st.get("lane") or {}).get("stages") or {}).items())
        checklist = "".join(f"<li>{e(c)}</li>" for c in review.get("check") or [])
        problems = review.get("contract_problems") or []
        ok_pill = "<span class='pill PASS'>ok</span>"
        clip_rows = "".join(
            f"<tr><td>{e(c)}</td><td>{ok_pill if not v.get('problems') else ''}"
            f"{e('; '.join(v.get('problems') or []))}</td><td>{e(', '.join(x['name'] for x in v.get('events') or []))}"
            f"{' (proposed)' if v.get('markers_proposed') else ''}</td></tr>"
            for c, v in (review.get("clips") or {}).items())
        body = [
            head,
            _imgs("Before (provider output, unmodified)", renders.get("before") or {}),
            _imgs("After (normalized, as it ships)", renders.get("after") or {}),
            _budget_table(review.get("budget") or []),
            ("<p><span class='pill FAIL'>CONTRACT</span> " + e("; ".join(problems)) + "</p>") if problems else "",
            (f"<h4>Clips</h4><table><tr><th>Clip</th><th>Checks</th><th>Markers</th></tr>{clip_rows}</table>"
             if clip_rows else ""),
            _provenance(review.get("provenance") or {}),
            (f"<h4>Check before approving</h4><ul>{checklist}</ul>" if checklist else ""),
            (f"<details><summary>Lane stages</summary><table>{stages}</table></details>" if stages else ""),
            ("<p class=mute>" + e("; ".join(st.get("notes") or [])) + "</p>") if st.get("notes") else "",
            "<p class=mute>Approving the exact hash approves this gate only; any later change to these bytes "
            "needs a fresh approval.</p>",
        ]
        out.append("<div class=card>" + "".join(body) + "</div>")
    return "".join(out)
