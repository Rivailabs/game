"""Streaming authenticated encryption for backup archives.

Format (``FGBK`` version 1)::

    header  = b"FGBK" | version(1) | salt(16) | log2(N)(1) | r(1) | p(1) | nonce_prefix(7)
    chunk*  = length(4, big-endian, ciphertext bytes) | AES-256-GCM(ciphertext + 16-byte tag)

The key is derived from the owner's passphrase with scrypt. Each chunk's 12-byte nonce is
``nonce_prefix | counter(4) | last(1)`` (the STREAM construction): reordering, dropping or
truncating chunks fails authentication because the counter and the final-chunk flag are part of
the nonce, and the header is authenticated as associated data of every chunk.

The passphrase is never written anywhere by Forge; it comes from a file outside every repository
(``chmod 600``) or from ``FORGE_BACKUP_PASSPHRASE``.
"""

from __future__ import annotations

import io
import os
import stat
import struct
from pathlib import Path
from typing import BinaryIO, Iterable, Optional

MAGIC = b"FGBK"
VERSION = 1
CHUNK = 1 << 20  # 1 MiB plaintext per chunk
SCRYPT_LOG2_N, SCRYPT_R, SCRYPT_P = 15, 8, 1
HEADER_LEN = 4 + 1 + 16 + 3 + 7


class BackupCryptoError(Exception):
    pass


def _aead():
    try:
        from cryptography.hazmat.primitives.ciphers.aead import AESGCM
        from cryptography.hazmat.primitives.kdf.scrypt import Scrypt
    except ImportError as e:  # pragma: no cover - exercised only without the package
        raise BackupCryptoError("encrypted backups need the 'cryptography' package: "
                                "pip install 'game-forge[backup]'") from e
    return AESGCM, Scrypt


def derive_key(passphrase: bytes, salt: bytes, log2_n: int = SCRYPT_LOG2_N, r: int = SCRYPT_R, p: int = SCRYPT_P) -> bytes:
    if len(passphrase) < 12:
        raise BackupCryptoError("backup passphrase must be at least 12 characters")
    _, Scrypt = _aead()
    return Scrypt(salt=salt, length=32, n=1 << log2_n, r=r, p=p).derive(passphrase)


def _nonce(prefix: bytes, counter: int, last: bool) -> bytes:
    if counter >= 1 << 32:
        raise BackupCryptoError("archive too large for one stream")
    return prefix + struct.pack(">I", counter) + (b"\x01" if last else b"\x00")


class EncryptingWriter(io.RawIOBase):
    """File-like sink: ``write`` plaintext, ciphertext goes to ``out``. ``close`` writes the final chunk."""

    def __init__(self, out: BinaryIO, passphrase: bytes, *, log2_n: int = SCRYPT_LOG2_N):
        self.finished = True  # until fully constructed (close() from __del__ must not write)
        AESGCM, _ = _aead()
        salt, self.prefix = os.urandom(16), os.urandom(7)
        self.header = MAGIC + bytes([VERSION]) + salt + bytes([log2_n, SCRYPT_R, SCRYPT_P]) + self.prefix
        self.aead = AESGCM(derive_key(passphrase, salt, log2_n))
        self.out = out
        self.out.write(self.header)
        self.buf = bytearray()
        self.counter = 0
        self.finished = False

    def writable(self) -> bool:
        return True

    def write(self, data) -> int:
        if self.finished:
            raise ValueError("write to a finished archive")
        self.buf += data
        # keep at least one byte buffered so the true final chunk is always written by close()
        while len(self.buf) > CHUNK:
            self._emit(bytes(self.buf[:CHUNK]), last=False)
            del self.buf[:CHUNK]
        return len(data)

    def _emit(self, plain: bytes, *, last: bool) -> None:
        ct = self.aead.encrypt(_nonce(self.prefix, self.counter, last), plain, self.header)
        self.out.write(struct.pack(">I", len(ct)) + ct)
        self.counter += 1

    def close(self) -> None:
        if not self.finished:
            self._emit(bytes(self.buf), last=True)
            self.buf.clear()
            self.finished = True
            self.out.flush()
        super().close()


