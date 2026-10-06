"""R5 paid offering (disabled by default): tenancy, entitlements, hosted review, BYOK, billing,
managed credits, monitoring, support, unit economics and the offering metrics report."""

from __future__ import annotations

import hashlib
import hmac
import json
import sqlite3
import threading
from decimal import Decimal

import pytest

from forge.hosted.billing import BillingService, PaddleAdapter, RazorpayAdapter, SignatureError
from forge.hosted.byok import Gateway, GatewayDenied, KeyVault, new_master_key
from forge.hosted.credits import CreditsExhausted, CreditsError, CreditsLedger, CreditTerms, Limits, Outcome, ScheduleEntry
from forge.hosted.db import HostedDB
from forge.hosted.economics import (PADDLE_STANDARD, RAZORPAY_STANDARD, CustomerMonth, billings_scenario, cohort,
                                    contribution, format_inr, studio_vs_makers)
from forge.hosted.entitlements import EntitlementService, Refusal
from forge.hosted.flags import Flags, HostedDisabled
from forge.hosted.metrics import measure
from forge.hosted.monitoring import monitoring_app
from forge.hosted.plans import MAKER, MB, STUDIO, TEST_PRICES, Price, PriceNotSellable, sellable_price
from forge.hosted.review import HostedReview, LinkInvalid
from forge.hosted.support import SupportDesk
from forge.hosted.tenancy import Forbidden, NotFound, SeatLimit, TenantService

ON = Flags(hosted=True)


class Clock:
    def __init__(self, t=1_800_000_000.0):
        self.t = t

    def __call__(self):
        return self.t


@pytest.fixture
def clock():
    return Clock()


@pytest.fixture
def db(tmp_path, clock):
    return HostedDB(tmp_path / "hosted.db", clock=clock)


@pytest.fixture
def svc(db):
    tenants = TenantService(db, ON)
    return tenants, EntitlementService(db, tenants)


# ------------------------------------------------------------------ flag


def test_hosted_service_is_disabled_by_default(db, monkeypatch):
    monkeypatch.delenv("FORGE_HOSTED_ENABLED", raising=False)
    with pytest.raises(HostedDisabled):
        TenantService(db, Flags.from_env())
    with pytest.raises(HostedDisabled, match="managed credits"):
        CreditsLedger(db, TenantService(db, ON), ON, [], CreditTerms("t1", None, 100, "", "", "", ""))
    monkeypatch.setenv("FORGE_HOSTED_ENABLED", "1")
    assert Flags.from_env().hosted and not Flags.from_env().managed_credits


# ------------------------------------------------------------------ plans


def test_entitlement_table_matches_the_plan():
    assert (MAKER.seats, MAKER.active_projects, MAKER.storage_bytes, MAKER.upload_bundles_per_month,
            MAKER.max_bundle_bytes, MAKER.review_retention_days, MAKER.concurrent_workflows) == \
        (1, 3, 5 * 1024 ** 3, 10, 250 * MB, 30, 1)
    assert (STUDIO.seats, STUDIO.active_projects, STUDIO.storage_bytes, STUDIO.upload_bundles_per_month,
            STUDIO.max_bundle_bytes, STUDIO.review_retention_days, STUDIO.concurrent_workflows) == \
        (5, 10, 25 * 1024 ** 3, 50, 250 * MB, 90, 3)
    assert MAKER.managed_credits_included == STUDIO.managed_credits_included == 0
    assert "no guaranteed response SLA" in MAKER.support and "only after staffing is confirmed" in STUDIO.support


def test_orders_need_a_declared_tax_treatment():
    with pytest.raises(PriceNotSellable, match="includes applicable taxes"):
        sellable_price(TEST_PRICES, "maker", "IN")
    prices = {**TEST_PRICES, ("maker", "IN"): Price("maker", "IN", "INR", 99_900, True)}
    assert sellable_price(prices, "maker", "IN").amount_minor == 99_900


# ------------------------------------------------------------------ tenancy and isolation


def test_seats_roles_and_membership_revocation(svc):
    tenants, ent = svc
    maker = tenants.create_tenant("Solo", "maker", "solo@example.com")
    with pytest.raises(SeatLimit, match="1 seat"):
        tenants.add_member(maker, "friend@example.com", "member")
    studio = tenants.create_tenant("Team", "studio", "lead@example.com")
    members = [tenants.add_member(studio, f"m{i}@example.com", "member") for i in range(4)]
    with pytest.raises(SeatLimit):
        tenants.add_member(studio, "sixth@example.com", "viewer")
    with pytest.raises(Forbidden):
        tenants.add_member(members[0], "x@example.com", "member")  # members cannot manage members
    pid = ent.create_project(studio, "Rune Duel")
    assert tenants.project(members[0], pid)["name"] == "Rune Duel"
    tenants.remove_member(studio, members[0].user_id)
    with pytest.raises(NotFound):
        tenants.project(members[0], pid)  # a stale principal loses access immediately
    with pytest.raises(Forbidden):
        tenants.remove_member(studio, studio.user_id)


