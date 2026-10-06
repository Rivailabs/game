"""Worker containment: policy, backend detection/argv, runner modes, sandboxed checks, orchestrator."""

from __future__ import annotations

import json
import os
import sys
import threading
import time
from pathlib import Path

import pytest

from forge.checks import CommandCheck, build_check
from forge.checks.base import CheckContext, ProcResult
from forge.models import EvidenceStatus, TaskState as S
from forge.sandbox import (
    ENFORCED,
    NOT_RUN,
    SUPERVISED,
    BackendInfo,
    Mount,
    NetworkMode,
    SandboxConfig,
    SandboxConfigError,
    SandboxedCheck,
    SandboxRefused,
    SandboxRunner,
    WorkerRole,
    detect_backends,
    load_sandbox_config,
)
from forge.sandbox.backends import BwrapBackend, DockerBackend, PodmanBackend, SandboxRequest
from forge.sandbox.policy import NetworkPolicy, builtin_forbidden_paths, validate_mounts
from forge.sandbox.runner import execute

from .conftest import unittest_check


class RecordingExecutor:
    def __init__(self, rc: int = 0, timed_out: bool = False):
        self.calls: list[tuple[list[str], Path, dict]] = []
        self.rc, self.timed_out = rc, timed_out

    def __call__(self, argv, cwd, timeout_s, env, cancel=None):
        self.calls.append((list(argv), Path(cwd), dict(env)))
        if argv[1:3] == ["rm", "--force"]:
            return ProcResult(0, "", "", 0.0)
        return ProcResult(None if self.timed_out else self.rc, "out", "", 0.01, timed_out=self.timed_out)


PODMAN = BackendInfo("podman", "/usr/bin/podman", True, rootless=True, version="5.0", allowlist_capable=True)
BWRAP = BackendInfo("bwrap", "/usr/bin/bwrap", True, rootless=True, version="0.9")


FAKE_HOME = Path("/nonexistent-forge-home")


def no_home_forbidden(tmp_path):
    """Forbidden list for tests: the real built-ins, but with a fake home so tmp paths are not under it."""
    return builtin_forbidden_paths(home=FAKE_HOME)


# ------------------------------------------------------------------ policy / config


def test_config_defaults_deny_network_except_restore_allowlist():
    cfg = SandboxConfig()
    assert cfg.network_for(WorkerRole.GENERATED_CODE_CHECK).mode == NetworkMode.DENY
    assert cfg.network_for(WorkerRole.ASSET_GENERATION).mode == NetworkMode.DENY
    restore = cfg.network_for(WorkerRole.DEPENDENCY_RESTORE)
    assert restore.mode == NetworkMode.ALLOWLIST and "api.nuget.org" in restore.hosts


def test_config_must_live_outside_worker_writable_scope(tmp_path):
    repo = tmp_path / "repo"
    repo.mkdir()
    inside = repo / "sandbox.toml"
    inside.write_text('[sandbox]\nbackend = "none"\n')
    os.chmod(inside, 0o600)
    with pytest.raises(SandboxConfigError, match="worker-writable"):
        load_sandbox_config(inside, writable_roots=[repo])
    outside = tmp_path / "owner" / "sandbox.toml"
    outside.parent.mkdir()
    outside.write_text('[sandbox]\nbackend = "podman"\nimage = "forge-worker@sha256:abc"\n'
                       '[sandbox.roles.dependency_restore]\nnetwork = "allowlist"\nhosts = ["api.nuget.org"]\n')
    os.chmod(outside, 0o644)
    cfg = load_sandbox_config(outside, writable_roots=[repo])
    assert cfg.backend == "podman" and cfg.source == str(outside.resolve())
    assert cfg.network_for(WorkerRole.DEPENDENCY_RESTORE).hosts == ("api.nuget.org",)
    os.chmod(outside, 0o666)
    with pytest.raises(SandboxConfigError, match="group/world writable"):
        load_sandbox_config(outside, writable_roots=[repo])


def test_config_rejects_unknown_keys_and_missing_file_gives_defaults(tmp_path):
    p = tmp_path / "s.toml"
    p.write_text('[sandbox]\nallow_everything = true\n')
    os.chmod(p, 0o600)
    with pytest.raises(SandboxConfigError, match="unknown"):
        load_sandbox_config(p)
    assert load_sandbox_config(tmp_path / "missing.toml").source == "defaults"