class DecryptingReader(io.RawIOBase):
    """File-like source over an encrypted archive. Raises :class:`BackupCryptoError` on any tampering."""

    def __init__(self, src: BinaryIO, passphrase: bytes):
        AESGCM, _ = _aead()
        header = src.read(HEADER_LEN)
        if len(header) != HEADER_LEN or header[:4] != MAGIC:
            raise BackupCryptoError("not a Forge backup archive")
        if header[4] != VERSION:
            raise BackupCryptoError(f"unsupported backup format version {header[4]}")
        salt = header[5:21]
        log2_n, r, p = header[21], header[22], header[23]
        if not (10 <= log2_n <= 22 and 1 <= r <= 32 and 1 <= p <= 16):
            raise BackupCryptoError("backup header has implausible key-derivation parameters")
        self.prefix = header[24:31]
        self.header = header
        self.aead = AESGCM(derive_key(passphrase, salt, log2_n, r, p))
        self.src = src
        self.counter = 0
        self.pending = b""
        self.done = False

    def readable(self) -> bool:
        return True

    def _next_chunk(self) -> bytes:
        from cryptography.exceptions import InvalidTag

        raw_len = self.src.read(4)
        if len(raw_len) != 4:
            raise BackupCryptoError("archive is truncated (final chunk missing)")
        (n,) = struct.unpack(">I", raw_len)
        if n < 16 or n > CHUNK + 16:
            raise BackupCryptoError("archive chunk has an invalid length")
        ct = self.src.read(n)
        if len(ct) != n:
            raise BackupCryptoError("archive is truncated")
        for last in (False, True):
            try:
                plain = self.aead.decrypt(_nonce(self.prefix, self.counter, last), ct, self.header)
            except InvalidTag:
                continue
            self.counter += 1
            if last:
                if self.src.read(1):
                    raise BackupCryptoError("unexpected data after the final chunk")
                self.done = True
            return plain
        raise BackupCryptoError("authentication failed: wrong passphrase or the archive was modified")

    def readinto(self, b) -> int:
        while not self.pending and not self.done:
            self.pending = self._next_chunk()
        if not self.pending:
            return 0
        n = min(len(b), len(self.pending))
        b[:n] = self.pending[:n]
        self.pending = self.pending[n:]
        return n


def decrypt_all(path: Path, passphrase: bytes) -> Iterable[bytes]:
    with open(path, "rb") as fh:
        r = DecryptingReader(fh, passphrase)
        while True:
            data = r.read(CHUNK)
            if not data:
                return
            yield data


def load_passphrase(passphrase_file: Optional[str], *, forbidden_roots: Iterable[Path] = ()) -> bytes:
    """Read the backup passphrase from a protected file outside every project, or the environment."""
    if passphrase_file:
        p = Path(passphrase_file).expanduser().resolve()
        for root in forbidden_roots:
            r = Path(root).resolve()
            if p == r or r in p.parents:
                raise BackupCryptoError(f"passphrase file {p} is inside {r}; keep it outside every project")
        if not p.exists():
            raise BackupCryptoError(f"passphrase file {p} does not exist")
        if p.stat().st_mode & (stat.S_IRWXG | stat.S_IRWXO):
            raise BackupCryptoError(f"passphrase file {p} must not be group/world readable (chmod 600)")
        value = p.read_text().strip()
    else:
        value = os.environ.get("FORGE_BACKUP_PASSPHRASE", "").strip()
    if not value:
        raise BackupCryptoError("no backup passphrase: pass --passphrase-file or set FORGE_BACKUP_PASSPHRASE")
    from ..credentials import register_secret

    register_secret(value)
    return value.encode("utf-8")
