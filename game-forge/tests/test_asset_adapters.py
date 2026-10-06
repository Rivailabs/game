"""Generation adapters with fake transports/runners, and the durable job runner."""

from __future__ import annotations

import base64
import json
import sys
import time
from pathlib import Path

import pytest

from forge.assets.adapters import (
    AdapterError,
    AdapterUnavailable,
    CreditPricing,
    DeepMotionAdapter,
    GenerationJob,
    ImageAPIAdapter,
    JobTimeout,
    LicensedLibraryAdapter,
    LocalModelAdapter,
    ManualStepAdapter,
    ManualStepPending,
    MeshyAdapter,
    TripoAdapter,
    UncertainSubmission,
    build_adapters,
    run_job,
)
from forge.assets.adapters.http import HttpResponse, TransportRefused, check_host, multipart
from forge.assets.registry import AssetRegistry
from forge.credentials import redact
from forge.sandbox import SandboxConfig, SandboxRunner
from forge.sandbox.policy import builtin_forbidden_paths
from forge.store import Store
from forge.util import FakeClock

from .asset_fakes import FakeTransport, sequence

KEY = "msy_live_secret_key_1234567890"


@pytest.fixture
def registry(tmp_path):
    return AssetRegistry(Store(tmp_path / "db" / "forge.db", clock=FakeClock()))


def job(tmp_path, op="text_to_3d", **kw) -> GenerationJob:
    base = dict(idempotency_key=f"archer:v1:generation:{op}", asset_id="archer", route_id="meshy",
                stage="default", operation=op, inputs={"prompt": "an original fantasy archer, neutral A-pose"},
                params={"target_polycount": 8000}, out_dir=tmp_path / "out")
    base.update(kw)
    return GenerationJob(**base)


def run(adapter, j, registry, **kw):
    clock = registry.store.clock
    return run_job(adapter, j, registry=registry, root_id="r1", reservation_id=None, sleep=clock.sleep,
                   now=clock.now, poll_interval_s=5, **kw)


# ------------------------------------------------------------------ http


def test_host_allow_list_and_multipart():
    check_host("https://api.meshy.ai/openapi/v2/text-to-3d", {"api.meshy.ai"})
    check_host("https://assets.meshy.ai/x.glb", {".meshy.ai"})
    with pytest.raises(TransportRefused):
        check_host("https://evil.example.com/x", {"api.meshy.ai", ".meshy.ai"})
    with pytest.raises(TransportRefused):
        check_host("http://api.meshy.ai/x", {"api.meshy.ai"})
    body, ctype = multipart({"a": "1"}, {"file": ("x.png", b"PNG", "image/png")})
    assert ctype.startswith("multipart/form-data; boundary=") and b'filename="x.png"' in body


# ------------------------------------------------------------------ Meshy


def meshy_transport(final_status="SUCCEEDED"):
    task = {"id": "t-1", "status": final_status, "progress": 100,
            "model_urls": {"glb": "https://assets.meshy.ai/t-1/model.glb", "fbx": "https://assets.meshy.ai/t-1/m.fbx"},
            "texture_urls": [{"base_color": "https://assets.meshy.ai/t-1/base.png"}],
            "thumbnail_url": "https://assets.meshy.ai/t-1/thumb.png",
            "task_error": {"message": "nsfw" if final_status == "FAILED" else ""}}
    return FakeTransport({
        ("POST", "https://api.meshy.ai/openapi/v2/text-to-3d"): {"result": "t-1"},
        ("GET", "https://api.meshy.ai/openapi/v2/text-to-3d/t-1"): sequence(
            {"id": "t-1", "status": "PENDING", "progress": 0}, {"id": "t-1", "status": "IN_PROGRESS", "progress": 50},
            task),
        ("GET", "https://assets.meshy.ai/"): b"binary-asset",
        ("GET", "https://api.meshy.ai/openapi/v1/balance"): {"balance": 100},
    })


