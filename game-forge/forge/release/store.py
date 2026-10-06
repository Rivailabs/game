"""Store submission: a separately authorised action, never automatic.

Plan: "Store submission is a separate action with its own destination/track and approval; a normal
code merge cannot publish a game." and "The release authority chooses the store track and rollout;
a routine Forge task cannot publish by itself."

* :class:`SubmissionAuthorisation` names the exact signed artifact (sha256 from the signing
  archive), the package, version code, track, release status and rollout fraction. It is signed by
  a release-authority key that the publisher trusts. The orchestrator never creates or uses one.
* :class:`PublishingClient` is the interface; :class:`GooglePlayClient` implements the Google Play
  Developer API v3 *edits* flow (insert edit -> upload bundle/APK -> update track -> commit) over an
  injected HTTP transport and token provider, so no network or credential exists in tests.
* Any failure after the edit is created deletes the edit, so nothing half-configured is committed.

Rollout percentages are a release decision: the plan warns that ten percent of a tiny audience is
not an informative test, so no default fraction is chosen here.
"""

from __future__ import annotations

import hashlib
import json
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, Optional, Protocol

from .keys import CryptoUnavailable, SignatureInvalid, sign_record, verify_record

TRACKS = ("internal", "alpha", "beta", "production")
RELEASE_STATUSES = ("draft", "inProgress", "halted", "completed")
API = "https://androidpublisher.googleapis.com/androidpublisher/v3/applications"
UPLOAD_API = "https://androidpublisher.googleapis.com/upload/androidpublisher/v3/applications"


class SubmissionRefused(Exception):
    pass


class SubmissionBlocked(Exception):
    pass


@dataclass
class SubmissionAuthorisation:
    package_id: str
    version_code: int
    signed_sha256: str
    track: str
    release_status: str
    rollout_fraction: Optional[float]
    release_name: str = ""
    release_notes: dict[str, str] = field(default_factory=dict)  # language -> text

    def validate(self) -> None:
        if self.track not in TRACKS:
            raise SubmissionRefused(f"unknown track {self.track!r}")
        if self.release_status not in RELEASE_STATUSES:
            raise SubmissionRefused(f"unknown release status {self.release_status!r}")
        if self.release_status in ("inProgress", "halted"):
            if self.rollout_fraction is None or not (0 < self.rollout_fraction < 1):
                raise SubmissionRefused("a staged rollout needs a fraction strictly between 0 and 1")
        elif self.rollout_fraction is not None:
            raise SubmissionRefused(f"status {self.release_status} takes no rollout fraction")

    def body(self) -> dict:
        return {"kind": "store_submission", "package_id": self.package_id, "version_code": self.version_code,
                "signed_sha256": self.signed_sha256, "track": self.track, "release_status": self.release_status,
                "rollout_fraction": self.rollout_fraction, "release_name": self.release_name,
                "release_notes": self.release_notes}


def authorise(auth: SubmissionAuthorisation, *, by: str, key_path: str | Path, clock=time.time) -> dict:
    """The release authority signs a submission authorisation (a separate decision from signing)."""
    auth.validate()
    return sign_record({**auth.body(), "authorised_by": by, "authorised_at": clock()}, key_path)


class PublishingClient(Protocol):
    def create_edit(self, package_id: str) -> str: ...

    def upload(self, package_id: str, edit_id: str, path: Path) -> int: ...

    def update_track(self, package_id: str, edit_id: str, track: str, release: dict) -> dict: ...

    def commit(self, package_id: str, edit_id: str) -> dict: ...

    def delete_edit(self, package_id: str, edit_id: str) -> None: ...


Transport = Callable[[str, str, dict, Optional[bytes]], tuple[int, dict]]


