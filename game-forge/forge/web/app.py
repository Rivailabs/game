"""Local review web UI (stdlib WSGI; bind to 127.0.0.1 only).

Pages: dashboard, task list by state, task review (five panels), budget, kill switch.
Every POST carries a per-process CSRF token. Secret values never appear: all text
shown is either structured state or redacted artifact content.
"""

from __future__ import annotations

import html
import json
import secrets
from typing import Callable, Iterable
from urllib.parse import parse_qs

from ..budget import BudgetLedger, CapacityLedger, CashLedger, milestone_scope, project_scope, root_scope
from ..models import Decision, EvidenceClass, EvidenceStatus, ReviewAction, TaskState
from ..orchestrator import Orchestrator, ReviewError
from ..store import Store, StoreError
from ..util import iso, micros_to_usd

e = html.escape

CSS = """
:root{--bg:#f6f7f9;--card:#fff;--ink:#1d2330;--mute:#5d6678;--line:#dde1e8;--accent:#2f5bd3;
--ok:#1f7a4d;--bad:#b3261e;--warn:#946200;--info:#44546a}
*{box-sizing:border-box}body{margin:0;font:15px/1.5 system-ui,-apple-system,Segoe UI,Roboto,sans-serif;
background:var(--bg);color:var(--ink)}a{color:var(--accent);text-decoration:none}a:hover{text-decoration:underline}
header{background:#1d2330;color:#fff;padding:12px 24px;display:flex;gap:24px;align-items:center;flex-wrap:wrap}
header a{color:#cfd8ea}header strong{font-size:17px;color:#fff}main{max-width:1180px;margin:0 auto;padding:20px 16px}
.card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:16px 18px;margin:0 0 16px}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(260px,1fr));gap:16px}
h1{font-size:22px;margin:4px 0 14px}h2{font-size:17px;margin:0 0 10px}h3{font-size:15px;margin:12px 0 6px}
table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:6px 8px;border-bottom:1px solid var(--line);
vertical-align:top}th{color:var(--mute);font-weight:600;font-size:13px}
.pill{display:inline-block;padding:1px 8px;border-radius:999px;font-size:12px;font-weight:600;border:1px solid}
.PASS,.ACCEPTED,.APPROVED{color:var(--ok);border-color:var(--ok)}
.FAIL,.FAILED,.REJECTED,.CANCELLED{color:var(--bad);border-color:var(--bad)}
.INCOMPLETE,.BLOCKED,.PAUSED,.PENDING,.NEEDS_INPUT,.AWAITING_APPROVAL,.RETRY_PENDING{color:var(--warn);border-color:var(--warn)}
.INFO,.DRAFT,.READY,.RUNNING,.VERIFYING,.INTEGRATING,.INTEGRATION_READY,.NOT_REQUIRED,.CANCEL_REQUESTED{color:var(--info);border-color:var(--info)}
.mute{color:var(--mute)}.big{font-size:26px;font-weight:700}
details{margin:6px 0}summary{cursor:pointer;color:var(--mute)}pre{background:#f0f2f6;padding:10px;border-radius:6px;
overflow:auto;max-height:420px;font-size:12.5px;white-space:pre-wrap;word-break:break-word}
form.inline{display:inline}button{font:inherit;padding:6px 14px;border-radius:6px;border:1px solid var(--line);
background:#fff;cursor:pointer}button.primary{background:var(--accent);color:#fff;border-color:var(--accent)}
button.danger{background:var(--bad);color:#fff;border-color:var(--bad)}
input[type=text],select,textarea{font:inherit;padding:6px 8px;border:1px solid var(--line);border-radius:6px;width:100%}
.actions{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:12px}
.banner{padding:10px 14px;border-radius:8px;margin-bottom:16px;font-weight:600}
.banner.stop{background:#fdecea;color:var(--bad);border:1px solid #f3b8b3}
.diff-add{color:#1f7a4d}.diff-del{color:#b3261e}
"""


def _pill(v: str) -> str:
    return f'<span class="pill {e(v)}">{e(v)}</span>'