def test_meshy_text_to_3d_request_shape_polling_and_download(tmp_path, registry):
    t = meshy_transport()
    a = MeshyAdapter(lambda: KEY, transport=t, pricing=CreditPricing(0.02, {"text_to_3d": 20}))
    j = job(tmp_path)
    assert a.estimate_ceiling_micros(j) == 400_000
    res = run(a, j, registry)
    post = t.calls[0]
    assert post["json"] == {"mode": "preview", "prompt": "an original fantasy archer, neutral A-pose",
                            "art_style": "realistic", "should_remesh": True, "topology": "triangle",
                            "target_polycount": 8000}
    assert post["headers"]["Authorization"] == f"Bearer {KEY}"
    assert redact(f"key {KEY}") == "key [REDACTED]"  # registered for redaction
    assert res.provider_job_id == "t2d:t-1" and res.polls == 3
    assert set(res.files) == {"model.glb", "model.fbx", "texture_0_base_color.png", "preview.png"}
    assert all(len(h) == 64 for h in res.hashes.values())
    row = registry.job(j.idempotency_key)
    assert row["status"] == "SUCCEEDED" and row["provider_job_id"] == "t2d:t-1"
    # a second run (e.g. after a restart) re-polls the recorded job and never resubmits
    t2 = meshy_transport()
    a2 = MeshyAdapter(lambda: KEY, transport=t2)
    run(a2, j, registry)
    assert not any(c["method"] == "POST" for c in t2.calls)


def test_meshy_image_to_3d_and_rig_shapes(tmp_path, registry):
    img = tmp_path / "concept.png"
    img.write_bytes(b"\x89PNG fake")
    t = FakeTransport({("POST", "https://api.meshy.ai/openapi/v1/image-to-3d"): {"result": "i-9"},
                       ("POST", "https://api.meshy.ai/openapi/v1/rigging"): {"result": "r-3"}})
    a = MeshyAdapter(lambda: KEY, transport=t)
    assert a.submit(job(tmp_path, "image_to_3d", inputs={"image": img})) == "i2d:i-9"
    body = t.calls[0]["json"]
    assert body["image_url"].startswith("data:image/png;base64,") and body["target_polycount"] == 8000
    assert a.submit(job(tmp_path, "rig", params={"input_task_id": "i-9", "height_m": 1.75})) == "rig:r-3"
    assert t.calls[1]["json"] == {"height_meters": 1.75, "input_task_id": "i-9"}
    with pytest.raises(AdapterError):
        a.submit(job(tmp_path, "text_to_motion"))


def test_meshy_failure_refuses_foreign_download_and_unknown_cost(tmp_path, registry):
    a = MeshyAdapter(lambda: KEY, transport=meshy_transport("FAILED"))
    with pytest.raises(AdapterError, match="failed: nsfw"):
        run(a, job(tmp_path), registry)
    assert registry.job(job(tmp_path).idempotency_key)["status"] == "FAILED"
    assert MeshyAdapter(lambda: KEY).estimate_ceiling_micros(job(tmp_path)) is None  # no pricing configured
    evil = FakeTransport({("POST", "https://api.meshy.ai/openapi/v2/text-to-3d"): {"result": "t-1"},
                          ("GET", "https://api.meshy.ai/openapi/v2/text-to-3d/t-1"): {
                              "status": "SUCCEEDED", "model_urls": {"glb": "https://attacker.example/x.glb"}}})
    a = MeshyAdapter(lambda: KEY, transport=evil)
    with pytest.raises(TransportRefused):
        run(a, job(tmp_path, idempotency_key="k2"), registry)
    assert not any("attacker" in c["url"] for c in evil.calls)


def test_missing_credential_is_unavailable_and_nothing_is_sent(tmp_path):
    def no_key():
        raise RuntimeError("credential for meshy not found")
    t = FakeTransport({})
    a = MeshyAdapter(no_key, transport=t)
    assert not a.available()[0]
    with pytest.raises(AdapterUnavailable):
        a.submit(job(tmp_path))
    assert t.calls == []


