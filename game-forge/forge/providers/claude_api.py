"""Forge-managed Claude API agent (builder / independent reviewer / visual judge).

* Uses the official ``anthropic`` Python SDK (optional dependency: ``pip install
  'game-forge[claude]'``) and the Messages API with client tools. The tool runner
  is Forge's own: the model can only list/read files inside the task worktree and
  write/delete files inside the task's *permitted paths*. There is no shell tool,
  so generated code never executes inside the agent loop and cannot read the key.
* The API key is obtained only from the ``CredentialBroker`` at call time and is
  passed to the SDK client object, never to a subprocess environment or a log.
* Each submission has a credible billable ceiling: ``max_turns`` requests, each
  bounded by ``max_tokens`` output and ``max_context_bytes`` input (bytes are an
  upper bound on tokens), priced at the highest input rate (cache write). The
  loop stops before a request could exceed the context ceiling.
* Refusal fallbacks (server-side ``fallbacks``) are OFF by default because they can
  route a job to a different, more expensive model; the plan forbids adapters from
  silently selecting a more expensive route. Enable ``refusal_fallback`` only with
  an owner decision (the ceiling then uses the fallback model's price).
"""

from __future__ import annotations

import json
import os
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Optional

from ..credentials import CredentialBroker, CredentialError, redact
from ..pathglob import is_safe_relative, match_path
from .base import (
    CodingRequest,
    ProviderAdapter,
    ProviderDescriptor,
    ProviderRejected,
    ProviderResult,
    ProviderUnavailable,
    ReconcileResult,
    ReviewRequest,
    ReviewResult,
    TransportError,
    Usage,
)
from .pricing import PriceTable

DEFAULT_MODEL = "claude-opus-5-5"
FALLBACK_PRICE_MODEL = "claude-opus-4-8"  # most expensive model "default" fallbacks may route to


@dataclass
class ClaudeRoleConfig:
    builder: str = DEFAULT_MODEL
    reviewer: str = DEFAULT_MODEL
    visual_judge: str = DEFAULT_MODEL
    effort_builder: str = "high"
    effort_reviewer: str = "high"


@dataclass
class ClaudeAdapterConfig:
    roles: ClaudeRoleConfig = field(default_factory=ClaudeRoleConfig)
    max_turns: int = 16
    max_tokens: int = 16000
    max_context_bytes: int = 250_000
    max_file_bytes: int = 120_000
    transport_retries: int = 2
    backoff_s: float = 2.0
    refusal_fallback: bool = False
    inference_geo: Optional[str] = None
    timeout_s: float = 600.0


SYSTEM_PROMPT = """You are the builder agent inside Game Forge, working on one bounded, owner-approved task.
Work only through the provided tools. You may read any project file in the workspace, but you can only
write or delete files under the task's permitted paths; other writes are rejected. You cannot run code:
Forge runs protected checks (build, tests, device) after you finish and reports failures back as a repair.
Protected acceptance tests are maintained by the owner: never delete, skip or weaken tests, gates or
thresholds - such changes are flagged for separate review and cannot pass automatically.
If a rule needed for the task is not specified, do not invent it: call `finish` and explain what input is
missing. When the change is complete, call `finish` with a short summary of what you changed and why."""

REVIEW_PROMPT = """You are the independent reviewer in Game Forge. Review the candidate diff against the
task's acceptance cases. Deterministic tests are the authority for rule correctness; your role is to spot
scope creep, missing cases, weakened tests or gates, and unclear code. A rejection must cite the specific
acceptance case and the diff/log evidence behind it. Say "uncertain" when evidence is insufficient."""

REVIEW_SCHEMA = {
    "type": "object",
    "properties": {
        "verdict": {"type": "string", "enum": ["approve", "reject", "uncertain"]},
        "findings": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "acceptance_case": {"type": "string"},
                    "evidence": {"type": "string"},
                    "comment": {"type": "string"},
                },
                "required": ["acceptance_case", "evidence", "comment"],
                "additionalProperties": False,
            },
        },
    },
    "required": ["verdict", "findings"],
    "additionalProperties": False,
}


