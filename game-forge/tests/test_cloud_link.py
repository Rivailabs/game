"""Outbound cloud link: authenticated, project-scoped, short-lived, replay-proof job messages."""

from __future__ import annotations

import json

import pytest

from forge.sandbox.link import LinkKey, LinkRejected, LinkVerifier, sign
from forge.store import Store
from forge.util import FakeClock

COMMIT = "a" * 40
SHA = "b" * 64


@pytest.fixture
def link(tmp_path):
    clock = FakeClock(1_000.0)
    store = Store(tmp_path / "f.db", clock=clock)
    keys = {"k1": LinkKey("k1", "astra", b"0123456789abcdef0123456789abcdef", 5_000.0),
            "other": LinkKey("other", "elsewhere", b"x" * 32, 5_000.0)}
    return LinkVerifier(store, "astra", keys.get), keys, store, clock


def msg(**kw):
    base = dict(project_id="astra", workflow="verify_candidate", root_id="astra-t1", commit=COMMIT,
                artifacts={"apk": SHA}, lease="workspace:/w/1", issued_at=990.0, expires_at=1_300.0, nonce="n-1")
    base.update(kw)
    return base


def test_valid_message_is_accepted_once(link):
    v, keys, store, _ = link
    m = sign(msg(), keys["k1"])
    got = v.verify(json.dumps(m))
    assert got.workflow == "verify_candidate" and got.artifacts == {"apk": SHA}
    with pytest.raises(LinkRejected, match="replayed"):
        v.verify(m)
    # durable: a new verifier (after a restart) still rejects the replay
    v2 = LinkVerifier(store, "astra", keys.get)
    with pytest.raises(LinkRejected, match="replayed"):
        v2.verify(m)
    assert store.events(type_="cloud_link_accepted")


@pytest.mark.parametrize("change,reason", [
    ({"workflow": "sign_release"}, "workflow not permitted"),
    ({"workflow": "publish_store"}, "workflow not permitted"),
    ({"commit": "main"}, "full hash"),
    ({"artifacts": {"apk": "latest"}}, "sha256"),
    ({"expires_at": 999.0}, "validity window"),
    ({"issued_at": 1_100.0}, "validity window"),
    ({"expires_at": 2_000.0}, "validity window"),
])
def test_rejections(link, change, reason):
    v, keys, _, _ = link
    with pytest.raises(LinkRejected, match=reason):
        v.verify(sign(msg(**change), keys["k1"]))


def test_no_shell_field_bad_signature_wrong_project_and_expired_key(link):
    v, keys, _, clock = link
    with pytest.raises(LinkRejected, match="malformed"):
        v.verify(sign(dict(msg(), command="rm -rf /"), keys["k1"]))
    tampered = sign(msg(), keys["k1"])
    tampered["workflow"] = "integrate_candidate"
    with pytest.raises(LinkRejected, match="bad signature"):
        v.verify(tampered)
    with pytest.raises(LinkRejected, match="wrong project"):
        v.verify(sign(msg(project_id="elsewhere", nonce="n-2"), keys["other"]))
    with pytest.raises(LinkRejected, match="unknown key"):
        v.verify(sign(msg(nonce="n-3"), LinkKey("k9", "astra", b"y" * 32, 9e9)))
    clock.t = 6_000.0
    with pytest.raises(LinkRejected, match="expired key"):
        v.verify(sign(msg(nonce="n-4", issued_at=5_990.0, expires_at=6_100.0), keys["k1"]))


def test_signing_workflows_can_never_be_allowed(tmp_path):
    with pytest.raises(ValueError, match="never be accepted"):
        LinkVerifier(Store(tmp_path / "x.db"), "astra", lambda k: None,
                     workflows=frozenset({"verify_candidate", "sign_release"}))
