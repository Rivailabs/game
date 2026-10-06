"""REST adapters for public 3D/motion APIs: Meshy, Tripo, DeepMotion, and a configured image API.

Request/response shapes follow each vendor's public API documentation as known when this adapter was
written; they are **not verified against the live services from this environment** (no network or
credentials here). That is why every API route has ``requires_certification``: the owner runs the
adapter checks with a real key (``forge assets certify <route>``) before unattended use. All tests use
a fake transport.

Costs: the vendors bill in credits. The owner configures ``credit_usd`` and ``credits`` per operation
in ``[assets.routes.<id>]``; without them the ceiling is unknown and unattended dispatch is refused.
"""

from __future__ import annotations

import base64
import time
from pathlib import Path
from typing import Any, Callable, Optional

from ...credentials import register_secret
from ...util import usd_to_micros
from ..routes import ROUTES
from .base import AdapterError, AdapterUnavailable, GenerationAdapter, GenerationJob, JobStatus
from .http import HttpTransport, UrllibTransport, bearer, check_host, download, multipart, ok_json

KeyProvider = Callable[[], str]


class CreditPricing:
    """Owner-configured credit prices -> micro-USD ceilings (None when not configured)."""

    def __init__(self, credit_usd: float | None = None, credits: dict[str, float] | None = None):
        self.credit_usd = credit_usd
        self.credits = dict(credits or {})

    def ceiling(self, operation: str) -> Optional[int]:
        if self.credit_usd is None or operation not in self.credits:
            return None
        return usd_to_micros(self.credit_usd * self.credits[operation])

    def cost(self, credits_used: Optional[float]) -> Optional[int]:
        if credits_used is None or self.credit_usd is None:
            return None
        return usd_to_micros(self.credit_usd * credits_used)


class _ApiAdapter(GenerationAdapter):
    base_url = ""
    allowed_hosts: set[str] = set()
    route_id = ""

    def __init__(self, key: KeyProvider | None, *, transport: HttpTransport | None = None,
                 pricing: CreditPricing | None = None, timeout_s: float = 60):
        self.route = ROUTES[self.route_id]
        self._key = key
        self.transport = transport or UrllibTransport(self.allowed_hosts)
        self.pricing = pricing or CreditPricing()
        self.timeout_s = timeout_s

    def available(self) -> tuple[bool, str]:
        if self._key is None:
            return False, f"no credential configured for {self.route_id}"
        try:
            self._key()
        except Exception as e:  # CredentialError and friends: nothing is sent
            return False, str(e)
        return True, ""

    def key(self) -> str:
        if self._key is None:
            raise AdapterUnavailable(f"no credential configured for {self.route_id}")
        try:
            k = self._key()
        except Exception as e:
            raise AdapterUnavailable(str(e)) from e
        register_secret(k)
        return k

    def _url(self, path: str) -> str:
        return self.base_url.rstrip("/") + path

    def _call(self, method: str, url: str, *, json_body: Any = None, data: bytes | None = None,
              headers: dict[str, str] | None = None) -> Any:
        check_host(url, self.allowed_hosts)
        hdrs = {**self._auth_headers(), **(headers or {})}
        resp = self.transport.request(method, url, headers=hdrs, json_body=json_body, data=data,
                                      timeout=self.timeout_s)
        return ok_json(resp, url)

    def _auth_headers(self) -> dict[str, str]:
        return bearer(self.key())

    def _download(self, url: str, dest: Path) -> Path:
        check_host(url, self.allowed_hosts)  # a response cannot point Forge at another host
        download(self.transport, url, dest)
        return dest

    def estimate_ceiling_micros(self, job: GenerationJob) -> Optional[int]:
        return self.pricing.ceiling(job.operation)


def _data_uri(path: Path) -> str:
    ext = path.suffix.lower().lstrip(".") or "png"
    mime = "image/jpeg" if ext in ("jpg", "jpeg") else f"image/{ext}"
    return f"data:{mime};base64," + base64.b64encode(path.read_bytes()).decode()


