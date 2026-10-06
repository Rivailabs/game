# Paid offering (R5): implemented, disabled by default

R5's exit gate is: a real paying customer completes an allowed BYOK workflow; billing reconciles; limits hold
under concurrency; cross-tenant access is blocked; the delivered service meets its written support promises. A
real customer, real billing and staffed support do not exist here, so the service is built and tested but stays
off (`FORGE_HOSTED_ENABLED` unset). Managed credits have their own flag (`FORGE_MANAGED_CREDITS_ENABLED`) because
they are a later, separately metered service.

| Part | Module | What it guarantees |
|---|---|---|
| Tenants, seats, roles | `forge/hosted/tenancy.py` | Maker 1 seat, Studio 5; roles owner/admin/approver/member/viewer; every lookup is tenant-scoped and answers *not found* across tenants; removed members lose access at once; staff access only by time-limited, audited owner grant |
| Entitlements | `forge/hosted/entitlements.py`, `plans.py` | the plan's table: projects 3/10, storage 5/25 GB, review bundles 10/50 per billing month at most 250 MB each, retention 30/90 days, workflows 1/3. Checks are atomic, refusals keep the local result, show use and expiry, and never add a charge |
| Hosted review | `forge/hosted/review.py` | uploads only for projects whose policy allows hosted review; private per-tenant storage; HMAC links of at most 15 minutes, re-checked on use against tenant, expiry and current membership |
| BYOK | `forge/hosted/byok.py` | keys encrypted with Fernet under a server master key; write-only API (fingerprints only); a gateway with short-lived tenant/project/provider credentials, certified adapters only, no caller-chosen URLs, keys redacted from errors |
| Billing | `forge/hosted/billing.py` | Razorpay (`X-Razorpay-Signature`) and Paddle (`Paddle-Signature: ts;h1`, replay window) verification before parsing; idempotent events; append-only money ledger with the price's tax-inclusive flag; refunds through an injected transport; orders refused while tax inclusion is undeclared |
| Managed credits | `forge/hosted/credits.py` | the job-outcome table exactly (release / settle / restore / restore / charge stands / reconcile); atomic reservations including running ones against balance, daily and period limits; idempotency keys stop duplicate jobs; schedule units never mix |
| Monitoring | `forge/hosted/monitoring.py` | `/healthz`, `/readyz`, `/metrics` with aggregate numbers only |
| Support | `forge/hosted/support.py` | stated expectations from `support-boundaries.md`, written into each ticket |
| Unit economics | `forge/hosted/economics.py`, `forge economics` | customer contribution = revenue excl. tax - provider usage - payment fees - refunds - hosting - support; Razorpay 2% + GST, Paddle 5% + $0.50 as configurable published rates |
| Offering metrics | `forge/hosted/metrics.py`, `forge metrics` | accepted roots / attempted, cost per accepted root, human minutes, time to first playable build, first-pass visual acceptance, escaped defects, recovery success, external projects retained, with sample size and scope |

Test prices (₹999 / $19 Maker, ₹4,999 / $99 Studio) are recorded with `tax_inclusive = None` on purpose: the plan
says to state whether displayed prices include tax before accepting orders, so checkout refuses until the operator
declares it.

## Not verified here

No real tenant, payment, Razorpay or Paddle account, webhook delivery, provider key, object store, PostgreSQL,
public deployment or support staffing exists. SQLite stands in for PostgreSQL; a local directory stands in for
private object storage. Billing payload field names follow the providers' published webhook formats and need
checking against live sandbox events before enabling the service.