class GooglePlayClient:
    """Google Play Developer API v3 edits flow over an injected transport.

    ``transport(method, url, headers, body) -> (status, json)``; ``token_provider()`` returns an OAuth
    access token for a service account with release permission. Neither is created here: the
    release authority supplies them when running a submission, outside every worker process.
    """

    def __init__(self, transport: Transport, token_provider: Callable[[], str]):
        self.transport = transport
        self.token = token_provider

    def _call(self, method: str, url: str, body: Optional[bytes] = None, content_type: str = "application/json") -> dict:
        status, data = self.transport(method, url, {"Authorization": f"Bearer {self.token()}",
                                                    "Content-Type": content_type}, body)
        if status >= 300:
            raise SubmissionRefused(f"Play API {method} {url.split('/applications/')[-1]} -> {status}: "
                                    f"{json.dumps(data)[:300]}")
        return data

    def create_edit(self, package_id: str) -> str:
        return self._call("POST", f"{API}/{package_id}/edits", b"{}")["id"]

    def upload(self, package_id: str, edit_id: str, path: Path) -> int:
        kind = "bundles" if path.suffix == ".aab" else "apks"
        data = self._call("POST", f"{UPLOAD_API}/{package_id}/edits/{edit_id}/{kind}?uploadType=media",
                          path.read_bytes(), "application/octet-stream")
        return int(data["versionCode"])

    def update_track(self, package_id: str, edit_id: str, track: str, release: dict) -> dict:
        body = json.dumps({"track": track, "releases": [release]}).encode()
        return self._call("PUT", f"{API}/{package_id}/edits/{edit_id}/tracks/{track}", body)

    def commit(self, package_id: str, edit_id: str) -> dict:
        return self._call("POST", f"{API}/{package_id}/edits/{edit_id}:commit")

    def delete_edit(self, package_id: str, edit_id: str) -> None:
        self._call("DELETE", f"{API}/{package_id}/edits/{edit_id}")


def submit(authorisation: dict, signing_record: dict, artifact: str | Path, client: PublishingClient, *,
           trusted_authorities: list[str], log_dir: str | Path, clock=time.time) -> dict:
    """Publish exactly the authorised signed artifact to the authorised track. Returns the record."""
    try:
        verify_record(authorisation, trusted_authorities)
    except CryptoUnavailable as e:
        raise SubmissionBlocked(str(e)) from e
    except SignatureInvalid as e:
        raise SubmissionRefused(f"authorisation rejected: {e}") from e
    if authorisation.get("kind") != "store_submission":
        raise SubmissionRefused("not a store submission authorisation")
    auth = SubmissionAuthorisation(**{k: authorisation[k] for k in (
        "package_id", "version_code", "signed_sha256", "track", "release_status", "rollout_fraction",
        "release_name", "release_notes")})
    auth.validate()
    if signing_record.get("signed_sha256") != auth.signed_sha256 or \
            signing_record.get("package_id") != auth.package_id or signing_record.get("version_code") != auth.version_code:
        raise SubmissionRefused("authorisation does not match the signing archive record")
    art = Path(artifact)
    actual = hashlib.sha256(art.read_bytes()).hexdigest()
    if actual != auth.signed_sha256:
        raise SubmissionRefused("artifact bytes are not the authorised signed artifact")
    edit = client.create_edit(auth.package_id)
    try:
        version = client.upload(auth.package_id, edit, art)
        if version != auth.version_code:
            raise SubmissionRefused(f"uploaded file has versionCode {version}, authorised {auth.version_code}")
        release: dict = {"versionCodes": [str(version)], "status": auth.release_status}
        if auth.rollout_fraction is not None:
            release["userFraction"] = auth.rollout_fraction
        if auth.release_name:
            release["name"] = auth.release_name
        if auth.release_notes:
            release["releaseNotes"] = [{"language": k, "text": v} for k, v in sorted(auth.release_notes.items())]
        client.update_track(auth.package_id, edit, auth.track, release)
        committed = client.commit(auth.package_id, edit)
    except Exception:
        try:
            client.delete_edit(auth.package_id, edit)
        except Exception:  # noqa: BLE001 - the original failure is what we report; an uncommitted edit expires
            pass
        raise
    record = {"package_id": auth.package_id, "version_code": auth.version_code, "track": auth.track,
              "release_status": auth.release_status, "rollout_fraction": auth.rollout_fraction, "edit_id": edit,
              "committed": committed, "signed_sha256": actual, "authorisation": authorisation,
              "submitted_at": clock()}
    out = Path(log_dir)
    out.mkdir(parents=True, exist_ok=True)
    (out / f"submission-{auth.package_id}-{auth.version_code}-{auth.track}.json").write_text(
        json.dumps(record, indent=2, sort_keys=True) + "\n")
    return record