# --------------------------------------------------------------------------- Meshy


class MeshyAdapter(_ApiAdapter):
    """Meshy OpenAPI: text-to-3D (preview/refine), image-to-3D, rigging. Bearer auth.

    Task ids are prefixed with the endpoint kind so ``poll`` knows where to look
    (``t2d:<id>``, ``i2d:<id>``, ``rig:<id>``).
    """

    route_id = "meshy"
    base_url = "https://api.meshy.ai"
    allowed_hosts = {"api.meshy.ai", "assets.meshy.ai", ".meshy.ai"}
    model = "meshy"
    ENDPOINTS = {"t2d": "/openapi/v2/text-to-3d", "i2d": "/openapi/v1/image-to-3d", "rig": "/openapi/v1/rigging"}

    def submit(self, job: GenerationJob) -> str:
        p = job.params
        if job.operation == "text_to_3d":
            kind = "t2d"
            body = {"mode": p.get("mode", "preview"), "prompt": job.inputs["prompt"],
                    "art_style": p.get("art_style", "realistic"), "should_remesh": True, "topology": "triangle",
                    "target_polycount": int(p["target_polycount"])}
            if p.get("negative_prompt"):
                body["negative_prompt"] = p["negative_prompt"]
            if p.get("seed") is not None:
                body["seed"] = int(p["seed"])
            if p.get("mode") == "refine":
                body = {"mode": "refine", "preview_task_id": p["preview_task_id"], "enable_pbr": True}
        elif job.operation == "image_to_3d":
            kind = "i2d"
            body = {"image_url": _data_uri(Path(job.inputs["image"])), "enable_pbr": True, "should_remesh": True,
                    "topology": "triangle", "target_polycount": int(p["target_polycount"])}
        elif job.operation == "rig":
            kind = "rig"
            body = {"height_meters": float(p.get("height_m", 1.8))}
            if p.get("input_task_id"):
                body["input_task_id"] = p["input_task_id"]
            else:
                body["model_url"] = _data_uri_model(Path(job.inputs["model"]))
        else:
            raise AdapterError(f"meshy does not support {job.operation}")
        data = self._call("POST", self._url(self.ENDPOINTS[kind]), json_body=body)
        task = data.get("result") if isinstance(data, dict) else None
        if not task:
            raise AdapterError(f"meshy returned no task id: {str(data)[:200]}")
        return f"{kind}:{task}"

    def poll(self, provider_job_id: str) -> JobStatus:
        kind, _, task = provider_job_id.partition(":")
        data = self._call("GET", self._url(f"{self.ENDPOINTS[kind]}/{task}"))
        state = {"PENDING": "queued", "IN_PROGRESS": "running", "SUCCEEDED": "succeeded", "FAILED": "failed",
                 "CANCELED": "cancelled", "EXPIRED": "failed"}.get(str(data.get("status")), "running")
        outputs: dict[str, str] = {}
        if kind == "rig":
            res = data.get("result") or {}
            if res.get("rigged_character_fbx_url"):
                outputs["rigged.fbx"] = res["rigged_character_fbx_url"]
            if res.get("rigged_character_glb_url"):
                outputs["rigged.glb"] = res["rigged_character_glb_url"]
        else:
            urls = data.get("model_urls") or {}
            for fmt in ("glb", "fbx"):
                if urls.get(fmt):
                    outputs[f"model.{fmt}"] = urls[fmt]
            for i, tex in enumerate(data.get("texture_urls") or []):
                for k, v in tex.items():
                    outputs[f"texture_{i}_{k}.png"] = v
            if data.get("thumbnail_url"):
                outputs["preview.png"] = data["thumbnail_url"]
        err = (data.get("task_error") or {}).get("message", "")
        return JobStatus(state, provider_job_id, float(data.get("progress") or 0), err, outputs,
                         cost_micros=None, raw={"status": data.get("status")})

    def fetch(self, status: JobStatus, out_dir: Path) -> dict[str, Path]:
        if not status.outputs:
            raise AdapterError("meshy job succeeded without outputs")
        return {name: self._download(url, out_dir / name) for name, url in status.outputs.items()}

    def cancel(self, provider_job_id: str) -> str:
        return "uncertain"  # the API can delete a task record; that does not prove compute stopped or refunds

    def adapter_checks(self) -> list[dict]:
        checks = super().adapter_checks()
        if checks[0]["ok"]:
            try:
                self._call("GET", self._url("/openapi/v1/balance"))
                checks.append({"check": "authenticated balance call", "ok": True, "detail": ""})
            except Exception as e:
                checks.append({"check": "authenticated balance call", "ok": False, "detail": str(e)[:200]})
        return checks


