"""User-owned official CLI connection (e.g. Claude Code) - SUPERVISED ROUTE ONLY.

Per the plan: the unmodified vendor tool authenticates directly with the end
user, who owns the subscription and its billing. Forge never collects, reads or
stores the CLI's tokens and never pools usage. Because Forge cannot prove that
generated tools are unable to read the CLI's credential material, this route is
supervised: it prepares a worktree and prints the command for the owner to run
themselves; it is refused in unattended mode and has no billable ceiling.
"""

from __future__ import annotations

import shlex
from typing import Optional

from .base import (
    CodingRequest,
    ProviderAdapter,
    ProviderDescriptor,
    ProviderResult,
    ReconcileResult,
    SupervisedOnly,
)


class OfficialCLIConnector(ProviderAdapter):
    def __init__(self, binary: str = "claude", vendor: str = "anthropic"):
        self.binary = binary
        self.descriptor = ProviderDescriptor(
            name=f"official-cli:{binary}", vendor=vendor, operations=["code"],
            auth_method="vendor's own sign-in flow inside the unmodified CLI; Forge never sees tokens",
            billing_party="the end user (their own subscription or API relationship)",
            data_destinations=[f"{vendor} services as configured by the user's CLI"],
            data_classes_sent=["prompt", "code", "test_excerpt"],
            cancellation="owner interrupts the CLI", usage_reporting="not reported to Forge",
            unattended_allowed=False, supervised_only=True,
            notes="Supervised route: not eligible for unattended dispatch or pooled usage.",
        )

    def estimate_ceiling_micros(self, req: CodingRequest) -> Optional[int]:
        return None  # unknown: billed to the user's subscription, not reserved by Forge

    def manual_command(self, req: CodingRequest) -> str:
        prompt = f"Implement Forge task {req.root.ticket or req.root.id}: {req.root.title}. " \
                 f"Only edit: {', '.join(req.root.permitted_paths)}."
        return f"cd {shlex.quote(str(req.workspace))} && {self.binary} {shlex.quote(prompt)}"

    def submit(self, req: CodingRequest) -> ProviderResult:
        raise SupervisedOnly(
            "official CLI connections are supervised: run this yourself, then use `forge run` to verify:\n  "
            + self.manual_command(req)
        )

    def reconcile(self, idempotency_key: str) -> ReconcileResult:
        return ReconcileResult("unknown", detail="supervised route; Forge does not track CLI jobs")