def test_cross_tenant_access_is_blocked_everywhere(tmp_path, db, svc, clock):
    tenants, ent = svc
    a = tenants.create_tenant("A", "studio", "a@example.com")
    b = tenants.create_tenant("B", "studio", "b@example.com")
    pa = ent.create_project(a, "A game", hosted_review_allowed=True)
    review = HostedReview(tenants, ent, tmp_path / "objects", b"s" * 32)
    f = tmp_path / "bundle.zip"
    f.write_bytes(b"A's build")
    obj = review.upload(a, pa, f)["object_id"]
    for attempt in (lambda: tenants.project(b, pa), lambda: review.upload(b, pa, f),
                    lambda: review.signed_url(b, obj), lambda: ent.delete_object(b, obj),
                    lambda: ent.start_workflow(b, pa), lambda: tenants.authorize(b, "view_review", a.tenant_id)):
        with pytest.raises(NotFound):
            attempt()
    # A link minted for A cannot be re-pointed at B by editing the query.
    url = review.signed_url(a, obj)
    with pytest.raises(LinkInvalid):
        review.open_url(url.replace(f"t={a.tenant_id}", f"t={b.tenant_id}"))
    vault = KeyVault(db, tenants, new_master_key())
    vault.store_key(a, "anthropic", "sk-ant-secret-key-of-a-123456")
    with pytest.raises(NotFound):
        vault.revoke(b, vault.list_keys(a)[0]["id"])
    gw = Gateway(db, tenants, vault, {"anthropic": lambda key, p: {"data": "ok", "usage": {}}})
    cred = gw.issue(a, pa, "anthropic", request_classes=["code"])
    with pytest.raises(GatewayDenied, match="not valid for this tenant"):
        gw.call(cred.token, tenant_id=b.tenant_id, project_id=pa, provider="anthropic", request_class="code", payload={})


# ------------------------------------------------------------------ entitlements


def test_project_storage_upload_and_size_limits(svc, clock):
    tenants, ent = svc
    maker = tenants.create_tenant("Solo", "maker", "solo@example.com")
    pids = [ent.create_project(maker, f"g{i}") for i in range(3)]
    with pytest.raises(Refusal, match="3 active hosted projects") as e:
        ent.create_project(maker, "g4")
    assert "export a project" in e.value.options
    with pytest.raises(Refusal, match="250 MB") as e:
        ent.reserve_upload(maker, pids[0], "huge.zip", 250 * MB + 1, "0" * 64)
    assert "nothing was charged" in str(e.value) and "Your local result is unchanged" in str(e.value)
    preview = ent.preview_upload(maker, 10 * MB)
    assert preview["allowed"] and preview["expires_at"] == clock.t + 30 * 86400
    for i in range(10):
        ent.reserve_upload(maker, pids[0], f"b{i}.zip", 1 * MB, "0" * 64)
    with pytest.raises(Refusal, match="10 review bundles"):
        ent.reserve_upload(maker, pids[0], "b11.zip", 1 * MB, "0" * 64)
    assert ent.usage(maker.tenant_id)["storage_bytes"] == 10 * MB


def test_storage_limit_counts_retained_bytes_and_renewal_resets_uploads(db, svc, clock):
    tenants, ent = svc
    maker = tenants.create_tenant("Solo", "maker", "solo@example.com")
    pid = ent.create_project(maker, "g")
    with db.tx() as c:
        c.execute("INSERT INTO subscriptions(tenant_id, provider, external_id, plan, status, period_start, period_end, "
                  "updated_at) VALUES(?,?,?,?,?,?,?,?)", (maker.tenant_id, "razorpay", "sub_1", "maker", "active",
                                                          clock.t - 10, clock.t + 30 * 86400, clock.t))
    for i in range(10):
        ent.reserve_upload(maker, pid, f"b{i}", 240 * MB, "0" * 64)
    with pytest.raises(Refusal):
        ent.reserve_upload(maker, pid, "b11", 1, "0" * 64)
    clock.t += 31 * 86400  # renewal: a new billing period
    with db.tx() as c:
        c.execute("UPDATE subscriptions SET period_start=?, period_end=? WHERE tenant_id=?",
                  (clock.t - 1, clock.t + 30 * 86400, maker.tenant_id))
    assert ent.usage(maker.tenant_id)["uploads_this_period"] == 0
    # The 30-day retention has expired, so the old bytes no longer count; fresh ones do.
    assert ent.usage(maker.tenant_id)["storage_bytes"] == 0
    for i in range(10):
        ent.reserve_upload(maker, pid, f"c{i}", 200 * MB, "0" * 64)
    with pytest.raises(Refusal):
        ent.reserve_upload(maker, pid, "c11", 1, "0" * 64)