def _data_uri_model(path: Path) -> str:
    return "data:application/octet-stream;base64," + base64.b64encode(path.read_bytes()).decode()


# --------------------------------------------------------------------------- Tripo


class TripoAdapter(_ApiAdapter):
    """Tripo OpenAPI v2: ``POST /task`` with a ``type``; ``GET /task/{id}``; image upload via ``/upload``."""

    route_id = "tripo"
    base_url = "https://api.tripo3d.ai/v2/openapi"
    allowed_hosts = {"api.tripo3d.ai", ".tripo3d.ai", ".tripo3d.com"}
    model = "tripo"
    STATES = {"queued": "queued", "running": "running", "success": "succeeded", "failed": "failed",
              "cancelled": "cancelled", "unknown": "running", "banned": "failed", "expired": "failed"}

    def _upload(self, image: Path) -> str:
        body, ctype = multipart({}, {"file": (image.name, image.read_bytes(),
                                              "image/png" if image.suffix.lower() == ".png" else "image/jpeg")})
        data = self._call("POST", self._url("/upload"), data=body, headers={"Content-Type": ctype})
        tok = (data.get("data") or {}).get("image_token") if data.get("code") == 0 else None
        if not tok:
            raise AdapterError(f"tripo upload failed: {str(data)[:200]}")
        return tok

    def submit(self, job: GenerationJob) -> str:
        p = job.params
        if job.operation == "text_to_3d":
            body = {"type": "text_to_model", "prompt": job.inputs["prompt"], "face_limit": int(p["target_polycount"]),
                    "texture": True, "pbr": True}
        elif job.operation == "image_to_3d":
            img = Path(job.inputs["image"])
            body = {"type": "image_to_model", "file": {"type": img.suffix.lower().lstrip(".") or "png",
                                                        "file_token": self._upload(img)},
                    "face_limit": int(p["target_polycount"]), "texture": True, "pbr": True}
        elif job.operation == "rig":
            body = {"type": "animate_rig", "original_model_task_id": p["model_task_id"], "out_format": "fbx"}
        elif job.operation == "retarget":
            body = {"type": "animate_retarget", "original_model_task_id": p["rig_task_id"],
                    "animation": p["animation"], "out_format": "fbx"}
        else:
            raise AdapterError(f"tripo does not support {job.operation}")
        if p.get("model_version"):
            body["model_version"] = p["model_version"]
        data = self._call("POST", self._url("/task"), json_body=body)
        tid = (data.get("data") or {}).get("task_id") if data.get("code") == 0 else None
        if not tid:
            raise AdapterError(f"tripo returned no task id: {str(data)[:200]}")
        return tid

    def poll(self, provider_job_id: str) -> JobStatus:
        data = self._call("GET", self._url(f"/task/{provider_job_id}"))
        d = data.get("data") or {}
        state = self.STATES.get(str(d.get("status")), "running")
        out = d.get("output") or {}
        outputs = {}
        for k, name in (("pbr_model", "model.glb"), ("model", "model.glb"), ("base_model", "base_model.glb"),
                        ("rendered_image", "preview.png")):
            if out.get(k) and name not in outputs:
                outputs[name] = out[k]
        if d.get("type") in ("animate_rig", "animate_retarget") and out.get("model"):
            outputs = {"rigged.fbx" if d.get("type") == "animate_rig" else "clip.fbx": out["model"]}
        credits = d.get("consumed_credit")
        return JobStatus(state, provider_job_id, float(d.get("progress") or 0),
                         "" if state != "failed" else str(d.get("status")), outputs,
                         cost_micros=self.pricing.cost(credits) if credits is not None else None,
                         usage={"credits": credits} if credits is not None else {})

    def fetch(self, status: JobStatus, out_dir: Path) -> dict[str, Path]:
        if not status.outputs:
            raise AdapterError("tripo job succeeded without outputs")
        return {name: self._download(url, out_dir / name) for name, url in status.outputs.items()}

    def adapter_checks(self) -> list[dict]:
        checks = super().adapter_checks()
        if checks[0]["ok"]:
            try:
                data = self._call("GET", self._url("/user/balance"))
                checks.append({"check": "authenticated balance call", "ok": data.get("code") == 0,
                               "detail": "" if data.get("code") == 0 else str(data)[:200]})
            except Exception as e:
                checks.append({"check": "authenticated balance call", "ok": False, "detail": str(e)[:200]})
        return checks


