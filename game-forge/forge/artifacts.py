"""Content-addressed artifact directory (sha256).

Accepted artifacts are immutable: the bytes live at ``<root>/<aa>/<sha256>`` and
are written once (atomic rename); writing the same content again is a no-op.
"""

from __future__ import annotations

import hashlib
import os
import tempfile
from pathlib import Path

from .credentials import redact


class ArtifactStore:
    def __init__(self, root: str | os.PathLike):
        self.root = Path(root)
        self.root.mkdir(parents=True, exist_ok=True)

    def path_for(self, digest: str) -> Path:
        if len(digest) != 64 or any(c not in "0123456789abcdef" for c in digest):
            raise ValueError(f"not a sha256 digest: {digest!r}")
        return self.root / digest[:2] / digest

    def put_bytes(self, data: bytes) -> str:
        digest = hashlib.sha256(data).hexdigest()
        dest = self.path_for(digest)
        if dest.exists():
            return digest
        dest.parent.mkdir(parents=True, exist_ok=True)
        fd, tmp = tempfile.mkstemp(dir=dest.parent, prefix=".tmp-")
        try:
            with os.fdopen(fd, "wb") as fh:
                fh.write(data)
                fh.flush()
                os.fsync(fh.fileno())
            os.replace(tmp, dest)
            os.chmod(dest, 0o444)
        finally:
            if os.path.exists(tmp):
                os.unlink(tmp)
        return digest

    def put_text(self, text: str, *, redact_secrets: bool = True) -> str:
        if redact_secrets:
            text = redact(text)
        return self.put_bytes(text.encode("utf-8"))

    def put_file(self, path: str | os.PathLike) -> str:
        return self.put_bytes(Path(path).read_bytes())

    def get_bytes(self, digest: str) -> bytes:
        data = self.path_for(digest).read_bytes()
        if hashlib.sha256(data).hexdigest() != digest:
            raise IOError(f"artifact {digest} is corrupted")
        return data

    def get_text(self, digest: str) -> str:
        return self.get_bytes(digest).decode("utf-8", errors="replace")

    def exists(self, digest: str) -> bool:
        try:
            return self.path_for(digest).exists()
        except ValueError:
            return False
