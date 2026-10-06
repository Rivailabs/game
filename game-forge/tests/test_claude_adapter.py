"""Claude API adapter with an injected fake client (no network, no SDK required)."""

import json
from pathlib import Path
from types import SimpleNamespace as NS

import pytest

from forge.credentials import CredentialBroker
from forge.models import AcceptanceCase, CandidateAttempt, ProjectPolicy, RootTask
from forge.providers.base import CodingRequest, PolicyViolation, ReviewRequest, TransportError, check_policy
from forge.providers.claude_api import ClaudeAdapterConfig, ClaudeAPIProvider, WorkspaceTools
from forge.providers.pricing import PriceTable, UnknownModelPrice
from forge.providers import Usage


def text(t):
    return NS(type="text", text=t, model_dump=lambda exclude_none=True: {"type": "text", "text": t})


def tool_use(i, name, inp):
    return NS(type="tool_use", id=i, name=name, input=inp,
              model_dump=lambda exclude_none=True: {"type": "tool_use", "id": i, "name": name, "input": inp})


def resp(content, stop, inp=100, out=50, rid="msg_1", model="claude-opus-5-5"):
    return NS(id=rid, model=model, stop_reason=stop, content=content,
              usage=NS(input_tokens=inp, output_tokens=out, cache_read_input_tokens=0, cache_creation_input_tokens=0))


class FakeMessages:
    def __init__(self, script):
        self.script = list(script)
        self.calls = []

    def create(self, **kw):
        self.calls.append(json.loads(json.dumps(kw, default=str)))
        item = self.script.pop(0)
        if isinstance(item, Exception):
            raise item
        return item


class FakeClient:
    def __init__(self, script):
        self.messages = FakeMessages(script)


def make_req(tmp_path):
    ws = tmp_path / "ws"
    (ws / "src").mkdir(parents=True, exist_ok=True)
    (ws / "src" / "calc.py").write_text("def add(a, b):\n    return a + b\n")
    (ws / "tests").mkdir(exist_ok=True)
    (ws / "tests" / "test_calc.py").write_text("assert True\n")
    root = RootTask(id="r1", project_id="p", milestone_id="m", title="implement mul", permitted_paths=["src/**"],
                    acceptance_cases=[AcceptanceCase(id="AC1", description="mul(3,4)==12")])
    att = CandidateAttempt(id="a1", root_id="r1", number=1)
    return CodingRequest(root=root, attempt=att, workspace=ws, project_workdir=".", idempotency_key="r1:a1:code",
                         instructions="Add mul.", protected_paths=["tests/**"])


def provider(script, **cfg):
    client = FakeClient(script)
    p = ClaudeAPIProvider(project_id="p", broker=CredentialBroker(), client=client,
                          config=ClaudeAdapterConfig(**cfg), sleep=lambda s: None)
    return p, client


def test_tool_loop_writes_only_permitted_paths(tmp_path):
    req = make_req(tmp_path)
    script = [
        resp([text("reading"), tool_use("t1", "read_file", {"path": "src/calc.py"}),
              tool_use("t2", "list_files", {"path": "."})], "tool_use"),
        resp([tool_use("t3", "write_file", {"path": "tests/test_calc.py", "content": "deleted"}),
              tool_use("t4", "write_file", {"path": "../escape.txt", "content": "x"}),
              tool_use("t5", "write_file", {"path": "src/calc.py", "content": "def mul(a, b):\n    return a * b\n"})],
             "tool_use"),
        resp([tool_use("t6", "finish", {"summary": "added mul"})], "tool_use", out=20),
    ]
    p, client = provider(script)
    res = p.submit(req)
    assert res.status == "completed" and res.summary == "added mul"
    assert res.files_written == ["src/calc.py"]
    assert "a * b" in (req.workspace / "src" / "calc.py").read_text()
    assert (req.workspace / "tests" / "test_calc.py").read_text() == "assert True\n"
    assert not (tmp_path / "escape.txt").exists()
    # tool errors returned to the model, flagged is_error
    second_results = client.messages.calls[2]["messages"][-1]["content"]
    errs = [r for r in second_results if r.get("is_error")]
    assert len(errs) == 2 and "permitted" in errs[0]["content"]
    # request shape: model, adaptive thinking, effort, strict tools, no forced tool_choice
    first = client.messages.calls[0]
    assert first["model"] == "claude-opus-5-5"
    assert first["thinking"] == {"type": "adaptive"}
    assert first["output_config"] == {"effort": "high"}
    assert "tool_choice" not in first
    assert all(t["strict"] is True for t in first["tools"])
    # usage -> cost via price table: 3 requests, 300 in / 120 out at $4/$20 per MTok
    assert res.usage.requests == 3 and res.usage.input_tokens == 300 and res.usage.output_tokens == 120
    assert res.cost_micros == 300 * 4 + 120 * 20
    # idempotent by key and reconcilable
    assert p.submit(req) is res
    assert p.reconcile("r1:a1:code").status == "completed"
    assert p.reconcile("other").status == "unknown"


def test_refusal_and_max_turns(tmp_path):
    p, _ = provider([resp([], "refusal")])
    assert p.submit(make_req(tmp_path)).status == "refused"
    loop = [resp([tool_use(f"t{i}", "list_files", {"path": "."})], "tool_use", rid=f"m{i}") for i in range(3)]
    p, _ = provider(loop, max_turns=3)
    res = p.submit(make_req(tmp_path / "b"))
    assert res.status == "incomplete" and "max_turns" in res.summary