# ------------------------------------------------------------------ Tripo


def test_tripo_upload_task_poll_and_credit_cost(tmp_path, registry):
    img = tmp_path / "concept.png"
    img.write_bytes(b"\x89PNG fake")
    t = FakeTransport({
        ("POST", "https://api.tripo3d.ai/v2/openapi/upload"): {"code": 0, "data": {"image_token": "tok-1"}},
        ("POST", "https://api.tripo3d.ai/v2/openapi/task"): {"code": 0, "data": {"task_id": "tp-7"}},
        ("GET", "https://api.tripo3d.ai/v2/openapi/task/tp-7"): sequence(
            {"code": 0, "data": {"task_id": "tp-7", "status": "running", "progress": 40}},
            {"code": 0, "data": {"task_id": "tp-7", "type": "image_to_model", "status": "success", "progress": 100,
                                 "consumed_credit": 30,
                                 "output": {"pbr_model": "https://cdn.tripo3d.com/tp-7.glb",
                                            "rendered_image": "https://cdn.tripo3d.com/tp-7.png"}}}),
        ("GET", "https://cdn.tripo3d.com/"): b"glb",
    })
    a = TripoAdapter(lambda: "tsk_secret_tripo_000000", transport=t, pricing=CreditPricing(0.01, {"image_to_3d": 40}))
    charged = []
    res = run(a, job(tmp_path, "image_to_3d", inputs={"image": img}, route_id="tripo"), registry,
              on_cost=lambda c, u: charged.append((c, u)))
    assert t.calls[0]["data"] and b"tok" not in t.calls[0]["data"]
    task_body = t.calls[1]["json"]
    assert task_body["type"] == "image_to_model" and task_body["file"] == {"type": "png", "file_token": "tok-1"}
    assert task_body["face_limit"] == 8000
    assert charged == [(300_000, {"credits": 30})]
    assert set(res.files) == {"model.glb", "preview.png"}


def test_tripo_rig_and_retarget_bodies(tmp_path):
    t = FakeTransport({("POST", "https://api.tripo3d.ai/v2/openapi/task"): {"code": 0, "data": {"task_id": "x"}}})
    a = TripoAdapter(lambda: "tsk_secret_tripo_000000", transport=t)
    a.submit(job(tmp_path, "rig", params={"model_task_id": "tp-7"}))
    a.submit(job(tmp_path, "retarget", params={"rig_task_id": "rig-1", "animation": "preset:idle"}))
    assert t.calls[0]["json"] == {"type": "animate_rig", "original_model_task_id": "tp-7", "out_format": "fbx"}
    assert t.calls[1]["json"]["type"] == "animate_retarget" and t.calls[1]["json"]["animation"] == "preset:idle"


# ------------------------------------------------------------------ DeepMotion


def test_deepmotion_session_upload_process_download(tmp_path, registry):
    video = tmp_path / "ref.mp4"
    video.write_bytes(b"video")
    t = FakeTransport({
        ("GET", "https://service.deepmotion.com/account/v1/auth"): HttpResponse(
            200, b"{}", {"Set-Cookie": "dmsess=session-secret-abcdef; Path=/"}),
        ("GET", "https://service.deepmotion.com/upload"): {"url": "https://storage.googleapis.com/dm/ref.mp4?sig=1"},
        ("PUT", "https://storage.googleapis.com/"): b"",
        ("POST", "https://service.deepmotion.com/job/v1/process"): {"rid": "rid-5"},
        ("GET", "https://service.deepmotion.com/job/v1/status/rid-5"): sequence(
            {"count": 1, "status": [{"rid": "rid-5", "status": "PROGRESS", "details": {"step": 1, "total": 2}}]},
            {"count": 1, "status": [{"rid": "rid-5", "status": "SUCCESS"}]}),
        ("GET", "https://service.deepmotion.com/job/v1/download/rid-5"): {
            "links": [{"rid": "rid-5", "urls": [{"files": [{"fbx": "https://storage.googleapis.com/dm/out.fbx"}]}]}]},
        ("GET", "https://storage.googleapis.com/dm/out.fbx"): b"fbx",
    })
    a = DeepMotionAdapter(lambda: "client:secret", transport=t)
    j = job(tmp_path, "video_to_motion", inputs={"video": video}, route_id="deepmotion",
            params={"model_id": "m1", "root_motion_in_place": True})
    res = run(a, j, registry)
    auth = t.calls[0]["headers"]["Authorization"]
    assert auth == "Basic " + base64.b64encode(b"client:secret").decode()
    proc = next(c for c in t.calls if c["url"].endswith("/job/v1/process"))
    assert proc["json"]["processor"] == "video2anim" and "rootAtOrigin=1" in proc["json"]["params"]
    assert proc["headers"]["cookie"] == "dmsess=session-secret-abcdef"
    assert list(res.files) == ["clip.fbx"]