def test_mounts_refuse_home_sockets_keys_and_allow_project_dirs(tmp_path):
    home = tmp_path / "home"
    (home / ".ssh").mkdir(parents=True)
    project = home / "game"
    project.mkdir()
    forbidden = builtin_forbidden_paths(home=home)
    validate_mounts([Mount(project, str(project), False)], forbidden=forbidden, home=home)  # project under home: fine
    with pytest.raises(SandboxRefused, match="forbidden path"):
        validate_mounts([Mount(home, str(home))], forbidden=forbidden, home=home)
    with pytest.raises(SandboxRefused):
        validate_mounts([Mount(home / ".ssh", "/x")], forbidden=forbidden, home=home)
    with pytest.raises(SandboxRefused):
        validate_mounts([Mount(Path("/var/run/docker.sock"), "/var/run/docker.sock")], forbidden=forbidden, home=home)
    with pytest.raises(SandboxRefused):
        validate_mounts([Mount(Path("/"), "/host")], forbidden=forbidden, home=home)
    (project / "android").mkdir()
    (project / "android" / "upload.jks").write_bytes(b"key")
    with pytest.raises(SandboxRefused, match="signing material"):
        validate_mounts([Mount(project, str(project), False)], forbidden=forbidden, home=home)


# ------------------------------------------------------------------ detection


def fake_run(responses):
    def run(argv, cwd, timeout, env=None):
        for prefix, res in responses.items():
            if " ".join(argv).startswith(prefix):
                return res
        return ProcResult(1, "", "unexpected", 0.0)
    return run


def test_detect_backends_requires_rootless():
    which = {"podman": "/usr/bin/podman", "docker": "/usr/bin/docker", "bwrap": "/usr/bin/bwrap"}.get
    run = fake_run({
        "/usr/bin/podman info": ProcResult(0, json.dumps({"host": {"security": {"rootless": True}},
                                                          "version": {"Version": "5.2"}}), "", 0),
        "/usr/bin/docker info": ProcResult(0, '["name=seccomp,profile=builtin"]|27.0', "", 0),
        "/usr/bin/bwrap --version": ProcResult(0, "bubblewrap 0.9.0", "", 0),
        "/usr/bin/bwrap --unshare-all": ProcResult(1, "", "No permissions to create new namespace", 0),
    })
    infos = {b.name: b for b in detect_backends(which=which, run=run)}
    assert infos["podman"].available and infos["podman"].rootless and infos["podman"].version == "5.2"
    assert not infos["docker"].available and "rootful" in infos["docker"].reason
    assert not infos["bwrap"].available and "namespaces" in infos["bwrap"].reason


def test_detect_backends_docker_rootless_and_nothing_installed():
    run = fake_run({"/d info": ProcResult(0, '["name=seccomp","name=rootless"]|27.0', "", 0)})
    infos = {b.name: b for b in detect_backends(which={"docker": "/d"}.get, run=run)}
    assert infos["docker"].available and infos["docker"].allowlist_capable
    assert not infos["podman"].available and "not installed" in infos["podman"].reason
    unreachable = fake_run({"/d info": ProcResult(1, "", "Cannot connect to the Docker daemon", 0)})
    infos = {b.name: b for b in detect_backends(which={"docker": "/d"}.get, run=unreachable)}
    assert not infos["docker"].available and "not reachable" in infos["docker"].reason


# ------------------------------------------------------------------ argv


def test_podman_argv_denies_network_and_limits_mounts(tmp_path):
    cfg = SandboxConfig(image="forge-worker@sha256:1")
    b = PodmanBackend(PODMAN, cfg)
    wt = tmp_path / "wt"
    req = SandboxRequest(["dotnet", "test"], wt, [Mount(wt, str(wt), False)], NetworkPolicy(), {"A": "1"},
                         name="forge-x", gpu="GPU-123")
    argv = b.argv(req)
    s = " ".join(argv)
    assert argv[:3] == ["/usr/bin/podman", "run", "--rm"]
    assert "--network none" in s and "--read-only" in s and "--cap-drop ALL" in s
    assert "no-new-privileges" in s and "--userns keep-id" in s
    assert f"{wt}:{wt}:rw" in s and str(Path.home()) + ":" not in s and "docker.sock" not in s
    assert "nvidia.com/gpu=GPU-123" in s and argv[-3:] == ["forge-worker@sha256:1", "dotnet", "test"]
    assert "--env HOME=/tmp/home" in s and "--env A=1" in s