def test_storage_limit_refuses_upload(svc):
    tenants, ent = svc
    maker = tenants.create_tenant("Solo", "maker", "solo@example.com")
    pid = ent.create_project(maker, "g")
    for i in range(9):
        ent.reserve_upload(maker, pid, f"b{i}", 250 * MB, "0" * 64)
    with pytest.raises(Refusal, match="250 MB"):
        ent.reserve_upload(maker, pid, "big", 3 * 1024 * MB, "0" * 64)
    preview = ent.preview_upload(maker, 250 * MB)
    assert preview["storage_after"] == 10 * 250 * MB and preview["allowed"]


def test_studio_pooled_storage_limit_counts_retained_bytes_across_periods(svc, clock):
    tenants, ent = svc
    studio = tenants.create_tenant("Team", "studio", "lead@example.com")
    pid = ent.create_project(studio, "g")
    for _ in range(2):  # two calendar months of 50 bundles; 90-day retention keeps all of them
        for i in range(50):
            ent.reserve_upload(studio, pid, f"b{i}", 250 * MB, "0" * 64)
        with pytest.raises(Refusal, match="50 review bundles"):
            ent.reserve_upload(studio, pid, "extra", 1, "0" * 64)
        clock.t += 31 * 86400
    assert ent.usage(studio.tenant_id)["storage_bytes"] == 100 * 250 * MB
    ent.reserve_upload(studio, pid, "c1", 250 * MB, "0" * 64)
    ent.reserve_upload(studio, pid, "c2", 250 * MB, "0" * 64)
    with pytest.raises(Refusal, match="storage limit") as e:
        ent.reserve_upload(studio, pid, "c3", 250 * MB, "0" * 64)
    assert e.value.usage["storage_limit"] == 25 * 1024 ** 3 and "prune expired or old bundles" in e.value.options