# ------------------------------------------------------------------ configured image API


def test_image_api_is_pinned_to_its_endpoint(tmp_path, registry):
    png = base64.b64encode(b"\x89PNG concept").decode()
    t = FakeTransport({("POST", "https://images.example.com/v1/generate"): {"images": [{"b64": png}]}})
    a = ImageAPIAdapter(lambda: "img_secret_key_000000", endpoint="https://images.example.com/v1/generate",
                        vendor="example", transport=t)
    res = run(a, job(tmp_path, "concept_image", route_id="api-image-service", params={"seed": 7}), registry)
    assert t.calls[0]["json"] == {"prompt": "an original fantasy archer, neutral A-pose", "width": 1024,
                                  "height": 1024, "seed": 7}
    assert res.files["concept_0.png"].read_bytes() == b"\x89PNG concept"


# ------------------------------------------------------------------ local model route


def local_runner(tmp_path):
    home = Path("/nonexistent-forge-home")
    return SandboxRunner(SandboxConfig(), backends=[], forbidden=builtin_forbidden_paths(home=home), home=home)


def test_local_route_requires_pinned_command(tmp_path):
    a = LocalModelAdapter("trellis2", argv=None)
    ok, why = a.available()
    assert not ok and "no pinned command" in why
    with pytest.raises(AdapterUnavailable):
        a.submit(job(tmp_path, route_id="trellis2"))
    with pytest.raises(ValueError):
        LocalModelAdapter("meshy", argv=["x"])


def test_local_route_runs_in_sandbox_and_collects_outputs(tmp_path, registry):
    script = tmp_path / "fake_model.py"
    script.write_text("import sys, pathlib\nout = pathlib.Path(sys.argv[1])\n"
                      "(out / 'mesh.glb').write_bytes(b'glTF' + sys.argv[2].encode())\n")
    a = LocalModelAdapter("trellis2", argv=[sys.executable, str(script), "{out_dir}", "{prompt}"],
                          outputs=["*.glb"], runner=local_runner(tmp_path))
    clock = registry.store.clock
    res = run_job(a, job(tmp_path, route_id="trellis2", gpu_uuid="GPU-1"), registry=registry, root_id="r1",
                  reservation_id=None, sleep=lambda s: time.sleep(0.05), now=clock.now, poll_interval_s=0.05)
    assert list(res.files) == ["mesh.glb"] and res.files["mesh.glb"].read_bytes().startswith(b"glTF")
    assert res.status.raw["containment"]["containment"] == "supervised"


def test_local_route_cancel_on_deadline(tmp_path, registry):
    script = tmp_path / "slow.py"
    script.write_text("import time; time.sleep(60)\n")
    a = LocalModelAdapter("trellis2", argv=[sys.executable, str(script)], runner=local_runner(tmp_path))
    clock = registry.store.clock
    t0 = time.monotonic()

    def sleep(s):
        time.sleep(0.05)
        clock.advance(100)
    with pytest.raises(JobTimeout, match="cancellation confirmed"):
        run_job(a, job(tmp_path, route_id="trellis2"), registry=registry, root_id="r1", reservation_id=None,
                sleep=sleep, now=clock.now, deadline=clock.now() + 150, poll_interval_s=0.05)
    assert time.monotonic() - t0 < 30
    assert registry.job(job(tmp_path).idempotency_key)["status"] == "CANCELLED"


