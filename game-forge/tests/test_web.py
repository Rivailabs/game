"""Local review UI (WSGI, exercised in-process)."""

import io
from urllib.parse import urlencode

from forge.budget import BudgetLedger
from forge.models import TaskState as S
from forge.web import ReviewApp


def call(app, method, path, form=None):
    body = urlencode(form or {}).encode()
    environ = {"REQUEST_METHOD": method, "PATH_INFO": path.split("?")[0],
               "QUERY_STRING": path.split("?")[1] if "?" in path else "", "CONTENT_LENGTH": str(len(body)),
               "wsgi.input": io.BytesIO(body)}
    out = {}

    def start(status, headers):
        out["status"], out["headers"] = status, dict(headers)

    data = b"".join(app(environ, start)).decode()
    return out["status"], out["headers"], data


def test_dashboard_task_list_and_budget(env):
    env.task()
    app = ReviewApp(env.store, {"demo": env.orch})
    st, hdrs, html = call(app, "GET", "/")
    assert st.startswith("200") and "Demo" in html and "DRAFT" in html
    assert "default-src 'self'" in hdrs["Content-Security-Policy"]
    assert "cdn" not in html.lower()
    st, _, html = call(app, "GET", "/tasks?state=DRAFT")
    assert "Task 1" in html
    st, _, html = call(app, "GET", "/budget")
    assert st.startswith("200") and "Cash ledger" in html and "Founder hours" in html
    st, _, _ = call(app, "GET", "/task/nope")
    assert st.startswith("404")


def test_review_page_five_panels_and_actions(make_env):
    env = make_env()
    t = env.task(visual_review_required=True)
    app = ReviewApp(env.store, {"demo": env.orch})
    st, _, _ = call(app, "POST", f"/task/{t.id}/action", {"action": "approve_task"})
    assert st.startswith("403")  # CSRF required
    st, hdrs, _ = call(app, "POST", f"/task/{t.id}/action", {"action": "approve_task", "csrf": app.csrf})
    assert st.startswith("303")
    env.orch.run_until_idle()
    assert env.store.get_root(t.id).state == S.AWAITING_APPROVAL
    st, _, html = call(app, "GET", f"/task/{t.id}")
    for panel in ("1 · Purpose and acceptance cases", "2 · Proposed changes", "3 · Technical evidence",
                  "4 · Visual / play evidence", "5 · Cost and remaining limits"):
        assert panel in html, panel
    assert "Request bounded repair" in html and "Approve this exact hash" in html
    cand = env.store.latest_attempt(t.id).candidate_hash
    # repair without correction/category is refused
    st, _, html = call(app, "POST", f"/task/{t.id}/action", {"action": "repair", "csrf": app.csrf, "candidate": cand})
    assert st.startswith("409")
    st, _, _ = call(app, "POST", f"/task/{t.id}/action", {"action": "approve", "csrf": app.csrf, "candidate": cand})
    assert st.startswith("303")
    assert env.store.get_root(t.id).state == S.INTEGRATION_READY
    dec = env.store.list_review_decisions(t.id)[-1]
    assert dec.action.value == "approve" and dec.candidate_hash == cand
    # diff artifact is viewable and escaped
    guard = next(e for e in env.store.list_evidence(t.id) if e.name == "diff-guard")
    st, _, html = call(app, "GET", f"/artifact/{guard.details['diff']}")
    assert st.startswith("200") and "def mul" in html


def test_kill_switch_from_ui(env):
    app = ReviewApp(env.store, {"demo": env.orch})
    st, _, _ = call(app, "POST", "/kill-switch", {"op": "stop", "csrf": app.csrf})
    assert st.startswith("303") and BudgetLedger(env.store).dispatch_stopped()
    _, _, html = call(app, "GET", "/")
    assert "Dispatch is STOPPED" in html and "Resume dispatch" in html
    call(app, "POST", "/kill-switch", {"op": "resume", "csrf": app.csrf})
    assert not BudgetLedger(env.store).dispatch_stopped()


def test_html_is_escaped(env):
    env.task(title="<script>alert(1)</script>")
    app = ReviewApp(env.store, {"demo": env.orch})
    _, _, html = call(app, "GET", "/tasks")
    assert "<script>alert(1)</script>" not in html and "&lt;script&gt;" in html


def test_server_refuses_non_localhost_bind(env):
    import pytest

    from forge.web.app import serve

    with pytest.raises(ValueError):
        serve(ReviewApp(env.store, {"demo": env.orch}), host="0.0.0.0")