class ReviewApp:
    def __init__(self, store: Store, orchestrators: dict[str, Orchestrator], reviewer: str = "owner"):
        self.store = store
        self.orch = orchestrators
        self.reviewer = reviewer
        self.csrf = secrets.token_urlsafe(24)
        self.budget = BudgetLedger(store)

    # ------------------------------------------------------------ WSGI
    def __call__(self, environ: dict, start_response: Callable) -> Iterable[bytes]:
        method = environ.get("REQUEST_METHOD", "GET")
        path = environ.get("PATH_INFO", "/") or "/"
        query = {k: v[0] for k, v in parse_qs(environ.get("QUERY_STRING", "")).items()}
        form: dict[str, str] = {}
        if method == "POST":
            try:
                n = int(environ.get("CONTENT_LENGTH") or 0)
            except ValueError:
                n = 0
            body = environ["wsgi.input"].read(n).decode("utf-8") if n else ""
            form = {k: v[0] for k, v in parse_qs(body, keep_blank_values=True).items()}
            if not secrets.compare_digest(form.get("csrf", ""), self.csrf):
                return self._respond(start_response, "403 Forbidden", self._page("Forbidden", "<p>Bad CSRF token.</p>"))
        try:
            status, body, headers = self.route(method, path, query, form)
        except (StoreError, KeyError) as ex:
            status, body, headers = "404 Not Found", self._page("Not found", f"<p>{e(str(ex))}</p>"), []
        except ReviewError as ex:
            status, body, headers = "409 Conflict", self._page("Action refused", f'<div class="card"><p>{e(str(ex))}</p>'
                                                                               '<p><a href="/">Back to dashboard</a></p></div>'), []
        return self._respond(start_response, status, body, headers)

    def _respond(self, start_response, status, body, headers=None):
        data = body.encode("utf-8") if isinstance(body, str) else body
        hdrs = [("Content-Type", "text/html; charset=utf-8"), ("Content-Length", str(len(data))),
                ("X-Frame-Options", "DENY"), ("Content-Security-Policy", "default-src 'self'; style-src 'unsafe-inline'"),
                ("Referrer-Policy", "no-referrer")] + [h for h in (headers or []) if h[0] != "Content-Type"]
        for h in headers or []:
            if h[0] == "Content-Type":
                hdrs[0] = h
        start_response(status, hdrs)
        return [data]

    def route(self, method: str, path: str, q: dict, form: dict):
        parts = [p for p in path.split("/") if p]
        if method == "GET":
            if not parts:
                return "200 OK", self.dashboard(), []
            if parts == ["tasks"]:
                return "200 OK", self.task_list(q.get("state"), q.get("project")), []
            if len(parts) == 2 and parts[0] == "task":
                return "200 OK", self.task_page(parts[1]), []
            if parts == ["budget"]:
                return "200 OK", self.budget_page(), []
            if len(parts) == 2 and parts[0] == "artifact":
                return "200 OK", self.artifact_page(parts[1]), []
            if parts == ["api", "status"]:
                data = json.dumps({pid: o.status() for pid, o in self.orch.items()})
                return "200 OK", data, [("Content-Type", "application/json")]
        if method == "POST":
            if len(parts) == 3 and parts[0] == "task" and parts[2] == "action":
                return self.task_action(parts[1], form)
            if parts == ["kill-switch"]:
                if form.get("op") == "stop":
                    self.budget.stop_dispatch(by=self.reviewer, reason=form.get("reason", "review page"))
                else:
                    self.budget.resume_dispatch(by=self.reviewer)
                return "303 See Other", "", [("Location", "/")]
        return "404 Not Found", self._page("Not found", "<p>No such page.</p>"), []

    # ------------------------------------------------------------ layout
    def _page(self, title: str, body: str) -> str:
        stop = ('<div class="banner stop">Dispatch is STOPPED by the owner. Committed jobs remain visible until settled.</div>'
                if self.budget.dispatch_stopped() else "")
        return (f"<!doctype html><html lang=en><head><meta charset=utf-8><meta name=viewport content='width=device-width,"
                f"initial-scale=1'><title>{e(title)} · Game Forge</title><style>{CSS}</style></head><body><header>"
                f"<strong>Game Forge</strong><a href='/'>Dashboard</a><a href='/tasks'>Tasks</a>"
                f"<a href='/budget'>Budget</a><span class=mute style='margin-left:auto'>local review · 127.0.0.1</span>"
                f"</header><main>{stop}{body}</main></body></html>")

    def _kill_switch_form(self) -> str:
        if self.budget.dispatch_stopped():
            return (f"<form method=post action='/kill-switch' class=inline><input type=hidden name=csrf value='{self.csrf}'>"
                    f"<input type=hidden name=op value=resume><button class=primary>Resume dispatch</button></form>")
        return (f"<form method=post action='/kill-switch' class=inline><input type=hidden name=csrf value='{self.csrf}'>"
                f"<input type=hidden name=op value=stop><button class=danger>Stop all new dispatch</button></form>")

    # ------------------------------------------------------------ pages
    def dashboard(self) -> str:
        cards = []
        for p in self.store.list_projects():
            roots = self.store.list_roots(p.id)
            counts: dict[str, int] = {}
            for r in roots:
                counts[r.state.value] = counts.get(r.state.value, 0) + 1
            rows = "".join(f"<tr><td>{_pill(s)}</td><td><a href='/tasks?project={e(p.id)}&state={e(s)}'>{n}</a></td></tr>"
                           for s, n in sorted(counts.items()))
            ps = self.budget.summary(project_scope(p.id))
            attention = [r for r in roots if r.state in (TaskState.AWAITING_APPROVAL, TaskState.NEEDS_INPUT,
                                                         TaskState.PAUSED, TaskState.BLOCKED)]
            att = "".join(f"<tr><td><a href='/task/{e(r.id)}'>{e(r.ticket or r.id)}</a></td><td>{e(r.title)}</td>"
                          f"<td>{_pill(r.state.value)}</td><td class=mute>{e(r.state_reason[:140])}</td></tr>"
                          for r in attention[:20]) or "<tr><td colspan=4 class=mute>Nothing needs you right now.</td></tr>"
            cards.append(
                f"<div class=card><h2>{e(p.name)} <span class=mute>({e(p.id)})</span></h2><div class=grid>"
                f"<div><h3>Tasks by state</h3><table>{rows or '<tr><td class=mute>no tasks</td></tr>'}</table></div>"
                f"<div><h3>Spend (model/API)</h3><div class=big>{micros_to_usd(ps.settled)}</div>"
                f"<div class=mute>settled · reserved {micros_to_usd(ps.reserved)} · cap {micros_to_usd(ps.cap)} · "
                f"available {micros_to_usd(ps.available)}</div><p>{self._kill_switch_form()}</p></div></div>"
                f"<h3>Needs attention</h3><table><tr><th>Ticket</th><th>Title</th><th>State</th><th>Reason</th></tr>{att}</table></div>")
        return self._page("Dashboard", "<h1>Projects</h1>" + ("".join(cards) or "<p>No projects. Run <code>forge init</code>.</p>"))

    def task_list(self, state: str | None, project: str | None) -> str:
        states = [TaskState(state)] if state else None
        roots = self.store.list_roots(project, states)
        groups: dict[str, list] = {}
        for r in roots:
            groups.setdefault(r.state.value, []).append(r)
        order = [s.value for s in TaskState]
        out = []
        filt = " ".join(f"<a href='/tasks?state={s}'>{_pill(s)}</a>" for s in order)
        for s in order:
            if s not in groups:
                continue
            rows = "".join(
                f"<tr><td><a href='/task/{e(r.id)}'>{e(r.ticket or r.id)}</a></td><td>{e(r.group or '')}</td>"
                f"<td>{e(r.title)}</td><td class=mute>{e(r.state_reason[:120])}</td></tr>" for r in groups[s])
            out.append(f"<div class=card><h2>{_pill(s)} <span class=mute>{len(groups[s])}</span></h2><table>"
                       f"<tr><th>Ticket</th><th>Group</th><th>Title</th><th>Reason</th></tr>{rows}</table></div>")
        return self._page("Tasks", f"<h1>Tasks</h1><p>{filt} <a href='/tasks'>all</a></p>" + ("".join(out) or "<p>No tasks.</p>"))

    def _evidence_rows(self, evs) -> str:
        rows = []
        for ev in evs:
            logs = ev.details.get("logs") or {}
            links = " ".join(f"<a href='/artifact/{e(h)}'>{e(n)}</a>" for n, h in logs.items())
            if ev.name == "diff-guard" and ev.details.get("diff"):
                links += f" <a href='/artifact/{e(ev.details['diff'])}'>diff</a>"
            extra = {k: v for k, v in ev.details.items() if k not in ("logs", "diff", "changed_files")}
            rows.append(
                f"<tr><td>{_pill(ev.status.value)}</td><td><b>{e(ev.name)}</b><br><span class=mute>{e(ev.evidence_class.value)}"
                f"</span></td><td>{e(ev.summary)}<details><summary>details</summary><pre>{e(json.dumps(extra, indent=2, default=str)[:6000])}"
                f"</pre></details></td><td>{links}</td><td class=mute>{e(iso(ev.created_at))}<br>"
                f"{e((ev.candidate_hash or '')[:12])}</td></tr>")
        return ("<table><tr><th>Status</th><th>Check</th><th>Result</th><th>Logs</th><th>When / hash</th></tr>"
                + "".join(rows) + "</table>") if rows else "<p class=mute>No evidence yet.</p>"

    def task_page(self, root_id: str) -> str:
        r = self.store.get_root(root_id)
        orch = self.orch.get(r.project_id)
        attempts = self.store.list_attempts(r.id)
        latest = attempts[-1] if attempts else None
        evs = self.store.list_evidence(r.id)
        cur_evs = [x for x in evs if latest and x.attempt_id == latest.id] if latest else []
        technical = [x for x in cur_evs if x.evidence_class in (EvidenceClass.RULES, EvidenceClass.REPLAY,
                                                                 EvidenceClass.INTEGRATION, EvidenceClass.STATIC,
                                                                 EvidenceClass.MODEL_REVIEW)]
        visual = [x for x in evs if x.evidence_class in (EvidenceClass.DEVICE, EvidenceClass.PERFORMANCE,
                                                         EvidenceClass.HUMAN)]
        # Panel 1: purpose & acceptance
        cases = "".join(f"<tr><td>{e(c.id)}</td><td>{e(c.description)}</td><td>{e(c.evidence_class.value)}</td></tr>"
                        for c in r.acceptance_cases) or "<tr><td colspan=3 class=mute>No acceptance cases.</td></tr>"
        deps = ", ".join(f"<a href='/task/{e(d.root_id)}'>{e(d.root_id)}</a>"
                         + (f" <span class=mute>@{e(d.artifact_hash[:10])}</span>" if d.artifact_hash else "")
                         for d in r.dependencies) or "none"
        stale = self.store.stale_marks(r.id)
        p1 = (f"<div class=card><h2>1 · Purpose and acceptance cases</h2><p><b>{e(r.deliverable or r.title)}</b></p>"
              f"<p class=mute>{e(r.description)}</p><table><tr><th>ID</th><th>Acceptance case</th><th>Evidence</th></tr>{cases}</table>"
              f"<p>Dependencies: {deps}</p>"
              + (f"<p><span class='pill FAIL'>STALE</span> a dependency was replaced; acknowledge to re-pin.</p>" if stale else "")
              + f"<p class=mute>Type {e(r.task_type.value)} · spec v{e(r.spec_version)} · visual review "
              f"{'required' if r.visual_review_required else 'not required'} · permitted paths: "
              f"{e(', '.join(r.permitted_paths) or '-')}</p></div>")
        # Panel 2: proposed changes
        if latest and latest.candidate_hash:
            guard = next((x for x in reversed(cur_evs) if x.name == "diff-guard"), None)
            diff_html = ""
            if guard and guard.details.get("diff") and orch:
                try:
                    diff = orch.artifacts.get_text(guard.details["diff"])
                    lines = []
                    for line in diff.splitlines()[:600]:
                        cls = "diff-add" if line.startswith("+") else "diff-del" if line.startswith("-") else ""
                        lines.append(f"<span class='{cls}'>{e(line)}</span>")
                    diff_html = "<pre>" + "\n".join(lines) + "</pre>"
                except Exception:
                    diff_html = "<p class=mute>diff unavailable</p>"
            changed = "".join(f"<li>{e(c['status'])} {e(c['path'])}</li>" for c in (guard.details.get("changed_files", [])
                                                                                   if guard else []))
            flags = ""
            if latest.gate_change_flags:
                flags = ("<p><span class='pill FAIL'>SEPARATE REVIEW</span> this candidate touches tests, gates or "
                         f"thresholds: {e(', '.join(latest.gate_change_flags))}. It cannot pass automatically.</p>")
            p2 = (f"<div class=card><h2>2 · Proposed changes</h2><p>Attempt {latest.number} of {r.max_attempts} · "
                  f"candidate <code>{e(latest.candidate_hash)}</code>"
                  + (f" · integrated <code>{e(latest.integrated_hash)}</code>" if latest.integrated_hash else "")
                  + f" · base <code>{e((latest.base_commit or '')[:12])}</code></p>{flags}<ul>{changed}</ul>"
                  f"<details><summary>Full diff</summary>{diff_html}</details></div>")
        else:
            p2 = "<div class=card><h2>2 · Proposed changes</h2><p class=mute>No candidate yet.</p></div>"
        # Panel 3 technical
        approvals = self.store.list_approvals(r.id)
        apr_rows = "".join(
            f"<tr><td><code>{e(a.candidate_hash[:12])}</code></td><td>{_pill(a.technical_pass.decision.value)}</td>"
            f"<td>{_pill(a.visual_approval.decision.value)}</td><td>{_pill(a.integrated_acceptance.decision.value)}</td>"
            f"<td>{_pill(a.release_approval.decision.value)}</td><td>{_pill(a.gate_change_review.decision.value)}</td></tr>"
            for a in approvals)
        apr_table = ("<table><tr><th>Hash</th><th>Technical pass</th><th>Visual approval</th><th>Integrated acceptance</th>"
                     f"<th>Release approval</th><th>Gate-change review</th></tr>{apr_rows}</table>") if approvals else ""
        p3 = f"<div class=card><h2>3 · Technical evidence</h2>{self._evidence_rows(technical)}<h3>Approvals by exact hash</h3>{apr_table or '<p class=mute>none</p>'}</div>"
        p4 = (f"<div class=card><h2>4 · Visual / play evidence</h2>{self._evidence_rows(visual)}"
              "<p class=mute>Device runs report INCOMPLETE (never pass) when the phone is disconnected, unauthorised, "
              "or the scenario does not report.</p></div>")
        # Panel 5 cost
        res = self.budget.reservations(root_id=r.id)
        rs = self.budget.summary(root_scope(r.id))
        ms = self.budget.summary(milestone_scope(r.milestone_id))
        ps = self.budget.summary(project_scope(r.project_id))
        res_rows = "".join(f"<tr><td>{e(x['purpose'] or '')}</td><td>{_pill(x['status'])}</td><td>{micros_to_usd(x['amount_micros'])}</td>"
                           f"<td>{micros_to_usd(x['settled_micros'])}</td></tr>" for x in res)
        pending = any(x["status"] == "CHARGE_PENDING" for x in res)
        prev = (f"<p><b>Specification revision.</b> Previous root <a href='/task/{e(r.previous_root_id)}'>{e(r.previous_root_id)}</a> "
                f"already cost {micros_to_usd(r.previous_cost_micros)} — this is not a free retry.</p>") if r.previous_root_id else ""
        p5 = (f"<div class=card><h2>5 · Cost and remaining limits</h2>{prev}"
              + ("<p><span class='pill PENDING'>provider completion/charge pending</span></p>" if pending else "")
              + f"<table><tr><th>Scope</th><th>Settled</th><th>Reserved</th><th>Cap</th><th>Available</th></tr>"
              + "".join(f"<tr><td>{e(s.scope)}</td><td>{micros_to_usd(s.settled)}</td><td>{micros_to_usd(s.reserved)}</td>"
                        f"<td>{micros_to_usd(s.cap)}</td><td>{micros_to_usd(s.available)}</td></tr>" for s in (rs, ms, ps))
              + f"</table><h3>Reservations</h3><table><tr><th>Purpose</th><th>Status</th><th>Reserved</th><th>Settled</th></tr>"
              f"{res_rows or '<tr><td colspan=4 class=mute>none</td></tr>'}</table>"
              f"<p class=mute>Attempts used: {len([a for a in attempts if a.status.value != 'ABANDONED'])} of {r.max_attempts}"
              f" (visual rejections count).</p></div>")
        actions = self._actions_html(r, latest)
        hist = "".join(f"<tr><td class=mute>{e(iso(ev['ts']))}</td><td>{e(ev['type'])}</td><td><code>{e(json.dumps(ev['payload'])[:220])}</code></td></tr>"
                       for ev in self.store.events(root_id=r.id)[-40:])
        body = (f"<h1>{e(r.ticket or '')} · {e(r.title)} {_pill(r.state.value)}</h1>"
                f"<p class=mute>{e(r.state_reason)}{' · ON HOLD' if r.held else ''} · root <code>{e(r.id)}</code></p>"
                f"{actions}{p1}{p2}{p3}{p4}{p5}<div class=card><details><summary>Event history</summary><table>{hist}</table>"
                f"</details></div>")
        return self._page(f"{r.ticket or r.id} review", body)

    def _actions_html(self, r, latest) -> str:
        if r.state in (TaskState.ACCEPTED, TaskState.FAILED, TaskState.CANCELLED):
            return "<div class=card><p class=mute>Terminal state; a specification change creates a new linked root.</p></div>"
        cand = (latest.integrated_hash if (latest and latest.integrated_hash and r.state == TaskState.AWAITING_APPROVAL
                and self.store.find_approval(r.id, latest.integrated_hash)) else latest.candidate_hash if latest else "") or ""
        hidden = f"<input type=hidden name=csrf value='{self.csrf}'><input type=hidden name=candidate value='{e(cand)}'>"
        cats = "".join(f"<option value='{c}'>{c.replace('_', ' ')}</option>" for c in
                       ["", "rule_incorrect", "test_failure", "compile_error", "visual_mismatch", "interaction_problem",
                        "performance", "scope_violation", "gate_tampering", "other"])
        blocks = []
        if r.state == TaskState.DRAFT:
            blocks.append(f"<form method=post action='/task/{e(r.id)}/action'>{hidden}<input type=hidden name=action value=approve_task>"
                          "<h3>Approve scope and limits</h3><button class=primary>Approve task</button></form>")
        if r.state == TaskState.AWAITING_APPROVAL:
            blocks.append(
                f"<form method=post action='/task/{e(r.id)}/action'>{hidden}<input type=hidden name=action value=approve>"
                f"<h3>Approve candidate <code>{e(cand[:12])}</code></h3>"
                "<label><input type=checkbox name=ack_gate value=1> I separately reviewed test/gate/threshold changes</label><br>"
                "<label><input type=checkbox name=accept_incomplete value=1> Accept incomplete technical evidence (override)</label><br>"
                "<button class=primary>Approve this exact hash</button></form>")
            blocks.append(
                f"<form method=post action='/task/{e(r.id)}/action'>{hidden}<input type=hidden name=action value=repair>"
                "<h3>Request bounded repair</h3><select name=category>" + cats + "</select>"
                "<textarea name=correction rows=3 placeholder='Specific correction, e.g. “hand does not grip bow”'></textarea>"
                "<button>Request repair</button></form>")
        if r.state not in (TaskState.DRAFT,):
            if r.state == TaskState.PAUSED or r.held:
                blocks.append(f"<form method=post action='/task/{e(r.id)}/action'>{hidden}<input type=hidden name=action value=resume>"
                              "<h3>Resume</h3><button>Resume</button></form>")
            else:
                blocks.append(f"<form method=post action='/task/{e(r.id)}/action'>{hidden}<input type=hidden name=action value=hold>"
                              "<h3>Hold</h3><button>Hold</button></form>")
        blocks.append(f"<form method=post action='/task/{e(r.id)}/action'>{hidden}<input type=hidden name=action value=cancel>"
                      "<h3>Cancel</h3><button class=danger>Cancel task</button></form>")
        return f"<div class=card><h2>Decision</h2><div class=actions>{''.join(blocks)}</div></div>"

    def task_action(self, root_id: str, form: dict):
        r = self.store.get_root(root_id)
        orch = self.orch[r.project_id]
        act = form.get("action")
        if act == "approve_task":
            orch.approve_task(root_id, by=self.reviewer)
        elif act in ("approve", "repair", "hold", "cancel", "resume"):
            orch.review(root_id, ReviewAction(act), reviewer=self.reviewer, candidate_hash=form.get("candidate") or None,
                        correction=form.get("correction", ""), failure_category=form.get("category") or None,
                        acknowledge_gate_change=form.get("ack_gate") == "1",
                        accept_incomplete=form.get("accept_incomplete") == "1")
        else:
            raise ReviewError(f"unknown action {act}")
        return "303 See Other", "", [("Location", f"/task/{root_id}")]

    def budget_page(self) -> str:
        cards = [f"<div class=card><h2>Owner kill switch</h2><p>{self._kill_switch_form()}</p>"
                 "<p class=mute>Stops new dispatch immediately. Limits are never raised automatically; change caps "
                 "with <code>forge budget set</code>.</p></div>"]
        for p in self.store.list_projects():
            scopes = [project_scope(p.id)] + [milestone_scope(m.id) for m in self.store.list_milestones(p.id)]
            rows = "".join(
                f"<tr><td>{e(s.scope)}</td><td>{micros_to_usd(s.cap)}</td><td>{micros_to_usd(s.settled)}</td>"
                f"<td>{micros_to_usd(s.reserved)}</td><td>{micros_to_usd(s.pending_charge)}</td><td>{micros_to_usd(s.available)}</td></tr>"
                for s in (self.budget.summary(x) for x in scopes))
            cards.append(f"<div class=card><h2>{e(p.name)} model/API ledger (USD)</h2><table><tr><th>Scope</th><th>Cap</th>"
                         f"<th>Settled</th><th>Reserved</th><th>Charge pending</th><th>Available</th></tr>{rows}</table></div>")
        rep = CashLedger(self.store).report("INR")
        per = "".join(f"<tr><td>{e(k)}</td><td>₹{v / 100:,.2f}</td></tr>" for k, v in rep["per_project_minor"].items())
        cards.append(f"<div class=card><h2>Cash ledger (INR)</h2><p>Combined cash ₹{rep['combined_cash_minor'] / 100:,.2f} · "
                     f"promo credit consumed ₹{rep['promo_credit_minor'] / 100:,.2f} (not cash) · normal-rate equivalent "
                     f"₹{rep['normal_rate_equivalent_minor'] / 100:,.2f}</p><table><tr><th>Allocation</th><th>Amount</th></tr>{per}</table>"
                     "<p class=mute>Shared expenses are recorded once and allocated; allocations sum to the combined total.</p></div>")
        hrs = CapacityLedger(self.store).summary()
        cards.append(f"<div class=card><h2>Founder hours (capacity, not cash)</h2><p class=big>{hrs['total']:.1f} h</p>"
                     f"<p class=mute>{e(json.dumps(hrs['by_kind']))}</p></div>")
        return self._page("Budget", "<h1>Budget</h1>" + "".join(cards))

    def artifact_page(self, digest: str) -> str:
        orch = next(iter(self.orch.values()), None)
        if orch is None or not orch.artifacts.exists(digest):
            raise KeyError(f"artifact {digest} not found")
        text = orch.artifacts.get_text(digest)
        return self._page(f"artifact {digest[:12]}", f"<h1>Artifact <code>{e(digest[:16])}</code></h1><pre>{e(text[:400_000])}</pre>")


def serve(app: ReviewApp, host: str = "127.0.0.1", port: int = 8765) -> None:  # pragma: no cover - manual
    from wsgiref.simple_server import WSGIRequestHandler, make_server

    if host not in ("127.0.0.1", "localhost", "::1"):
        raise ValueError("the review server binds to localhost only in R1")

    class QuietHandler(WSGIRequestHandler):
        def log_message(self, *a):
            pass

    with make_server(host, port, app, handler_class=QuietHandler) as httpd:
        print(f"Game Forge review UI: http://{host}:{port}/  (Ctrl+C to stop)")
        httpd.serve_forever()
