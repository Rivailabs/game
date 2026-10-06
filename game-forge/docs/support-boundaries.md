# Support boundaries and response expectations

Plan: "Publish support boundaries and response expectations actually staffed by the business. Unresolved
arbitrary-game debugging is not hidden inside a promise of unlimited priority support."

## Inside the boundary

- The supported profile in `getting-started.md` (Linux x86-64 Ubuntu LTS, the pinned Unity 6 LTS patch, .NET 8,
  a physical Android phone).
- The `turn-duel-2p` template and games built inside it.
- Forge's own behaviour: setup, intake, planning, the task loop, review, lanes, packaging, export, updates.

## Outside the boundary

- Other operating systems, other engines, iOS, real-time or online networking beyond explicit modules.
- Debugging a game outside the supported template. This can be scoped as a separately priced guided service;
  nothing about it is promised by a subscription.
- Provider outages, provider terms or billing disputes: the provider bills you directly (BYOK).
- Unity licensing decisions and accounts.

## Response expectations (stated, not guaranteed)

| Offering | What you can expect |
|---|---|
| Open core | Community support. No response time. |
| Maker | Asynchronous setup/bug intake. **No guaranteed response SLA.** |
| Studio | A proposed two-business-day initial response for up to two supported-workflow incidents per month, **only once support staffing is confirmed**. Until then, and for a third incident in a month, tickets are asynchronous with no stated response time. |
| Enterprise | Only what has been scoped, implemented and verified for that customer. |

The ticket intake (`forge.hosted.support`) applies exactly these rules and writes the expectation into every
ticket. Security reports are triaged as received; revoke any exposed credential immediately.

## What support staff can see

Nothing by default. A tenant owner can grant time-limited (at most 72 hours), reasoned access; every staff read is
checked against that grant and written to the audit log. Diagnostic bundles are created and shared by you.
