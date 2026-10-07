"""Check runners, device adapter, Unity adapter, credential broker and redaction."""

import os
import stat
import sys
import textwrap
from pathlib import Path

import pytest

from forge.checks import (
    CheckContext,
    CommandCheck,
    DeviceScenarioCheck,
    UnityBuildCheck,
    build_check,
    parse_adb_devices,
    parse_console_summary,
    parse_trx,
)
from forge.checks.device import AdbDeviceService
from forge.credentials import CredentialBroker, CredentialError, ProviderCredentialSpec, redact, register_secret, scrubbed_env
from forge.models import EvidenceStatus, ToolchainManifest, ToolStatus


def test_command_check_pass_fail_timeout_missing(tmp_path):
    ctx = CheckContext()
    ok = CommandCheck("ok", [sys.executable, "-c", "print('hi')"]).run(tmp_path, ctx)
    assert ok.status == EvidenceStatus.PASS and "hi" in ok.logs["stdout"]
    bad = CommandCheck("bad", [sys.executable, "-c", "import sys; sys.exit(3)"]).run(tmp_path, ctx)
    assert bad.status == EvidenceStatus.FAIL and bad.details["exit_code"] == 3
    slow = CommandCheck("slow", [sys.executable, "-c", "import time; time.sleep(5)"], timeout_s=0.5).run(tmp_path, ctx)
    assert slow.status == EvidenceStatus.FAIL and slow.details["timed_out"]
    missing = CommandCheck("missing", ["definitely-not-a-tool-xyz"]).run(tmp_path, ctx)
    assert missing.status == EvidenceStatus.BLOCKED
    assert CommandCheck("missing", ["definitely-not-a-tool-xyz"]).availability(ctx)[0] is False
    with pytest.raises(TypeError):
        CommandCheck("shell", "echo hi")


def test_checks_never_see_secrets(tmp_path, monkeypatch):
    monkeypatch.setenv("ANTHROPIC_API_KEY", "sk-ant-api03-supersecretvalue1234567890")
    monkeypatch.setenv("SOME_TOKEN", "tok")
    out = CommandCheck("env", [sys.executable, "-c",
                               "import os;print(os.environ.get('ANTHROPIC_API_KEY'), os.environ.get('SOME_TOKEN'), "
                               "os.environ.get('FORGE_SANDBOX'))"]).run(tmp_path, CheckContext())
    assert out.logs["stdout"].strip() == "None None 1"
    env = scrubbed_env()
    assert "ANTHROPIC_API_KEY" not in env and "PATH" in env
    with pytest.raises(ValueError):
        scrubbed_env(extra={"MY_API_KEY": "x"})


def test_redaction():
    register_secret("my-very-secret-key-123")
    text = "key=my-very-secret-key-123 x-api-key: abc sk-ant-api03-AAAAAAAAAAAAAAAAAAAA Bearer abcdefghijklmnop"
    red = redact(text)
    assert "my-very-secret-key-123" not in red and "sk-ant-api03" not in red and "abcdefghijklmnop" not in red


def test_credential_broker(tmp_path, monkeypatch):
    monkeypatch.delenv("FORGE_TEST_KEY", raising=False)
    repo = tmp_path / "repo"
    repo.mkdir()
    inside = repo / "key.txt"
    inside.write_text("inside-repo-key-0001")
    os.chmod(inside, 0o600)
    b = CredentialBroker(repo_roots=[str(repo)])
    b.register(ProviderCredentialSpec("prov", "FORGE_TEST_KEY", str(inside)))
    b.allow("proj", "prov")
    with pytest.raises(CredentialError, match="inside a project"):
        b.get("proj", "prov")
    outside = tmp_path / "secret.key"
    outside.write_text("outside-key-0002\n")
    os.chmod(outside, 0o644)
    b.register(ProviderCredentialSpec("prov", "FORGE_TEST_KEY", str(outside)))
    with pytest.raises(CredentialError, match="chmod 600"):
        b.get("proj", "prov")
    os.chmod(outside, 0o600)
    assert b.get("proj", "prov") == "outside-key-0002"
    assert "outside-key-0002" not in redact("leak outside-key-0002")
    with pytest.raises(CredentialError, match="not authorised"):
        b.get("other-project", "prov")
    monkeypatch.setenv("FORGE_TEST_KEY", "env-key-000000003")
    assert b.get("proj", "prov") == "env-key-000000003"