def test_context_ceiling_stops_before_overspending(tmp_path):
    p, client = provider([resp([tool_use("t1", "read_file", {"path": "src/big.txt"})], "tool_use")],
                         max_context_bytes=15_000)
    req = make_req(tmp_path)
    (req.workspace / "src" / "big.txt").write_text("x" * 20_000)
    res = p.submit(req)
    assert res.status == "incomplete" and "context ceiling" in res.summary
    assert len(client.messages.calls) == 1


def test_transport_retries_then_error(tmp_path):
    p, client = provider([TimeoutError("t1"), resp([tool_use("f", "finish", {"summary": "ok"})], "tool_use")],
                         transport_retries=2)
    res = p.submit(make_req(tmp_path))
    assert res.status == "completed" and res.transport_retries == 1
    p, _ = provider([TimeoutError("a"), ConnectionError("b"), TimeoutError("c")], transport_retries=2)
    with pytest.raises(TransportError):
        p.submit(make_req(tmp_path / "x"))


def test_ceiling_is_bounded_and_uses_highest_input_rate(tmp_path):
    p, _ = provider([], max_turns=10, max_tokens=16000, max_context_bytes=200_000, transport_retries=1)
    ceiling = p.estimate_ceiling_micros(make_req(tmp_path))
    per_request = 200_000 * 5.0 + 16_000 * 20  # cache-write rate 1.25 x $4 = $5/MTok
    assert ceiling == int(per_request * 11)
    p2, _ = provider([], max_turns=10, max_tokens=16000, max_context_bytes=200_000, transport_retries=1,
                     refusal_fallback=True)
    assert p2.estimate_ceiling_micros(make_req(tmp_path)) > ceiling  # priced at the pricier fallback model


def test_review_structured_output(tmp_path):
    verdict = {"verdict": "reject", "findings": [{"acceptance_case": "AC1", "evidence": "diff line 3",
                                                   "comment": "mul returns a+b"}]}
    p, client = provider([resp([text(json.dumps(verdict))], "end_turn")])
    req = make_req(tmp_path)
    rr = p.review(ReviewRequest(req.root, "abc", "diff --git ...", req.root.acceptance_cases, "mul: FAIL", "k"))
    assert rr.verdict == "reject" and rr.findings[0]["acceptance_case"] == "AC1"
    fmt = client.messages.calls[0]["output_config"]["format"]
    assert fmt["type"] == "json_schema" and fmt["schema"]["required"] == ["verdict", "findings"]
    assert rr.cost_micros == 100 * 4 + 50 * 20


def test_descriptor_and_policy():
    p, _ = provider([])
    d = p.descriptor
    assert d.vendor == "anthropic" and "api.anthropic.com" in d.data_destinations[0]
    assert d.cancellation.startswith("uncertain")
    check_policy(d, ProjectPolicy(allowed_vendors=["anthropic"]), unattended=True)
    with pytest.raises(PolicyViolation):
        check_policy(d, ProjectPolicy(allowed_vendors=["anthropic"], allowed_data_classes=["prompt"]), unattended=True)
    with pytest.raises(PolicyViolation):
        check_policy(d, ProjectPolicy(allowed_vendors=[]), unattended=True)


def test_cli_connector_is_supervised_only(tmp_path):
    from forge.providers.base import SupervisedOnly
    from forge.providers.cli_connector import OfficialCLIConnector

    c = OfficialCLIConnector()
    assert c.descriptor.supervised_only and not c.descriptor.unattended_allowed
    assert c.estimate_ceiling_micros(make_req(tmp_path)) is None
    with pytest.raises(PolicyViolation):
        check_policy(c.descriptor, ProjectPolicy(allowed_vendors=["anthropic"]), unattended=True)
    with pytest.raises(SupervisedOnly):
        c.submit(make_req(tmp_path))


def test_workspace_tools_reject_symlink_escape(tmp_path):
    ws = tmp_path / "ws"
    (ws / "src").mkdir(parents=True)
    outside = tmp_path / "outside.txt"
    outside.write_text("secret")
    (ws / "src" / "link.txt").symlink_to(outside)
    tools = WorkspaceTools(ws, ["src/**"], 10_000)
    with pytest.raises(PermissionError):
        tools.call("read_file", {"path": "src/link.txt"})
    with pytest.raises(PermissionError):
        tools.call("read_file", {"path": ".git/config"})


def test_price_table():
    pt = PriceTable()
    assert pt.cost_micros(Usage(model="claude-opus-5-5", input_tokens=1_000_000, output_tokens=1_000_000)) == 24_000_000
    assert pt.cost_micros(Usage(model="claude-opus-5-5", cache_read_input_tokens=1_000_000)) == 200_000
    with pytest.raises(UnknownModelPrice):
        pt.cost_micros(Usage(model="mystery-model", input_tokens=1))
    pt2 = PriceTable.from_config({"claude-opus-5-5": {"input_per_mtok": 1, "output_per_mtok": 2}})
    assert pt2.cost_micros(Usage(model="claude-opus-5-5", input_tokens=10, output_tokens=10)) == 30