def _tool(name: str, description: str, props: dict, required: list[str]) -> dict:
    return {
        "name": name,
        "description": description,
        "strict": True,
        "input_schema": {"type": "object", "properties": props, "required": required, "additionalProperties": False},
    }


TOOLS = [
    _tool("list_files", "List files under a workspace-relative directory ('.' for the root).",
          {"path": {"type": "string"}}, ["path"]),
    _tool("read_file", "Read a UTF-8 text file (workspace-relative path).", {"path": {"type": "string"}}, ["path"]),
    _tool("write_file", "Create or overwrite a text file. Only allowed under the task's permitted paths.",
          {"path": {"type": "string"}, "content": {"type": "string"}}, ["path", "content"]),
    _tool("delete_file", "Delete a file under the task's permitted paths.", {"path": {"type": "string"}}, ["path"]),
    _tool("finish", "Finish the task with a summary (or the missing input that blocks it).",
          {"summary": {"type": "string"}}, ["summary"]),
]


class WorkspaceTools:
    """Path-confined file tools. All paths are relative to the task worktree."""

    def __init__(self, workspace: Path, permitted: list[str], max_file_bytes: int):
        self.ws = workspace.resolve()
        self.permitted = permitted
        self.max_file_bytes = max_file_bytes
        self.written: list[str] = []

    def _resolve(self, rel: str) -> Path:
        rel = (rel or ".").strip()
        if rel in (".", "./", ""):
            return self.ws
        if not is_safe_relative(rel):
            raise PermissionError(f"path {rel!r} is outside the workspace")
        p = (self.ws / rel).resolve()
        if p != self.ws and self.ws not in p.parents:
            raise PermissionError(f"path {rel!r} escapes the workspace")
        return p

    def _check_write(self, rel: str) -> Path:
        p = self._resolve(rel)
        if not match_path(rel, self.permitted):
            raise PermissionError(f"write to {rel!r} denied: not in permitted paths {self.permitted}")
        return p

    def call(self, name: str, args: dict) -> str:
        if name == "list_files":
            base = self._resolve(args.get("path", "."))
            if not base.is_dir():
                raise FileNotFoundError(f"{args.get('path')} is not a directory")
            items = []
            for p in sorted(base.rglob("*")):
                rel = p.relative_to(self.ws).as_posix()
                if p.is_file() and ".git" not in p.relative_to(self.ws).parts and "/bin/" not in f"/{rel}" \
                        and "/obj/" not in f"/{rel}":
                    items.append(rel)
                if len(items) >= 500:
                    items.append("... (truncated)")
                    break
            return "\n".join(items) or "(empty)"
        if name == "read_file":
            p = self._resolve(args["path"])
            data = p.read_bytes()
            if len(data) > self.max_file_bytes:
                return data[: self.max_file_bytes].decode("utf-8", errors="replace") + "\n... (truncated)"
            return redact(data.decode("utf-8", errors="replace"))
        if name == "write_file":
            p = self._check_write(args["path"])
            content = args["content"]
            if len(content.encode()) > self.max_file_bytes:
                raise ValueError("file too large for one write")
            p.parent.mkdir(parents=True, exist_ok=True)
            p.write_text(content)
            self.written.append(args["path"])
            return f"wrote {args['path']} ({len(content)} chars)"
        if name == "delete_file":
            p = self._check_write(args["path"])
            if p.exists():
                p.unlink()
                self.written.append(args["path"])
            return f"deleted {args['path']}"
        raise ValueError(f"unknown tool {name}")


def _usage_from(resp: Any, model: str) -> Usage:
    u = getattr(resp, "usage", None)
    g = (lambda k: int(getattr(u, k, 0) or 0)) if u is not None else (lambda k: 0)
    return Usage(model=getattr(resp, "model", None) or model, input_tokens=g("input_tokens"),
                 output_tokens=g("output_tokens"), cache_read_input_tokens=g("cache_read_input_tokens"),
                 cache_creation_input_tokens=g("cache_creation_input_tokens"), requests=1)


def _block_to_param(b: Any) -> dict:
    if hasattr(b, "model_dump"):
        return b.model_dump(exclude_none=True)
    return dict(b)