def test_allowlist_needs_proxy_and_internal_network():
    restore = NetworkPolicy(NetworkMode.ALLOWLIST, ("api.nuget.org",))
    b = DockerBackend(BackendInfo("docker", "/usr/bin/docker", True, rootless=True), SandboxConfig(image="img"))
    ok, why = b.can_enforce(restore)
    assert not ok and "egress_proxy" in why
    cfg = SandboxConfig(image="img", egress_proxy="http://10.0.9.2:3128", restore_network="forge-restore")
    b = DockerBackend(BackendInfo("docker", "/usr/bin/docker", True, rootless=True), cfg)
    assert b.can_enforce(restore)[0]
    s = " ".join(b.argv(SandboxRequest(["dotnet", "restore"], Path("/w"), [], restore)))
    assert "--network forge-restore" in s and "HTTPS_PROXY=http://10.0.9.2:3128" in s
    assert "FORGE_EGRESS_ALLOWLIST=api.nuget.org" in s
    assert not PodmanBackend(PODMAN, SandboxConfig()).can_enforce(NetworkPolicy())[0]  # no pinned image


def test_bwrap_argv_unshares_everything_and_refuses_allowlist(tmp_path):
    b = BwrapBackend(BWRAP, SandboxConfig())
    assert not b.can_enforce(NetworkPolicy(NetworkMode.ALLOWLIST, ("pypi.org",)))[0]
    wt = tmp_path / "wt"
    argv = b.argv(SandboxRequest(["python3", "-m", "unittest"], wt, [Mount(wt, str(wt), False)], NetworkPolicy()))
    s = " ".join(argv)
    assert "--unshare-all" in s and "--share-net" not in s and "--clearenv" in s
    assert f"--bind {wt} {wt}" in s and "--die-with-parent" in s
    assert argv[argv.index("--") + 1:] == ["python3", "-m", "unittest"]


# ------------------------------------------------------------------ runner


def test_runner_without_backend_runs_supervised_and_says_so(tmp_path):
    ex = RecordingExecutor()
    r = SandboxRunner(SandboxConfig(), backends=[], executor=ex, forbidden=no_home_forbidden(tmp_path), home=FAKE_HOME)
    res = r.run(["echo", "hi"], workdir=tmp_path, role=WorkerRole.GENERATED_CODE_CHECK)
    assert res.containment == SUPERVISED and not res.contained
    ev = res.evidence()
    assert "NOT CONTAINED" in ev["statement"] and "unrestricted" in ev["network"]
    assert ex.calls[0][0] == ["echo", "hi"]
    assert not any(k.endswith("API_KEY") for k in ex.calls[0][2])


def test_runner_require_containment_refuses(tmp_path):
    r = SandboxRunner(SandboxConfig(require_containment=True), backends=[BackendInfo("podman", None, False, "absent")],
                      executor=RecordingExecutor(), forbidden=no_home_forbidden(tmp_path), home=FAKE_HOME)
    with pytest.raises(SandboxRefused, match="requires containment"):
        r.run(["true"], workdir=tmp_path, role=WorkerRole.GENERATED_CODE_CHECK)


def test_runner_enforced_with_backend_and_kills_container_on_timeout(tmp_path):
    ex = RecordingExecutor(timed_out=True)
    r = SandboxRunner(SandboxConfig(image="img"), backends=[PODMAN], executor=ex,
                      forbidden=no_home_forbidden(tmp_path), home=FAKE_HOME)
    res = r.run(["sleep", "99"], workdir=tmp_path, role=WorkerRole.GENERATED_CODE_CHECK, timeout_s=1)
    assert res.containment == ENFORCED and res.backend == "podman" and "--network none" in res.network
    wrapped = ex.calls[0][0]
    assert wrapped[:2] == ["/usr/bin/podman", "run"]
    # a timed-out container is removed explicitly: killing the client does not stop it
    assert res.proc.timed_out
    assert ex.calls[-1][0][1:3] == ["rm", "--force"]


def test_runner_falls_back_to_supervised_when_backend_cannot_enforce_allowlist(tmp_path):
    ex = RecordingExecutor()
    r = SandboxRunner(SandboxConfig(), backends=[BWRAP], executor=ex, forbidden=no_home_forbidden(tmp_path), home=FAKE_HOME)
    res = r.run(["dotnet", "restore"], workdir=tmp_path, role=WorkerRole.DEPENDENCY_RESTORE)
    assert res.containment == SUPERVISED and any("allow-list" in x for x in res.reasons)
    res = r.run(["dotnet", "test"], workdir=tmp_path, role=WorkerRole.GENERATED_CODE_CHECK)
    assert res.containment == ENFORCED and res.backend == "bwrap"