def test_concurrency_limit_holds_under_parallel_starts(tmp_path, clock):
    db = HostedDB(tmp_path / "h.db", clock=clock)
    tenants = TenantService(db, ON)
    ent = EntitlementService(db, tenants)
    studio = tenants.create_tenant("Team", "studio", "lead@example.com")
    pid = ent.create_project(studio, "g")
    ok, refused = [], []
    barrier = threading.Barrier(12)

    def go():
        barrier.wait()
        try:
            ok.append(ent.start_workflow(studio, pid))
        except Refusal:
            refused.append(1)

    threads = [threading.Thread(target=go) for _ in range(12)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    assert len(ok) == 3 and len(refused) == 9
    ent.finish_workflow(studio, ok[0])
    assert ent.start_workflow(studio, pid)


# ------------------------------------------------------------------ hosted review


def test_review_upload_needs_project_policy_and_links_are_short_lived(tmp_path, svc, clock):
    tenants, ent = svc
    studio = tenants.create_tenant("Team", "studio", "lead@example.com")
    viewer = tenants.add_member(studio, "v@example.com", "viewer")
    pid = ent.create_project(studio, "g")
    review = HostedReview(tenants, ent, tmp_path / "objects", b"k" * 32)
    f = tmp_path / "build.apk"
    f.write_bytes(b"apk-bytes")
    with pytest.raises(Forbidden, match="policy"):
        review.upload(studio, pid, f)
    with pytest.raises(Forbidden):
        review.upload(viewer, pid, f)
    ent.set_hosted_review(studio, pid, True)
    with pytest.raises(Forbidden):
        review.upload(viewer, pid, f)  # viewers cannot upload
    up = review.upload(studio, pid, f)
    assert up["sha256"] == hashlib.sha256(b"apk-bytes").hexdigest()
    url = review.signed_url(viewer, up["object_id"], ttl_s=120)
    assert review.open_url(url).read_bytes() == b"apk-bytes"
    with pytest.raises(ValueError):
        review.signed_url(viewer, up["object_id"], ttl_s=3600)
    with pytest.raises(LinkInvalid, match="bad signature"):
        review.open_url(url.replace("sig=", "sig=0"))
    clock.t += 121
    with pytest.raises(LinkInvalid, match="expired"):
        review.open_url(url)
    url2 = review.signed_url(viewer, up["object_id"])
    tenants.remove_member(studio, viewer.user_id)
    with pytest.raises(LinkInvalid, match="no longer"):
        review.open_url(url2)
    clock.t += 91 * 86400
    assert review.expire() == 1 and not (tmp_path / "objects" / studio.tenant_id / up["object_id"]).exists()


# ------------------------------------------------------------------ BYOK


def test_byok_keys_are_encrypted_write_only_and_used_only_by_the_gateway(db, svc):
    tenants, ent = svc
    owner = tenants.create_tenant("Team", "studio", "lead@example.com")
    member = tenants.add_member(owner, "m@example.com", "member")
    pid = ent.create_project(owner, "g")
    secret = "sk-ant-api03-REALLYSECRETVALUE-xyz9"
    vault = KeyVault(db, tenants, new_master_key())
    with pytest.raises(Forbidden):
        vault.store_key(member, "anthropic", secret)
    info = vault.store_key(owner, "anthropic", secret)
    assert info["fingerprint"] == "sk-...xyz9"
    assert not any(hasattr(vault, n) for n in ("get_key", "read_key", "export_key"))
    raw = sqlite3.connect(db.path).execute("SELECT ciphertext FROM byok_keys").fetchone()[0]
    assert secret.encode() not in raw
    dump = "\n".join(sqlite3.connect(db.path).iterdump())
    assert secret not in dump and "REALLYSECRETVALUE" not in dump
    seen = []

    def adapter(key, payload):
        seen.append(key)
        if payload.get("explode"):
            raise RuntimeError(f"upstream rejected key {key}")
        return {"data": {"text": "hello"}, "usage": {"input_tokens": 10}, "debug_key": key}

    gw = Gateway(db, tenants, vault, {"anthropic": adapter})
    cred = gw.issue(member, pid, "anthropic", request_classes=["code"], ttl_s=600)
    out = gw.call(cred.token, tenant_id=owner.tenant_id, project_id=pid, provider="anthropic", request_class="code",
                  payload={"prompt": "hi"})
    assert out == {"data": {"text": "hello"}, "usage": {"input_tokens": 10}} and seen == [secret]
    with pytest.raises(GatewayDenied, match="destinations"):
        gw.call(cred.token, tenant_id=owner.tenant_id, project_id=pid, provider="anthropic", request_class="code",
                payload={"base_url": "https://evil.example"})
    with pytest.raises(GatewayDenied, match="request class"):
        gw.call(cred.token, tenant_id=owner.tenant_id, project_id=pid, provider="anthropic", request_class="image",
                payload={})
    with pytest.raises(GatewayDenied) as e:
        gw.call(cred.token, tenant_id=owner.tenant_id, project_id=pid, provider="anthropic", request_class="code",
                payload={"explode": True})
    assert secret not in str(e.value)
    with pytest.raises(GatewayDenied, match="reservation"):
        gw.call(cred.token, tenant_id=owner.tenant_id, project_id=pid, provider="anthropic", request_class="code",
                payload={}, reservation_ok=lambda: False)
    vault.revoke(owner, info["key_id"])
    with pytest.raises(GatewayDenied, match="revoked"):
        gw.call(cred.token, tenant_id=owner.tenant_id, project_id=pid, provider="anthropic", request_class="code",
                payload={})
    with pytest.raises(GatewayDenied, match="no active"):
        gw.issue(owner, pid, "anthropic", request_classes=["code"])
    tokens = [r[0] for r in sqlite3.connect(db.path).execute("SELECT token_hash FROM gateway_credentials")]
    assert cred.token not in tokens  # only hashes are stored


# ------------------------------------------------------------------ billing


def _razorpay_body(event_id, etype, tenant_id, **entity):
    payload = {}
    if etype.startswith("subscription"):
        payload["subscription"] = {"entity": {"id": "sub_1", "plan_id": "plan_studio_in", "status": "active",
                                              "current_start": 1_800_000_000, "current_end": 1_802_592_000,
                                              "notes": {"tenant_id": tenant_id}, **entity}}
    elif etype.startswith("payment"):
        payload["payment"] = {"entity": {"id": "pay_1", "amount": 499_900, "currency": "INR",
                                         "notes": {"tenant_id": tenant_id}, **entity}}
    else:
        payload["refund"] = {"entity": {"id": "rfnd_1", "payment_id": "pay_1", "amount": 100_000, "currency": "INR",
                                        "notes": {"tenant_id": tenant_id}, **entity}}
    return json.dumps({"id": event_id, "event": etype, "payload": payload}).encode()


def _rz_headers(body, secret="whsec"):
    return {"X-Razorpay-Signature": hmac.new(secret.encode(), body, hashlib.sha256).hexdigest()}


def test_razorpay_webhooks_verify_apply_idempotently_and_refund(db, svc):
    tenants, ent = svc
    owner = tenants.create_tenant("Team", "maker", "lead@example.com", region="IN")
    calls = []

    def transport(method, url, headers, body):
        calls.append((method, url, body))
        return 200, {"id": "rfnd_1", "status": "processed"}

    prices = {**TEST_PRICES, ("studio", "IN"): Price("studio", "IN", "INR", 499_900, True)}
    billing = BillingService(db, tenants, {"razorpay": RazorpayAdapter("whsec", transport)},
                             {"plan_studio_in": "studio"}, prices)
    body = _razorpay_body("evt_1", "subscription.activated", owner.tenant_id)
    with pytest.raises(SignatureError):
        billing.handle_webhook("razorpay", {"X-Razorpay-Signature": "00"}, body)
    assert billing.handle_webhook("razorpay", _rz_headers(body), body)["status"] == "applied"
    assert billing.handle_webhook("razorpay", _rz_headers(body), body)["status"] == "duplicate"
    assert tenants.tenant(owner.tenant_id)["plan"] == "studio"  # Studio seats now apply
    pay = _razorpay_body("evt_2", "payment.captured", owner.tenant_id)
    billing.handle_webhook("razorpay", _rz_headers(pay), pay)
    money = billing.money(owner.tenant_id)
    assert money[0]["amount_minor"] == 499_900 and money[0]["tax_inclusive"] == 1
    with pytest.raises(Exception, match="exceeds"):
        billing.request_refund(owner, "razorpay", "pay_1", 600_000, "goodwill")
    res = billing.request_refund(owner, "razorpay", "pay_1", 100_000, "setup did not work")
    assert calls[0][1].endswith("/payments/pay_1/refund") and "webhook" in res["note"]
    rf = _razorpay_body("evt_3", "refund.processed", owner.tenant_id)
    billing.handle_webhook("razorpay", _rz_headers(rf), rf)
    assert [m["amount_minor"] for m in billing.money(owner.tenant_id)] == [499_900, -100_000]
    with pytest.raises(sqlite3.DatabaseError, match="append-only"):
        db.conn.execute("UPDATE money_ledger SET amount_minor=0")
    orphan = _razorpay_body("evt_4", "payment.captured", "ten_unknown")
    assert billing.handle_webhook("razorpay", _rz_headers(orphan), orphan)["status"] == "unassigned"


def test_paddle_signature_with_replay_window(db, svc, clock):
    tenants, _ = svc
    owner = tenants.create_tenant("Intl", "maker", "intl@example.com")
    adapter = PaddleAdapter("pdl_secret", clock=clock)
    billing = BillingService(db, tenants, {"paddle": adapter}, {"pri_studio": "studio"}, TEST_PRICES)
    body = json.dumps({"event_id": "evt_p1", "event_type": "subscription.activated", "data": {
        "id": "sub_p1", "status": "active", "custom_data": {"tenant_id": owner.tenant_id},
        "items": [{"price": {"id": "pri_studio"}}],
        "current_billing_period": {"starts_at": "2027-01-15T08:00:00Z", "ends_at": "2027-02-15T08:00:00Z"}}}).encode()
    ts = str(int(clock.t))
    h1 = hmac.new(b"pdl_secret", ts.encode() + b":" + body, hashlib.sha256).hexdigest()
    assert billing.handle_webhook("paddle", {"Paddle-Signature": f"ts={ts};h1={h1}"}, body)["status"] == "applied"
    assert tenants.tenant(owner.tenant_id)["plan"] == "studio"
    with pytest.raises(SignatureError, match="mismatch"):
        billing.handle_webhook("paddle", {"Paddle-Signature": f"ts={ts};h1={'0' * 64}"}, body)
    clock.t += 3600
    with pytest.raises(SignatureError, match="replay"):
        billing.handle_webhook("paddle", {"Paddle-Signature": f"ts={ts};h1={h1}"}, body)
    with pytest.raises(SignatureError):
        billing.handle_webhook("paddle", {}, body)


# ------------------------------------------------------------------ managed credits


SCHEDULE = [ScheduleEntry("img-v1", "forge-credit-v1", "imgco", "img-2", 10, "image"),
            ScheduleEntry("vid-v1", "vendorx-credit", "vendorx", "vid-1", 50, "clip")]
TERMS = CreditTerms("credits-2026-10", expiry_days=365, minimum_topup=100, refund_policy="unused credits refundable "
                    "within 14 days", cancellation="cancel any time", unused_balance="expires after 365 days",
                    export_access="ledger export always available")


@pytest.fixture
def credits(db):
    flags = Flags(hosted=True, managed_credits=True)
    tenants = TenantService(db, flags)
    owner = tenants.create_tenant("Team", "studio", "lead@example.com")
    led = CreditsLedger(db, tenants, flags, SCHEDULE, TERMS)
    led.top_up(owner, "forge-credit-v1", 1000, payment_ref="pay_1", terms_version=TERMS.version)
    return led, owner


def _job(led, owner, key, units=5):
    return led.reserve(owner, schedule_id="img-v1", job_units=units, params={"prompt": "bow"}, idempotency_key=key,
                       authorised_max_credits=500)


def test_credit_outcomes_follow_the_plan_table_exactly(credits):
    led, owner = credits
    unit = "forge-credit-v1"
    bal = lambda: led.balance(owner.tenant_id, unit)  # noqa: E731

    j = _job(led, owner, "k-not-submitted")  # holds 50
    assert bal() == {"available": 950, "reserved": 50, "charged": 0, "forge_cost": 0}
    led.resolve(owner, j["id"], Outcome.NOT_SUBMITTED)
    assert bal() == {"available": 1000, "reserved": 0, "charged": 0, "forge_cost": 0}

    j = _job(led, owner, "k-success")
    led.mark_submitted(owner, j["id"], "prov-1")
    r = led.resolve(owner, j["id"], Outcome.SUCCESS, actual_credits=30)
    assert r["job_charged"] == 30 and bal() == {"available": 970, "reserved": 0, "charged": 30, "forge_cost": 0}

    j = _job(led, owner, "k-platform")
    led.resolve(owner, j["id"], Outcome.PLATFORM_FAULT, upstream_cost=40)
    assert bal() == {"available": 970, "reserved": 0, "charged": 30, "forge_cost": 40}

    j = _job(led, owner, "k-provider")
    led.resolve(owner, j["id"], Outcome.PROVIDER_FAILURE, upstream_cost=5)
    assert bal() == {"available": 970, "reserved": 0, "charged": 30, "forge_cost": 45}

    j = _job(led, owner, "k-taste")
    r = led.resolve(owner, j["id"], Outcome.TASTE_REJECTION, actual_credits=50)
    assert r["job_charged"] == 50 and bal()["charged"] == 80 and bal()["available"] == 920

    j = _job(led, owner, "k-unknown")
    led.mark_submitted(owner, j["id"], "prov-9")
    assert led.resolve(owner, j["id"], Outcome.UNKNOWN)["status"] == "UNKNOWN"
    assert bal()["reserved"] == 50  # held while unknown
    again = _job(led, owner, "k-unknown")  # a retry reuses the job: no second reservation
    assert again["id"] == j["id"] and bal()["reserved"] == 50
    assert led.reconcile(owner, j["id"], lambda pid: {"status": "running"})["status"] == "UNKNOWN"
    r = led.reconcile(owner, j["id"], lambda pid: {"status": "completed", "credits": 20})
    assert r["status"] == "SETTLED" and bal() == {"available": 900, "reserved": 0, "charged": 100, "forge_cost": 45}
    with pytest.raises(CreditsError, match="closed"):
        led.resolve(owner, j["id"], Outcome.SUCCESS, actual_credits=1)


def test_credit_overrun_is_forge_cost_and_limits_include_reservations(credits):
    led, owner = credits
    j = _job(led, owner, "k1")  # 50 held
    led.resolve(owner, j["id"], Outcome.SUCCESS, actual_credits=70)
    b = led.balance(owner.tenant_id, "forge-credit-v1")
    assert b["charged"] == 50 and b["forge_cost"] == 20  # never more than the authorised maximum
    led.set_limits(owner, Limits(daily_limit=120))
    _job(led, owner, "k2")  # charged today 50 + reserved 0 + 50 = 100 <= 120
    with pytest.raises(CreditsExhausted, match="daily limit") as e:
        _job(led, owner, "k3")  # 50 + 50 reserved + 50 > 120
    assert "top up" in e.value.evidence["options"]
    with pytest.raises(CreditsError, match="authorised"):
        led.reserve(owner, schedule_id="img-v1", job_units=100, params={}, idempotency_key="k4",
                    authorised_max_credits=500)


def test_credit_units_are_not_interchangeable_and_terms_are_required(credits):
    led, owner = credits
    with pytest.raises(CreditsExhausted):  # forge credits cannot pay for a vendor-x schedule job
        led.reserve(owner, schedule_id="vid-v1", job_units=1, params={}, idempotency_key="v1",
                    authorised_max_credits=100)
    with pytest.raises(CreditsError, match="terms"):
        led.top_up(owner, "forge-credit-v1", 500, payment_ref="p", terms_version="old")
    with pytest.raises(CreditsError, match="minimum"):
        led.top_up(owner, "forge-credit-v1", 10, payment_ref="p", terms_version=TERMS.version)
    with pytest.raises(sqlite3.DatabaseError, match="append-only"):
        led.db.conn.execute("DELETE FROM credit_ledger")
    assert set(led.obligations(owner.tenant_id)) == {"forge-credit-v1"}


def test_credit_reservations_hold_under_concurrency(tmp_path, clock):
    db = HostedDB(tmp_path / "c.db", clock=clock)
    flags = Flags(hosted=True, managed_credits=True)
    tenants = TenantService(db, flags)
    owner = tenants.create_tenant("T", "studio", "o@example.com")
    led = CreditsLedger(db, tenants, flags, SCHEDULE, TERMS)
    led.top_up(owner, "forge-credit-v1", 200, payment_ref="p", terms_version=TERMS.version)
    ok, refused = [], []
    barrier = threading.Barrier(10)

    def go(i):
        barrier.wait()
        try:
            ok.append(_job(led, owner, f"c{i}"))
        except CreditsExhausted:
            refused.append(i)

    ts = [threading.Thread(target=go, args=(i,)) for i in range(10)]
    for t in ts:
        t.start()
    for t in ts:
        t.join()
    assert len(ok) == 4 and len(refused) == 6  # 4 x 50 = 200: never overspent
    assert led.balance(owner.tenant_id, "forge-credit-v1")["available"] == 0


# ------------------------------------------------------------------ monitoring and support


def test_monitoring_endpoints(db, svc):
    tenants, _ = svc
    tenants.create_tenant("A", "maker", "a@example.com")
    app = monitoring_app(db, ON)
    seen = {}

    def call(path):
        body = b"".join(app({"PATH_INFO": path}, lambda s, h: seen.__setitem__(path, s)))
        return seen[path], body.decode()

    assert call("/healthz")[0] == "200 OK" and call("/readyz")[0] == "200 OK"
    status, text = call("/metrics")
    assert "forge_tenants_total 1" in text and "a@example.com" not in text
    assert call("/nope")[0].startswith("404")
    assert monitoring_app(db, Flags())({"PATH_INFO": "/readyz"}, lambda s, h: seen.__setitem__("off", s)) and \
        seen["off"].startswith("503")


def test_support_states_expectations_without_guarantees(db, svc):
    tenants, _ = svc
    maker = tenants.create_tenant("Solo", "maker", "solo@example.com")
    studio = tenants.create_tenant("Team", "studio", "lead@example.com")
    unstaffed = SupportDesk(db, tenants, ON)
    t = unstaffed.open_ticket(maker, category="setup", severity="normal", summary="adb",
                              description="phone not found; my key is sk-ant-abcdefghijklmnopqrst")
    assert "no guaranteed response" in t["expectation"].lower()
    stored = unstaffed.tickets(maker)[0]["description"]
    assert "sk-ant-" not in stored and "[REDACTED]" in stored
    assert "not active yet" in unstaffed.open_ticket(studio, category="supported_workflow_bug", severity="high",
                                                     summary="x", description="y")["expectation"]
    staffed = SupportDesk(db, tenants, Flags(hosted=True, studio_support_staffed=True))
    r1 = staffed.open_ticket(studio, category="supported_workflow_bug", severity="high", summary="a", description="b")
    r2 = staffed.open_ticket(studio, category="supported_workflow_bug", severity="high", summary="a", description="b")
    r3 = staffed.open_ticket(studio, category="supported_workflow_bug", severity="high", summary="a", description="b")
    assert "incident 1 of 2" in r1["expectation"] and "not a guaranteed SLA" in r1["expectation"]
    assert "incident 2 of 2" in r2["expectation"] and "are used" in r3["expectation"]
    out = staffed.open_ticket(studio, category="arbitrary_game_debugging", severity="normal", summary="a", description="b")
    assert "outside the support boundary" in out["expectation"] and not out["counts_against_allowance"]


# ------------------------------------------------------------------ economics


def test_published_fee_models():
    assert RAZORPAY_STANDARD.fee(99_900) == 2_358  # 2% = 1998 paise, + 18% GST = 2357.64 -> 2358
    assert PADDLE_STANDARD.fee(1_900) == 145  # 5% of $19 = 95c + 50c


def test_contribution_equation():
    m = CustomerMonth(price_minor=99_900, tax_inclusive=True, provider_usage_minor=0, refunds_chargebacks_minor=0,
                      hosting_storage_minor=5_000, support_minor=10_000)
    c = contribution(m)
    assert c.revenue_ex_tax_minor == 84_661 and c.tax_minor == 15_239  # 999 incl. 18% GST
    assert c.payment_fees_minor == RAZORPAY_STANDARD.fee(99_900)
    assert c.contribution_minor == 84_661 - 2_358 - 5_000 - 10_000
    excl = contribution(CustomerMonth(price_minor=1_900, tax_inclusive=False, tax_rate_percent=Decimal("0"),
                                      fee_model=PADDLE_STANDARD, support_minor=3_000))
    assert excl.contribution_minor == 1_900 - 145 - 3_000 and "negative contribution" in " ".join(excl.notes)
    assert cohort(m, 50).contribution_minor == 50 * c.contribution_minor


def test_plan_arithmetic_scenarios():
    inr = billings_scenario(99_900, 50)
    assert inr["monthly_gross_minor"] == 4_995_000 and format_inr(inr["monthly_gross_minor"]) == "₹49,950"
    assert format_inr(inr["annualized_run_rate_minor"]) == "₹5,99,400"
    usd = billings_scenario(1_900, 50)
    assert usd["monthly_gross_minor"] == 95_000 and usd["annualized_run_rate_minor"] == 1_140_000
    s = studio_vs_makers(99_900, 499_900)
    assert s["five_makers_minor"] == 499_500 and s["studio_minus_makers_minor"] == 400
    assert s["studio_per_seat_minor"] == Decimal("99980")  # ₹999.80 per seat


# ------------------------------------------------------------------ offering metrics


def test_offering_metrics_report(tmp_path):
    from forge.budget import BudgetLedger, milestone_scope, project_scope, root_scope
    from forge.models import (CandidateAttempt, Decision, Evidence, EvidenceClass, EvidenceStatus, GateRecord, Project,
                              RootTask, TaskState)
    from forge.store import Store
    from forge.util import FakeClock

    clock = FakeClock(1_000_000.0)
    store = Store(tmp_path / "f.db", clock=clock)
    store.put_project(Project(id="p", name="p", repo_path=".", created_at=1_000_000.0))
    ledger = BudgetLedger(store)
    for s in (project_scope("p"), milestone_scope("m")):
        ledger.set_cap(s, 100_000_000)
    specs = [("r1", TaskState.ACCEPTED, True, 1), ("r2", TaskState.ACCEPTED, True, 2), ("r3", TaskState.FAILED, False, 1),
             ("r4", TaskState.DRAFT, False, 0)]
    for rid, state, visual, attempts in specs:
        store.create_root(RootTask(id=rid, project_id="p", milestone_id="m", title=rid, state=state,
                                   visual_review_required=visual))
        ledger.set_cap(root_scope(rid), 10_000_000)
        for n in range(1, attempts + 1):
            a = store.create_attempt(CandidateAttempt(id=f"{rid}-a{n}", root_id=rid, number=n))
            res = ledger.reserve(project_id="p", milestone_id="m", root_id=rid, amount_micros=1_000_000)
            ledger.settle(res, 500_000)
            if visual:
                rec = store.get_or_create_approval(rid, a.id, f"hash-{rid}-{n}")
                approve = (rid == "r1") or n == 2
                rec.visual_approval = GateRecord(decision=Decision.APPROVED if approve else Decision.REJECTED,
                                                 at=clock.now() + n)
                store.save_approval(rec)
    clock.advance(7200)
    store.add_evidence(Evidence(id="e1", root_id="r1", evidence_class=EvidenceClass.DEVICE, status=EvidenceStatus.PASS,
                                name="device", created_at=clock.now()))
    store.append_event("recovery_check", root_id="r3")
    store.append_event("recovery_check", root_id="r1")
    rep = measure(store, "p", scope="rune-duel milestone M1 (test data)", human_minutes={"r1": 30, "r2": 50},
                  manual_fix_roots={"r2"}, escaped_defects=[{"root_id": "r1", "description": "off-by-one"}])
    assert rep.sample_size == 3
    assert rep.get("accepted_roots_per_attempted").value == pytest.approx(2 / 3)
    assert rep.get("accepted_without_manual_fix").value == pytest.approx(1 / 3)
    assert rep.get("cost_per_accepted_root").value == 4 * 500_000 / 2  # failed r3's spend is included
    assert rep.get("human_minutes_per_root").value == 40
    assert rep.get("time_to_first_playable_build").value == pytest.approx(2.0)
    assert rep.get("first_pass_visual_acceptance").value == 0.5
    assert rep.get("escaped_defects").value == 1
    assert rep.get("recovery_success").value == 0.5
    assert rep.get("external_projects_retained").value is None
    md = rep.markdown()
    assert "hypothesis" in md and "no data" in md and "Sample: 3" in md