# ------------------------------------------------------------------ dotnet parsing

def test_parse_console_summary():
    text = ("Passed!  - Failed:     0, Passed:    12, Skipped:     1, Total:    13, Duration: 51 ms - A.dll (net8.0)\n"
            "Failed!  - Failed:     2, Passed:     3, Skipped:     0, Total:     5, Duration: 9 ms - B.dll (net8.0)\n")
    s = parse_console_summary(text)
    assert (s["total"], s["passed"], s["failed"], s["skipped"], s["assemblies"]) == (18, 15, 2, 1, 2)
    assert parse_console_summary("No test is available in x.dll")["no_tests"]


def test_parse_trx(tmp_path):
    trx = tmp_path / "r.trx"
    trx.write_text(textwrap.dedent("""\
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult testName="Fire_beats_wind" outcome="Passed" duration="00:00:00.01"/>
            <UnitTestResult testName="Tie_has_zero_margin" outcome="Failed" duration="00:00:00.02">
              <Output><ErrorInfo><Message>Expected 0 but was 1</Message></ErrorInfo></Output>
            </UnitTestResult>
          </Results>
          <ResultSummary outcome="Failed"><Counters total="2" executed="2" passed="1" failed="1" error="0"/></ResultSummary>
        </TestRun>"""))
    r = parse_trx(trx)
    assert r["counters"]["total"] == 2 and r["counters"]["failed"] == 1
    assert r["failures"] == [{"name": "Tie_has_zero_margin", "message": "Expected 0 but was 1"}]


# ------------------------------------------------------------------ device service

def fake_adb(tmp_path, devices_output: str, logcat: str = "") -> str:
    p = tmp_path / "adb"
    script = "\n".join([
        f"#!{sys.executable}",
        "import sys",
        "args = sys.argv[1:]",
        "if args[:1] == ['-s']:",
        "    args = args[2:]",
        "if args[:1] == ['devices']:",
        f"    print({devices_output!r})",
        "elif args[:2] == ['shell', 'getprop']:",
        "    print({'ro.product.model': 'Pixel 7', 'ro.build.version.release': '14',",
        "           'ro.product.cpu.abi': 'arm64-v8a'}.get(args[2], ''))",
        "elif args[:2] == ['logcat', '-d']:",
        f"    print({logcat!r})",
        "else:",
        "    print('ok')",
    ]) + "\n"
    p.write_text(script)
    p.chmod(p.stat().st_mode | stat.S_IEXEC)
    return str(p)


def test_device_absent_adb_is_incomplete(tmp_path):
    chk = DeviceScenarioCheck("device", serial="ABC123", adb_path=str(tmp_path / "no-adb"))
    out = chk.run(tmp_path, CheckContext())
    assert out.status == EvidenceStatus.INCOMPLETE and "adb" in out.summary
    assert chk.availability(CheckContext())[0] is False


def test_device_absent_from_path_is_incomplete(tmp_path, monkeypatch):
    monkeypatch.setenv("PATH", str(tmp_path))
    out = DeviceScenarioCheck("device").run(tmp_path, CheckContext())
    assert out.status == EvidenceStatus.INCOMPLETE


@pytest.mark.parametrize("listing,needle", [
    ("List of devices attached\n", "disconnected"),
    ("List of devices attached\nABC123\tunauthorized usb:1-1 transport_id:1", "authorised"),
    ("List of devices attached\nABC123\toffline", "offline"),
    ("List of devices attached\nemulator-5554\tdevice product:sdk_gphone64 model:sdk transport_id:2", "disconnected"),
])
def test_device_problems_are_incomplete_never_pass(tmp_path, listing, needle):
    adb = fake_adb(tmp_path, listing)
    out = DeviceScenarioCheck("device", serial="ABC123", adb_path=adb).run(tmp_path, CheckContext())
    assert out.status == EvidenceStatus.INCOMPLETE and needle in out.summary


def test_emulator_never_satisfies_device_gate(tmp_path):
    adb = fake_adb(tmp_path, "List of devices attached\nemulator-5554\tdevice product:sdk_gphone64 model:sdk")
    out = DeviceScenarioCheck("device", serial="emulator-5554", adb_path=adb).run(tmp_path, CheckContext())
    assert out.status == EvidenceStatus.INCOMPLETE and "emulator" in out.summary


