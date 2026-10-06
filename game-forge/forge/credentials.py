"""Credential broker and log redaction.

API keys are read only here: from an environment variable or from a key file
that lives *outside* the repository. The broker authenticates provider calls
for a named project/provider and never hands keys to generated code: subprocess
environments built by Forge are scrubbed (``scrubbed_env``) and every log or
evidence text passes through ``redact`` before it is stored.
"""

from __future__ import annotations

import os
import re
import stat
from dataclasses import dataclass, field
from pathlib import Path

# Environment variables that must never reach generated-code subprocesses.
SECRET_ENV_PATTERNS = (
    re.compile(r".*API[_-]?KEY.*", re.I),
    re.compile(r".*TOKEN.*", re.I),
    re.compile(r".*SECRET.*", re.I),
    re.compile(r".*PASSWORD.*", re.I),
    re.compile(r".*CREDENTIAL.*", re.I),
    re.compile(r"^ANTHROPIC_.*"),
    re.compile(r"^FORGE_KEY.*"),
    re.compile(r"^AWS_.*"),
    re.compile(r"^GOOGLE_APPLICATION_CREDENTIALS$"),
    re.compile(r"^SSH_AUTH_SOCK$"),
)

_REDACTION_PATTERNS = (
    re.compile(r"sk-ant-[A-Za-z0-9_\-]{8,}"),
    re.compile(r"sk-[A-Za-z0-9]{20,}"),
    re.compile(r"(?i)(x-api-key|authorization)\s*[:=]\s*\S+"),
    re.compile(r"(?i)bearer\s+[A-Za-z0-9._\-]{12,}"),
    re.compile(r"ghp_[A-Za-z0-9]{20,}"),
)

_known_secrets: set[str] = set()


def register_secret(value: str) -> None:
    if value and len(value) >= 8:
        _known_secrets.add(value)


def redact(text: str) -> str:
    if not text:
        return text
    for secret in sorted(_known_secrets, key=len, reverse=True):
        text = text.replace(secret, "[REDACTED]")
    for pat in _REDACTION_PATTERNS:
        text = pat.sub("[REDACTED]", text)
    return text


def is_secret_env(name: str) -> bool:
    return any(p.match(name) for p in SECRET_ENV_PATTERNS)


def scrubbed_env(base: dict[str, str] | None = None, extra: dict[str, str] | None = None) -> dict[str, str]:
    """Environment for subprocesses that execute generated code or builds."""
    src = dict(os.environ if base is None else base)
    env = {k: v for k, v in src.items() if not is_secret_env(k)}
    env["FORGE_SANDBOX"] = "1"
    for k, v in (extra or {}).items():
        if is_secret_env(k):
            raise ValueError(f"refusing to pass secret-like variable {k} to a subprocess")
        env[k] = v
    return env


class CredentialError(Exception):
    pass


@dataclass
class ProviderCredentialSpec:
    provider: str
    env_var: str
    key_file: str | None = None


@dataclass
class CredentialBroker:
    """Resolves provider credentials for (project, provider) pairs.

    ``repo_roots`` lists directories the key file must NOT be inside, so a key
    can never be committed with the project or read by a worker's file tools.
    """

    specs: dict[str, ProviderCredentialSpec] = field(default_factory=dict)
    repo_roots: list[str] = field(default_factory=list)
    allowed: dict[str, set[str]] = field(default_factory=dict)  # project -> providers

    def register(self, spec: ProviderCredentialSpec) -> None:
        self.specs[spec.provider] = spec

    def allow(self, project_id: str, provider: str) -> None:
        self.allowed.setdefault(project_id, set()).add(provider)

    def has_credential(self, provider: str) -> bool:
        try:
            self._resolve(provider)
            return True
        except CredentialError:
            return False

    def get(self, project_id: str, provider: str) -> str:
        if provider not in self.allowed.get(project_id, set()):
            raise CredentialError(f"project {project_id} is not authorised to use provider {provider}")
        return self._resolve(provider)

    def _resolve(self, provider: str) -> str:
        spec = self.specs.get(provider)
        if spec is None:
            raise CredentialError(f"no credential spec registered for {provider}")
        value = os.environ.get(spec.env_var, "").strip()
        if not value and spec.key_file:
            p = Path(spec.key_file).expanduser().resolve()
            for root in self.repo_roots:
                r = Path(root).resolve()
                if p == r or r in p.parents:
                    raise CredentialError(
                        f"key file {p} is inside a project/repository directory; move it outside"
                    )
            if p.exists():
                mode = p.stat().st_mode
                if mode & (stat.S_IRWXG | stat.S_IRWXO):
                    raise CredentialError(f"key file {p} must not be group/world readable (chmod 600)")
                value = p.read_text().strip()
        if not value:
            raise CredentialError(
                f"credential for {provider} not found (set {spec.env_var} or a key file outside the repo)"
            )
        register_secret(value)
        return value
