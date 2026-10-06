"""Ed25519 signing of approval records (release approvals, store authorisations, update manifests).

Approvals cross a process boundary: the owner's tool writes an approval file and the separate
signing service (or the store publisher, or the updater) acts on it. A file anyone can write is
not an approval, so each approval is signed with the approver's Ed25519 key and the receiving
process trusts only the public keys in its own configuration.

Keys are generated outside every repository with mode 0600. ``cryptography`` is required; without
it the functions raise :class:`CryptoUnavailable` and callers report BLOCKED rather than skipping
the signature.
"""

from __future__ import annotations

import base64
import json
import os
from pathlib import Path


class CryptoUnavailable(Exception):
    pass


class SignatureInvalid(Exception):
    pass


def _crypto():
    try:
        from cryptography.exceptions import InvalidSignature
        from cryptography.hazmat.primitives import serialization
        from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey, Ed25519PublicKey
    except ImportError as e:  # pragma: no cover - environment dependent
        raise CryptoUnavailable("the `cryptography` package is required for signed approvals") from e
    return Ed25519PrivateKey, Ed25519PublicKey, serialization, InvalidSignature


def canonical(obj: dict) -> bytes:
    return json.dumps(obj, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def generate_key(path: str | Path) -> str:
    """Write a new private key (PEM, 0600). Returns the public key as hex."""
    priv_cls, _, ser, _ = _crypto()
    path = Path(path).expanduser()
    if path.exists():
        raise FileExistsError(f"{path} exists; refusing to overwrite a key")
    key = priv_cls.generate()
    pem = key.private_bytes(ser.Encoding.PEM, ser.PrivateFormat.PKCS8, ser.NoEncryption())
    path.parent.mkdir(parents=True, exist_ok=True)
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "wb") as fh:
        fh.write(pem)
    return public_hex(path)


def _load_private(path: str | Path):
    _, _, ser, _ = _crypto()
    p = Path(path).expanduser()
    if os.name == "posix" and p.stat().st_mode & 0o077:
        raise PermissionError(f"{p} is readable by other users; chmod 600 it")
    return ser.load_pem_private_key(p.read_bytes(), password=None)


def public_hex(private_path: str | Path) -> str:
    _, _, ser, _ = _crypto()
    pub = _load_private(private_path).public_key()
    return pub.public_bytes(ser.Encoding.Raw, ser.PublicFormat.Raw).hex()


def sign_record(record: dict, private_path: str | Path) -> dict:
    """Return ``record`` plus ``signature`` (base64) and ``signer_public_key`` (hex)."""
    body = {k: v for k, v in record.items() if k not in ("signature", "signer_public_key")}
    key = _load_private(private_path)
    sig = key.sign(canonical(body))
    return {**body, "signature": base64.b64encode(sig).decode(), "signer_public_key": public_hex(private_path)}


def verify_record(record: dict, trusted_public_keys: list[str]) -> str:
    """Verify the signature with one of the trusted keys. Returns the key used; raises otherwise."""
    _, pub_cls, _, invalid = _crypto()
    sig_b64, pub = record.get("signature"), record.get("signer_public_key")
    if not sig_b64 or not pub:
        raise SignatureInvalid("record is not signed")
    if pub not in trusted_public_keys:
        raise SignatureInvalid("record is signed by a key this service does not trust")
    body = {k: v for k, v in record.items() if k not in ("signature", "signer_public_key")}
    try:
        pub_cls.from_public_bytes(bytes.fromhex(pub)).verify(base64.b64decode(sig_b64), canonical(body))
    except (invalid, ValueError) as e:
        raise SignatureInvalid("signature does not match the record") from e
    return pub
