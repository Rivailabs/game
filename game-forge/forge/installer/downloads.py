"""Versioned, checksum-verified downloads with their terms.

Plan: "Dependencies and model downloads are versioned, checksum-verified and accompanied by their
applicable terms. Forge-owned code can use Apache-2.0; that label must not imply that Unity, every
model, third-party binary or generated output shares the same licence."

A downloads manifest lists each item with an exact version, sha256, size, licence and terms URL.
Items that need an account or a licence decision (Unity, Android SDK terms) are ``delivery =
"manual"``: Forge shows the step and never fetches them. An automatic item whose terms need
acceptance is refused until the owner records that acceptance; a checksum mismatch deletes the
partial file and fails.
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import tempfile
import time
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Optional, Protocol

DEFAULT_MANIFEST = Path(__file__).resolve().parent / "downloads.json"


class DownloadError(Exception):
    pass


class ChecksumMismatch(DownloadError):
    pass


class TermsNotAccepted(DownloadError):
    pass


@dataclass
class DownloadEntry:
    name: str
    version: str
    kind: str  # tool | model | package | template
    licence: str
    terms_url: str
    delivery: str = "automatic"  # automatic | manual
    url: str = ""
    sha256: str = ""
    size_bytes: Optional[int] = None
    requires_acceptance: bool = False
    account_required: bool = False
    note: str = ""

    def problems(self) -> list[str]:
        out = []
        if not self.version or self.version.lower() in ("latest", "*"):
            out.append(f"{self.name}: needs an exact version")
        if not self.licence or not self.terms_url:
            out.append(f"{self.name}: licence and terms URL are required")
        if self.delivery == "automatic":
            if not re.fullmatch(r"[0-9a-f]{64}", self.sha256 or ""):
                out.append(f"{self.name}: automatic downloads need a sha256")
            if not self.url.startswith("https://"):
                out.append(f"{self.name}: automatic downloads need an https URL")
        elif self.delivery != "manual":
            out.append(f"{self.name}: unknown delivery {self.delivery!r}")
        return out


@dataclass
class DownloadsManifest:
    manifest_version: str
    profile: str
    entries: list[DownloadEntry] = field(default_factory=list)

    @classmethod
    def load(cls, path: str | Path = DEFAULT_MANIFEST) -> "DownloadsManifest":
        d = json.loads(Path(path).read_text())
        m = cls(d["manifest_version"], d.get("profile", ""), [DownloadEntry(**e) for e in d.get("entries", [])])
        problems = [p for e in m.entries for p in e.problems()]
        if problems:
            raise DownloadError("invalid downloads manifest: " + "; ".join(problems))
        return m

    def manual_steps(self) -> list[DownloadEntry]:
        return [e for e in self.entries if e.delivery == "manual"]

    def automatic(self) -> list[DownloadEntry]:
        return [e for e in self.entries if e.delivery == "automatic"]


class Fetcher(Protocol):
    def fetch(self, url: str, dest: Path) -> None: ...


class TermsLedger:
    """Append-only record of the owner's terms acceptances (name, version, terms URL, who, when)."""

    def __init__(self, path: str | Path, clock=time.time):
        self.path, self.clock = Path(path), clock

    def accept(self, entry: DownloadEntry, *, by: str) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        with open(self.path, "a", encoding="utf-8") as fh:
            fh.write(json.dumps({"name": entry.name, "version": entry.version, "terms_url": entry.terms_url,
                                 "licence": entry.licence, "by": by, "at": self.clock()}, sort_keys=True) + "\n")

    def accepted(self, entry: DownloadEntry) -> bool:
        if not self.path.exists():
            return False
        for line in self.path.read_text().splitlines():
            r = json.loads(line)
            if r["name"] == entry.name and r["version"] == entry.version and r["terms_url"] == entry.terms_url:
                return True
        return False


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 16), b""):
            h.update(chunk)
    return h.hexdigest()


def fetch_verified(entry: DownloadEntry, dest_dir: str | Path, fetcher: Fetcher, *,
                   terms: Optional[TermsLedger] = None) -> Path:
    if entry.delivery != "automatic":
        raise DownloadError(f"{entry.name} is a manual step: {entry.note or entry.terms_url}")
    if entry.problems():
        raise DownloadError("; ".join(entry.problems()))
    if entry.requires_acceptance and not (terms and terms.accepted(entry)):
        raise TermsNotAccepted(f"{entry.name} {entry.version}: accept its terms first ({entry.terms_url})")
    dest_dir = Path(dest_dir)
    dest_dir.mkdir(parents=True, exist_ok=True)
    final = dest_dir / f"{entry.name}-{entry.version}{''.join(Path(entry.url).suffixes[-2:])}"
    if final.exists() and sha256_file(final) == entry.sha256:
        return final
    fd, tmp = tempfile.mkstemp(dir=dest_dir, prefix=".dl-")
    os.close(fd)
    try:
        fetcher.fetch(entry.url, Path(tmp))
        size = os.path.getsize(tmp)
        if entry.size_bytes is not None and size != entry.size_bytes:
            raise ChecksumMismatch(f"{entry.name}: size {size} != {entry.size_bytes}")
        actual = sha256_file(Path(tmp))
        if actual != entry.sha256:
            raise ChecksumMismatch(f"{entry.name}: sha256 {actual} != {entry.sha256}")
        os.replace(tmp, final)
    finally:
        if os.path.exists(tmp):
            os.unlink(tmp)
    return final


def describe(entries: list[DownloadEntry]) -> list[dict]:
    return [asdict(e) for e in entries]