# --------------------------------------------------------------------------- DeepMotion


class DeepMotionAdapter(_ApiAdapter):
    """DeepMotion Animate 3D REST API (video -> FBX motion). Session auth with client id/secret.

    Flow: ``GET /account/v1/auth`` (Basic) -> session cookie; ``GET /upload`` -> signed URL; ``PUT`` the
    video; ``POST /job/v1/process``; ``GET /job/v1/status/{rid}``; ``GET /job/v1/download/{rid}``.
    The reference video is uploaded to DeepMotion (data class ``video``).
    """

    route_id = "deepmotion"
    base_url = "https://service.deepmotion.com"
    allowed_hosts = {"service.deepmotion.com", ".deepmotion.com", "storage.googleapis.com"}
    model = "deepmotion-animate3d"

    def __init__(self, key: KeyProvider | None, **kw):
        super().__init__(key, **kw)
        self._session: Optional[str] = None

    def _auth_headers(self) -> dict[str, str]:
        if self._session is None:
            cred = self.key()  # "client_id:client_secret"
            check_host(self._url("/account/v1/auth"), self.allowed_hosts)
            resp = self.transport.request("GET", self._url("/account/v1/auth"), headers={
                "Authorization": "Basic " + base64.b64encode(cred.encode()).decode()}, timeout=self.timeout_s)
            if resp.status >= 400:
                raise AdapterError(f"deepmotion authentication failed (HTTP {resp.status})")
            cookie = resp.headers.get("Set-Cookie", "") or resp.headers.get("set-cookie", "")
            sess = next((c.split("=", 1)[1] for c in cookie.split(";") if c.strip().startswith("dmsess=")), None)
            if not sess:
                raise AdapterError("deepmotion authentication returned no session")
            register_secret(sess)
            self._session = sess
        return {"cookie": f"dmsess={self._session}"}

    def submit(self, job: GenerationJob) -> str:
        if job.operation != "video_to_motion":
            raise AdapterError(f"deepmotion does not support {job.operation}")
        video = Path(job.inputs["video"])
        up = self._call("GET", self._url(f"/upload?name={video.name}&resumable=0"))
        signed = up.get("url")
        if not signed:
            raise AdapterError("deepmotion upload URL missing")
        check_host(signed, self.allowed_hosts)
        resp = self.transport.request("PUT", signed, data=video.read_bytes(),
                                      headers={"Content-Type": "application/octet-stream"}, timeout=300)
        if resp.status >= 400:
            raise AdapterError(f"deepmotion video upload failed (HTTP {resp.status})")
        params = ["config=configDefault", "formats=fbx", f"model={job.params.get('model_id', '')}"]
        if job.params.get("root_motion_in_place"):
            params.append("rootAtOrigin=1")
        data = self._call("POST", self._url("/job/v1/process"),
                          json_body={"url": signed.split("?")[0], "processor": "video2anim", "params": params})
        rid = data.get("rid")
        if not rid:
            raise AdapterError(f"deepmotion returned no job id: {str(data)[:200]}")
        return rid

    def poll(self, provider_job_id: str) -> JobStatus:
        data = self._call("GET", self._url(f"/job/v1/status/{provider_job_id}"))
        st = ((data.get("status") or [{}])[0]) if isinstance(data.get("status"), list) else {}
        raw = str(st.get("status", "PROGRESS")).upper()
        state = {"SUCCESS": "succeeded", "FAILURE": "failed", "PROGRESS": "running", "RETRY": "running"}.get(raw,
                                                                                                             "running")
        details = st.get("details") or {}
        outputs = {}
        if state == "succeeded":
            dl = self._call("GET", self._url(f"/job/v1/download/{provider_job_id}"))
            for link in dl.get("links") or []:
                for u in link.get("urls") or []:
                    for f in u.get("files") or []:
                        if f.get("fbx"):
                            outputs["clip.fbx"] = f["fbx"]
        return JobStatus(state, provider_job_id, float(details.get("step", 0) or 0) / max(float(
            details.get("total", 1) or 1), 1.0), str(details.get("exitReason", "")) if state == "failed" else "",
                         outputs, cost_micros=None)

    def fetch(self, status: JobStatus, out_dir: Path) -> dict[str, Path]:
        if not status.outputs:
            raise AdapterError("deepmotion job succeeded without an FBX")
        return {name: self._download(url, out_dir / name) for name, url in status.outputs.items()}


