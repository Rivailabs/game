"""Minimal HTTP transport for API adapters (stdlib only) with a per-adapter host allow-list.

The plan forbids turning a permitted request mechanism into a general credential-forwarding proxy:
every adapter names the exact hosts it may contact (API host + its download CDN) and the transport
refuses anything else, so a provider response cannot redirect Forge (and its key) elsewhere.
Credentials are passed per request by the adapter, registered for log redaction and never logged.
"""

from __future__ import annotations

import json
import secrets
import ssl
import urllib.error
import urllib.request
from dataclasses import dataclass, field
from typing import Any, Optional, Protocol
from urllib.parse import urlparse

from ...credentials import redact


class TransportRefused(Exception):
    pass


class HttpError(Exception):
    def __init__(self, status: int, body: str, url: str):
        super().__init__(f"HTTP {status} from {urlparse(url).netloc}: {redact(body)[:300]}")
        self.status, self.body = status, body


@dataclass
class HttpResponse:
    status: int
    body: bytes
    headers: dict[str, str] = field(default_factory=dict)

    def json(self) -> Any:
        return json.loads(self.body.decode("utf-8"))


class HttpTransport(Protocol):
    def request(self, method: str, url: str, *, headers: dict[str, str] | None = None, json_body: Any = None,
                data: bytes | None = None, timeout: float = 60) -> HttpResponse: ...


def check_host(url: str, allowed_hosts: set[str]) -> None:
    u = urlparse(url)
    if u.scheme != "https":
        raise TransportRefused(f"refusing non-HTTPS URL {u.scheme}://{u.netloc}")
    host = (u.hostname or "").lower()
    if not any(host == h or (h.startswith(".") and host.endswith(h)) for h in allowed_hosts):
        raise TransportRefused(f"host {host} is not in this adapter's allow-list")


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):  # pragma: no cover - network only
        raise TransportRefused(f"redirect to {urlparse(newurl).netloc} refused (credentials stay on the API host)")


class UrllibTransport:
    """Real transport (urllib, system CA bundle). Redirects are refused; hosts are allow-listed."""

    def __init__(self, allowed_hosts: set[str]):
        self.allowed_hosts = {h.lower() for h in allowed_hosts}
        self._opener = urllib.request.build_opener(_NoRedirect(), urllib.request.HTTPSHandler(
            context=ssl.create_default_context()))

    def request(self, method: str, url: str, *, headers: dict[str, str] | None = None, json_body: Any = None,
                data: bytes | None = None, timeout: float = 60) -> HttpResponse:  # pragma: no cover - network
        check_host(url, self.allowed_hosts)
        hdrs = dict(headers or {})
        if json_body is not None:
            data = json.dumps(json_body).encode()
            hdrs.setdefault("Content-Type", "application/json")
        req = urllib.request.Request(url, data=data, method=method, headers=hdrs)
        try:
            with self._opener.open(req, timeout=timeout) as resp:
                return HttpResponse(resp.status, resp.read(), dict(resp.headers))
        except urllib.error.HTTPError as e:
            raise HttpError(e.code, e.read().decode(errors="replace"), url) from None


def multipart(fields: dict[str, str], files: dict[str, tuple[str, bytes, str]]) -> tuple[bytes, str]:
    """Encode a multipart/form-data body. ``files``: name -> (filename, bytes, content type)."""
    boundary = "forge-" + secrets.token_hex(12)
    out = bytearray()
    for k, v in fields.items():
        out += f"--{boundary}\r\nContent-Disposition: form-data; name=\"{k}\"\r\n\r\n{v}\r\n".encode()
    for k, (fn, content, ctype) in files.items():
        out += (f"--{boundary}\r\nContent-Disposition: form-data; name=\"{k}\"; filename=\"{fn}\"\r\n"
                f"Content-Type: {ctype}\r\n\r\n").encode()
        out += content + b"\r\n"
    out += f"--{boundary}--\r\n".encode()
    return bytes(out), f"multipart/form-data; boundary={boundary}"


def download(transport: HttpTransport, url: str, dest, *, timeout: float = 300) -> None:
    resp = transport.request("GET", url, timeout=timeout)
    if resp.status != 200:
        raise HttpError(resp.status, resp.body[:200].decode(errors="replace"), url)
    dest.parent.mkdir(parents=True, exist_ok=True)
    dest.write_bytes(resp.body)


def ok_json(resp: HttpResponse, url: str) -> Any:
    if resp.status >= 400:
        raise HttpError(resp.status, resp.body.decode(errors="replace"), url)
    return resp.json()


def bearer(key: Optional[str]) -> dict[str, str]:
    return {"Authorization": f"Bearer {key}"} if key else {}