class ClaudeAPIProvider(ProviderAdapter):
    def __init__(self, *, project_id: str, broker: CredentialBroker, prices: PriceTable | None = None,
                 config: ClaudeAdapterConfig | None = None, client: Any = None,
                 sleep: Callable[[float], None] | None = None, role: str = "builder"):
        self.project_id = project_id
        self.broker = broker
        self.prices = prices or PriceTable()
        self.cfg = config or ClaudeAdapterConfig()
        self._client = client
        self._sleep = sleep or __import__("time").sleep
        self.role = role
        self._completed: dict[str, ProviderResult] = {}
        self.descriptor = ProviderDescriptor(
            name="claude-api", vendor="anthropic",
            operations=["code", "review", "visual_judge"],
            auth_method="API key via Forge credential broker (env var or key file outside the repo)",
            billing_party="the API account owner whose key is configured (BYOK)",
            data_destinations=["api.anthropic.com (Claude Messages API)"],
            data_classes_sent=["prompt", "code", "test_excerpt"],
            region=self.cfg.inference_geo or "global",
            quotas="Anthropic rate limits per API key; Forge root/milestone/project caps",
            timeout_s=self.cfg.timeout_s,
            cancellation="uncertain (a sent synchronous request cannot be confirmed cancelled)",
            usage_reporting="exact per-request token usage from the response",
            structured_output=True,
            tested_versions=["anthropic-python>=1.0", "Messages API 2023-06-01"],
            notes="Refusal fallbacks disabled unless the owner enables them.",
        )

    # ------------------------------------------------------------------ client
    def client(self) -> Any:
        if self._client is None:
            try:
                import anthropic  # type: ignore
            except ImportError as e:  # pragma: no cover - depends on environment
                raise ProviderUnavailable(
                    "the 'anthropic' package is not installed: pip install 'game-forge[claude]'") from e
            try:
                key = self.broker.get(self.project_id, "claude-api")
            except CredentialError as e:
                raise ProviderUnavailable(f"credentials: {e}") from e
            self._client = anthropic.Anthropic(api_key=key, max_retries=0, timeout=self.cfg.timeout_s)
        return self._client

    def _transport_errors(self) -> tuple[type, ...]:
        errs: list[type] = [TransportError, TimeoutError, ConnectionError]
        try:
            import anthropic  # type: ignore

            errs += [anthropic.APIConnectionError, anthropic.APITimeoutError, anthropic.RateLimitError,
                     anthropic.InternalServerError]
        except Exception:
            pass
        return tuple(errs)

    def _rejection_errors(self) -> tuple[type, ...]:
        try:
            import anthropic  # type: ignore

            return (anthropic.AuthenticationError, anthropic.PermissionDeniedError, anthropic.BadRequestError,
                    anthropic.NotFoundError)
        except Exception:
            return (ProviderRejected,)

    # ------------------------------------------------------------------ cost
    def _price_model(self, model: str) -> str:
        if not self.cfg.refusal_fallback:
            return model
        a, b = self.prices.get(model), self.prices.get(FALLBACK_PRICE_MODEL)
        return model if a.output_per_mtok >= b.output_per_mtok else FALLBACK_PRICE_MODEL

    def estimate_ceiling_micros(self, req: CodingRequest) -> Optional[int]:
        model = self._price_model(self.cfg.roles.builder)
        per_request = self.prices.ceiling_micros(model, input_tokens=self.cfg.max_context_bytes,
                                                 output_tokens=self.cfg.max_tokens)
        # each transport retry repeats at most one request
        return per_request * (self.cfg.max_turns + self.cfg.transport_retries)

    def review_ceiling_micros(self, req: ReviewRequest) -> Optional[int]:
        model = self._price_model(self.cfg.roles.reviewer)
        return self.prices.ceiling_micros(model, input_tokens=self.cfg.max_context_bytes,
                                          output_tokens=self.cfg.max_tokens) * (1 + self.cfg.transport_retries)

    # ------------------------------------------------------------------ calls
    def _create(self, **kw: Any) -> Any:
        c = self.client()
        if self.cfg.inference_geo:
            kw["inference_geo"] = self.cfg.inference_geo
        if self.cfg.refusal_fallback:
            return c.beta.messages.create(betas=["server-side-fallback-2026-07-01"], fallbacks="default", **kw)
        return c.messages.create(**kw)

    def _create_with_retries(self, usage: Usage, counters: dict, **kw: Any) -> Any:
        errs = self._transport_errors()
        rejected = self._rejection_errors()
        attempt = 0
        while True:
            try:
                return self._create(**kw)
            except rejected as e:  # type: ignore[misc]
                raise ProviderRejected(f"{type(e).__name__}: {redact(str(e))}", usage=usage,
                                       cost_micros=self.prices.cost_micros(usage) if usage.requests else 0) from e
            except errs as e:  # type: ignore[misc]
                attempt += 1
                counters["transport_retries"] = counters.get("transport_retries", 0) + 1
                if attempt > self.cfg.transport_retries:
                    raise TransportError(f"transport failure after {attempt} attempts: {redact(str(e))}",
                                         usage=usage, cost_micros=self.prices.cost_micros(usage) if usage.model else 0)
                self._sleep(self.cfg.backoff_s * (2 ** (attempt - 1)))

    def _prompt(self, req: CodingRequest) -> str:
        r = req.root
        cases = "\n".join(f"- [{c.id}] {c.description}" for c in r.acceptance_cases) or "- (none listed)"
        parts = [
            f"Task {r.ticket or r.id}: {r.title}",
            f"Deliverable: {r.deliverable or r.description}",
            f"Project directory inside the workspace: {req.project_workdir}",
            f"Permitted write paths: {', '.join(r.permitted_paths) or '(none)'}",
            f"Protected (read-only) paths: {', '.join(req.protected_paths) or '(none)'}",
            f"Input contract: {r.input_contract or '-'}",
            f"Output contract: {r.output_contract or '-'}",
            f"Acceptance cases:\n{cases}",
            req.instructions,
        ]
        if req.repair_notes:
            parts.append(f"This is repair attempt {req.attempt.number}. Previous attempt findings:\n{req.repair_notes}")
        return "\n\n".join(p for p in parts if p)

    def submit(self, req: CodingRequest) -> ProviderResult:
        if req.idempotency_key in self._completed:
            return self._completed[req.idempotency_key]
        model = self.cfg.roles.builder
        tools = WorkspaceTools(req.workspace, req.root.permitted_paths, self.cfg.max_file_bytes)
        messages: list[dict] = [{"role": "user", "content": self._prompt(req)}]
        usage = Usage(model=model)
        counters: dict = {}
        transcript: list[str] = []
        summary, status, job_ids = "", "incomplete", []
        for turn in range(self.cfg.max_turns):
            size = len(SYSTEM_PROMPT) + len(json.dumps(TOOLS)) + len(json.dumps(messages, default=str))
            if size > self.cfg.max_context_bytes:
                status, summary = "incomplete", "context ceiling reached before the task finished"
                break
            resp = self._create_with_retries(
                usage, counters, model=model, max_tokens=self.cfg.max_tokens, system=SYSTEM_PROMPT,
                tools=TOOLS, messages=messages, thinking={"type": "adaptive"},
                output_config={"effort": self.cfg.roles.effort_builder},
            )
            usage.add(_usage_from(resp, model))
            job_ids.append(getattr(resp, "id", ""))
            stop = getattr(resp, "stop_reason", None)
            content = list(getattr(resp, "content", []) or [])
            for b in content:
                if getattr(b, "type", None) == "text":
                    transcript.append(f"assistant: {b.text}")
            if stop == "refusal":
                status, summary = "refused", "the model declined the request (stop_reason=refusal)"
                break
            messages.append({"role": "assistant", "content": [_block_to_param(b) for b in content]})
            if stop == "pause_turn":
                continue
            tool_uses = [b for b in content if getattr(b, "type", None) == "tool_use"]
            if not tool_uses:
                status = "completed" if stop == "end_turn" else "incomplete"
                summary = summary or ("model ended without calling finish" if stop == "end_turn" else f"stopped: {stop}")
                break
            results, finished = [], False
            for tu in tool_uses:
                args = tu.input if isinstance(tu.input, dict) else json.loads(tu.input or "{}")
                if tu.name == "finish":
                    finished, summary = True, str(args.get("summary", ""))
                    results.append({"type": "tool_result", "tool_use_id": tu.id, "content": "ok"})
                    continue
                try:
                    out = tools.call(tu.name, args)
                    results.append({"type": "tool_result", "tool_use_id": tu.id, "content": out})
                    transcript.append(f"tool {tu.name}({args.get('path', '')}) ok")
                except Exception as e:  # tool errors go back to the model, never crash Forge
                    results.append({"type": "tool_result", "tool_use_id": tu.id, "content": str(e), "is_error": True})
                    transcript.append(f"tool {tu.name}({args.get('path', '')}) error: {e}")
            messages.append({"role": "user", "content": results})
            if finished:
                status = "completed"
                break
        else:
            status, summary = "incomplete", f"max_turns ({self.cfg.max_turns}) reached"
        result = ProviderResult(
            status=status, provider_job_id=",".join(j for j in job_ids if j)[:500], summary=redact(summary),
            usage=usage, cost_micros=self.prices.cost_micros(usage) if usage.requests else 0,
            files_written=sorted(set(tools.written)), transcript=redact("\n".join(transcript))[-20000:],
            transport_retries=counters.get("transport_retries", 0),
        )
        self._completed[req.idempotency_key] = result
        return result

    def reconcile(self, idempotency_key: str) -> ReconcileResult:
        if idempotency_key in self._completed:
            return ReconcileResult("completed", self._completed[idempotency_key])
        # The Messages API has no job lookup by client key: an interrupted call is unknowable.
        return ReconcileResult("unknown", detail="synchronous Messages API call; outcome and charge cannot be queried")

    def cancel(self, provider_job_id: str) -> str:
        return "uncertain"

    def review(self, req: ReviewRequest) -> ReviewResult:
        model = self.cfg.roles.reviewer
        cases = "\n".join(f"- [{c.id}] {c.description}" for c in req.acceptance_cases) or "- (none)"
        diff = req.diff
        budget = self.cfg.max_context_bytes - 20_000
        if len(diff) > budget:
            diff = diff[:budget] + "\n... (diff truncated; say 'uncertain' if this matters)"
        content = (f"Task: {req.root.title}\nCandidate: {req.candidate_hash}\nAcceptance cases:\n{cases}\n\n"
                   f"Technical evidence:\n{req.evidence_summary}\n\nDiff:\n{diff}")
        usage = Usage(model=model)
        resp = self._create_with_retries(
            usage, {}, model=model, max_tokens=self.cfg.max_tokens, system=REVIEW_PROMPT,
            messages=[{"role": "user", "content": content}], thinking={"type": "adaptive"},
            output_config={"effort": self.cfg.roles.effort_reviewer,
                           "format": {"type": "json_schema", "schema": REVIEW_SCHEMA}},
        )
        usage.add(_usage_from(resp, model))
        cost = self.prices.cost_micros(usage)
        if getattr(resp, "stop_reason", None) == "refusal":
            return ReviewResult("uncertain", [{"acceptance_case": "-", "evidence": "-",
                                               "comment": "reviewer declined (refusal)"}], usage, cost)
        text = next((b.text for b in resp.content if getattr(b, "type", None) == "text"), "{}")
        try:
            data = json.loads(text)
            return ReviewResult(data.get("verdict", "uncertain"), list(data.get("findings", [])), usage, cost, text)
        except json.JSONDecodeError:
            return ReviewResult("uncertain", [], usage, cost, text)


def broker_from_env(project_id: str, repo_roots: list[str], key_file: str | None = None) -> CredentialBroker:
    from ..credentials import ProviderCredentialSpec

    b = CredentialBroker(repo_roots=repo_roots)
    b.register(ProviderCredentialSpec("claude-api", "ANTHROPIC_API_KEY",
                                      key_file or os.path.expanduser("~/.config/game-forge/anthropic.key")))
    b.allow(project_id, "claude-api")
    return b