def test_runner_refuses_forbidden_mount_before_running(tmp_path):
    ex = RecordingExecutor()
    home = tmp_path / "home"
    home.mkdir()
    r = SandboxRunner(SandboxConfig(), backends=[], executor=ex, forbidden=builtin_forbidden_paths(home=home),
                      home=home)
    with pytest.raises(SandboxRefused):
        r.run(["true"], workdir=home, role=WorkerRole.GENERATED_CODE_CHECK)
    assert ex.calls == []


def test_execute_kills_process_group_on_cancel(tmp_path):
    cancel = threading.Event()
    threading.Timer(0.3, cancel.set).start()
    t0 = time.monotonic()
    res = execute([sys.executable, "-c", "import time; time.sleep(30)"], tmp_path, 60, dict(os.environ), cancel)
    assert res.timed_out and time.monotonic() - t0 < 10
    ok = execute([sys.executable, "-c", "print('hello')"], tmp_path, 30, dict(os.environ))
    assert ok.returncode == 0 and "hello" in ok.stdout


# ------------------------------------------------------------------ sandboxed check


def test_sandboxed_check_routes_inner_processes_and_records_containment(tmp_path):
    ex = RecordingExecutor()
    cfg = SandboxConfig(image="img", egress_proxy="http://proxy:3128", restore_network="forge-restore")
    runner = SandboxRunner(cfg, backends=[PODMAN], executor=ex, forbidden=no_home_forbidden(tmp_path), home=FAKE_HOME)
    chk = SandboxedCheck(CommandCheck("unit", ["python3", "-m", "unittest"]), runner,
                         restore_argv=["pip", "download", "x"])
    oc = chk.run(tmp_path, CheckContext())
    assert oc.status == EvidenceStatus.PASS
    c = oc.details["containment"]
    assert c["containment"] == ENFORCED
    assert [e["role"] for e in c["executions"]] == ["dependency_restore", "generated_code_check"]
    assert "forge-restore" in c["executions"][0]["network"] and "none" in c["executions"][1]["network"]
    assert all(call[0][0] == "/usr/bin/podman" for call in ex.calls)


def test_sandboxed_check_supervised_summary_and_blocked_on_refusal(tmp_path):
    runner = SandboxRunner(SandboxConfig(), backends=[], forbidden=no_home_forbidden(tmp_path), home=FAKE_HOME)
    chk = SandboxedCheck(CommandCheck("py", [sys.executable, "-c", "print(1)"]), runner)
    oc = chk.run(tmp_path, CheckContext())
    assert oc.status == EvidenceStatus.PASS and "SUPERVISED" in oc.summary
    assert oc.details["containment"]["containment"] == SUPERVISED
    strict = SandboxRunner(SandboxConfig(require_containment=True), backends=[],
                           forbidden=no_home_forbidden(tmp_path), home=FAKE_HOME)
    oc = SandboxedCheck(CommandCheck("py", [sys.executable, "-c", "print(1)"]), strict).run(tmp_path, CheckContext())
    assert oc.status == EvidenceStatus.BLOCKED and oc.details["containment"]["containment"] == NOT_RUN


def test_build_check_wraps_when_spec_requests_sandbox(tmp_path):
    chk = build_check("rules", {"type": "command", "argv": ["true"], "sandbox": "generated_code_check",
                                "sandbox_config": str(tmp_path / "absent.toml")})
    assert isinstance(chk, SandboxedCheck) and chk.role == WorkerRole.GENERATED_CODE_CHECK
    bad = tmp_path / "bad.toml"
    bad.write_text("[sandbox]\nbackend='x'\n")
    os.chmod(bad, 0o600)
    chk = build_check("rules", {"type": "command", "argv": ["true"], "sandbox": True, "sandbox_config": str(bad)})
    assert not chk.availability(CheckContext())[0]


# ------------------------------------------------------------------ orchestrator


def test_supervised_checks_route_candidate_to_owner(make_env, tmp_path):
    runner = SandboxRunner(SandboxConfig(), backends=[], forbidden=no_home_forbidden(tmp_path), home=FAKE_HOME)
    checks = {"add": SandboxedCheck(unittest_check("add", "test_add.py"), runner),
              "mul": SandboxedCheck(unittest_check("mul", "test_mul.py"), runner)}
    env = make_env(checks=checks)
    t = env.task()
    env.orch.approve_task(t.id)
    env.orch.run_until_idle()
    root = env.store.get_root(t.id)
    assert root.state == S.AWAITING_APPROVAL
    assert "supervised mode" in root.state_reason
    evs = [e for e in env.store.list_evidence(t.id) if e.name in ("add", "mul")]
    assert evs and all(e.details["containment"]["containment"] == SUPERVISED for e in evs)