def test_device_without_build_or_result_is_incomplete(tmp_path):
    adb = fake_adb(tmp_path, "List of devices attached\nABC123\tdevice model:Pixel_7", logcat="nothing here")
    out = DeviceScenarioCheck("device", serial="ABC123", adb_path=adb).run(tmp_path, CheckContext())
    assert out.status == EvidenceStatus.INCOMPLETE and "build" in out.summary
    (tmp_path / "game.apk").write_bytes(b"apk")
    out = DeviceScenarioCheck("device", serial="ABC123", adb_path=adb, apk="game.apk", package="p", activity="a",
                              wait_s=0).run(tmp_path, CheckContext())
    assert out.status == EvidenceStatus.INCOMPLETE and "did not report" in out.summary
    assert out.details["model"] == "Pixel_7" and out.details["android_version"] == "14"


def test_device_scenario_result_marker(tmp_path):
    adb = fake_adb(tmp_path, "List of devices attached\nABC123\tdevice model:Pixel_7",
                   logcat="10-06 I/Unity: FORGE_SCENARIO_RESULT: PASS seeds=3 frames=1800")
    (tmp_path / "game.apk").write_bytes(b"apk")
    out = DeviceScenarioCheck("device", serial="ABC123", adb_path=adb, apk="game.apk", package="p", activity="a",
                              wait_s=0).run(tmp_path, CheckContext())
    assert out.status == EvidenceStatus.PASS and out.details["scenario_report"] == "seeds=3 frames=1800"


def test_parse_adb_devices():
    devs = parse_adb_devices("List of devices attached\nR58N\tdevice usb:1 product:a54x model:SM_A546E transport_id:3\n")
    assert devs[0].serial == "R58N" and devs[0].model == "SM_A546E" and not devs[0].is_emulator
    assert AdbDeviceService(adb_path="/nonexistent/adb").list_devices() == []


# ------------------------------------------------------------------ unity

def test_unity_absent_is_blocked(tmp_path):
    chk = UnityBuildCheck()
    out = chk.run(tmp_path, CheckContext(manifest=None))
    assert out.status == EvidenceStatus.BLOCKED and "preflight" in out.summary
    m = ToolchainManifest(generated_at=0, profile="x", tools={"unity": ToolStatus(name="unity", found=False)})
    out = chk.run(tmp_path, CheckContext(manifest=m))
    assert out.status == EvidenceStatus.BLOCKED and "Unity editor not recorded" in out.summary
    m.unity_editor_path = str(tmp_path / "Unity")
    out = chk.run(tmp_path, CheckContext(manifest=m))
    assert out.status == EvidenceStatus.BLOCKED and "does not exist" in out.summary
    assert chk.availability(CheckContext(manifest=m))[0] is False


def test_unity_build_with_stub_editor(tmp_path):
    editor = tmp_path / "Unity"
    editor.write_text(f"#!{sys.executable}\nimport sys,os\na=sys.argv\np=a[a.index('-projectPath')+1]\n"
                      "os.makedirs(os.path.join(p,'Builds/Android'),exist_ok=True)\n"
                      "open(os.path.join(p,'Builds/Android/game.apk'),'w').write('apk')\n")
    editor.chmod(0o755)
    (tmp_path / "unity").mkdir()
    m = ToolchainManifest(generated_at=0, profile="x", unity_editor_path=str(editor),
                          tools={"unity": ToolStatus(name="unity", found=True, version="6000.0.23f1")})
    out = UnityBuildCheck().run(tmp_path, CheckContext(manifest=m))
    assert out.status == EvidenceStatus.PASS and out.details["editor_version"] == "6000.0.23f1"


def test_build_check_registry():
    assert build_check("x", {"type": "command", "argv": ["true"]}).name == "x"
    assert build_check("d", {"type": "dotnet_test", "project_dir": "astra-kingdoms"}).project_dir == "astra-kingdoms"
    assert isinstance(build_check("u", {"type": "unity_build"}), UnityBuildCheck)
    assert isinstance(build_check("v", {"type": "device", "serial": "S"}), DeviceScenarioCheck)
    with pytest.raises(ValueError):
        build_check("z", {"type": "nope"})
