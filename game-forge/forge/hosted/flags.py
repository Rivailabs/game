"""Feature flags for the paid offering.

R5's exit gate needs a real paying customer, real billing reconciliation and staffed support, none
of which exist here. The hosted service is therefore implemented but **disabled by default**:
every entry point calls :func:`require_hosted` and refuses unless the operator turned it on.
Managed model credits have their own, separate flag because the plan makes them a later,
separately metered service behind its own commercial, billing and cost gate.
"""

from __future__ import annotations

import os
from dataclasses import dataclass


class HostedDisabled(Exception):
    pass


def _truthy(v: str | None) -> bool:
    return (v or "").strip().lower() in ("1", "true", "yes", "on")


@dataclass(frozen=True)
class Flags:
    hosted: bool = False
    managed_credits: bool = False
    #: Studio's two-business-day expectation applies only once staffing is confirmed.
    studio_support_staffed: bool = False

    @classmethod
    def from_env(cls) -> "Flags":
        return cls(hosted=_truthy(os.environ.get("FORGE_HOSTED_ENABLED")),
                   managed_credits=_truthy(os.environ.get("FORGE_MANAGED_CREDITS_ENABLED")),
                   studio_support_staffed=_truthy(os.environ.get("FORGE_STUDIO_SUPPORT_STAFFED")))


def require_hosted(flags: Flags) -> None:
    if not flags.hosted:
        raise HostedDisabled("the hosted service is disabled (set FORGE_HOSTED_ENABLED=1 only after the R5 gate "
                             "conditions are met)")


def require_managed_credits(flags: Flags) -> None:
    require_hosted(flags)
    if not flags.managed_credits:
        raise HostedDisabled("managed credits are a later, separately metered service and are disabled")