# --------------------------------------------------------------------------- configured image API


class ImageAPIAdapter(_ApiAdapter):
    """An owner-approved concept-image API at a fixed endpoint (configured, never chosen by generated code).

    Request ``{"prompt", "width", "height", "seed"}``; response ``{"images": [{"b64": ...} | {"url": ...}]}``.
    Synchronous services return the image at submit time; ``poll`` reports it as succeeded.
    """

    route_id = "api-image-service"

    def __init__(self, key: KeyProvider | None, *, endpoint: str, vendor: str, **kw):
        host = (endpoint.split("/")[2] if "//" in endpoint else endpoint).lower()
        self.allowed_hosts = {host}
        self.endpoint = endpoint
        self.vendor = vendor
        super().__init__(key, **kw)
        self._results: dict[str, dict] = {}

    def submit(self, job: GenerationJob) -> str:
        if job.operation != "concept_image":
            raise AdapterError(f"image API does not support {job.operation}")
        body = {"prompt": job.inputs["prompt"], "width": int(job.params.get("width", 1024)),
                "height": int(job.params.get("height", 1024))}
        if job.params.get("seed") is not None:
            body["seed"] = int(job.params["seed"])
        data = self._call("POST", self.endpoint, json_body=body)
        jid = f"img-{int(time.time() * 1000)}-{len(self._results)}"
        self._results[jid] = data
        return jid

    def poll(self, provider_job_id: str) -> JobStatus:
        data = self._results.get(provider_job_id)
        if data is None:
            return JobStatus("failed", provider_job_id, error="result not held by this process (synchronous API)")
        outputs = {}
        for i, img in enumerate(data.get("images") or []):
            outputs[f"concept_{i}.png"] = img.get("url") or ("b64:" + img.get("b64", ""))
        return JobStatus("succeeded" if outputs else "failed", provider_job_id, 1.0,
                         "" if outputs else "no images returned", outputs)

    def fetch(self, status: JobStatus, out_dir: Path) -> dict[str, Path]:
        files = {}
        for name, ref in status.outputs.items():
            dest = out_dir / name
            if ref.startswith("b64:"):
                dest.write_bytes(base64.b64decode(ref[4:]))
                files[name] = dest
            else:
                files[name] = self._download(ref, dest)
        return files