def test_uncertain_submission_is_never_resubmitted(tmp_path, registry):
    t = meshy_transport()
    j = job(tmp_path)
    registry.record_job(j.idempotency_key, asset_id="archer", root_id="r1", route="meshy", stage="default",
                        reservation_id=None)  # crashed after recording, before the id came back
    with pytest.raises(UncertainSubmission):
        run(MeshyAdapter(lambda: KEY, transport=t), j, registry)
    assert t.calls == [] and registry.job(j.idempotency_key)["status"] == "UNKNOWN"


# ------------------------------------------------------------------ manual + library


def test_mixamo_manual_step_inbox(tmp_path, registry):
    model = tmp_path / "archer.fbx"
    model.write_bytes(b"fbx")
    a = ManualStepAdapter("mixamo", tmp_path / "inbox")
    j = job(tmp_path, "rig", route_id="mixamo", inputs={"model": model},
            params={"expected_files": ["archer_rigged.fbx"]})
    with pytest.raises(ManualStepPending, match="waiting for the owner"):
        run(a, j, registry)
    box = next((tmp_path / "inbox").iterdir())
    text = (box / "INSTRUCTIONS.md").read_text()
    assert "mixamo.com" in text and "archer_rigged.fbx" in text and (box / "archer.fbx").exists()
    (box / "archer_rigged.fbx").write_bytes(b"rigged")
    (box / "done.json").write_text(json.dumps({"by": "owner", "terms": "Adobe Mixamo terms"}))
    res = run(a, j, registry)
    assert res.files["archer_rigged.fbx"].read_bytes() == b"rigged"
    assert res.status.usage["confirmed_by"] == "owner"


def test_licensed_library_import_requires_licence_manifest(tmp_path, registry):
    a = LicensedLibraryAdapter("licensed-sound-library", str(tmp_path / "lib"))
    assert not a.available()[0]
    (tmp_path / "lib" / "bow").mkdir(parents=True)
    (tmp_path / "lib" / "bow" / "draw_01.wav").write_bytes(b"RIFF")
    (tmp_path / "lib" / "library.json").write_text(json.dumps({
        "library": "Example SFX", "licence": "single seat", "licence_id": "INV-1",
        "items": [{"file": "bow/draw_01.wav", "tags": ["bow"], "purpose": "draw"},
                  {"file": "../escape.wav", "tags": ["bow"], "purpose": "hit"}]}))
    res = run(a, job(tmp_path, "library_import", route_id="licensed-sound-library",
                     params={"purpose": "draw", "tags": ["bow"]}), registry)
    assert list(res.files) == ["draw_01.wav"] and res.status.usage["rights"]["licence_id"] == "INV-1"
    with pytest.raises(AdapterError, match="missing or outside"):
        run(a, job(tmp_path, "library_import", route_id="licensed-sound-library", idempotency_key="k-hit",
                   params={"purpose": "hit"}), registry)


def test_build_adapters_from_config(tmp_path):
    ad = build_adapters({"meshy": {"credit_usd": 0.02, "credits": {"text_to_3d": 20}},
                         "trellis2": {"argv": ["python", "run.py", "{out_dir}"]},
                         "mixamo": {}, "licensed-sound-library": {"path": str(tmp_path)}},
                        project_id="p", repo_roots=[str(tmp_path / "repo")], data_dir=tmp_path)
    assert isinstance(ad["meshy"], MeshyAdapter) and isinstance(ad["trellis2"], LocalModelAdapter)
    assert ad["meshy"].estimate_ceiling_micros(job(tmp_path)) == 400_000
    with pytest.raises(ValueError, match="unknown route"):
        build_adapters({"midjourney": {}}, project_id="p", repo_roots=[], data_dir=tmp_path)
