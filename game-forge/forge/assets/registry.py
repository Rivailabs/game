"""Durable asset records in Forge's SQLite store (additive tables; the R1 schema is untouched).

* ``asset_licence_gates`` - the owner's first-use decision per route;
* ``asset_permissions`` - documented rights that lift a licence restriction;
* ``asset_certifications`` - adapter checks that passed (needed for unattended API routes);
* ``asset_workers`` / ``asset_benchmarks`` - GPU capability records and route benchmarks;
* ``asset_lanes`` - the per-asset stage state (see :mod:`forge.assets.lane`);
* ``asset_provenance`` - one provenance record per asset version and stage;
* ``asset_jobs`` - generation jobs with idempotency keys, provider job ids, status and cost.

Every write appends an event to the append-only log.
"""

from __future__ import annotations

import json
from dataclasses import asdict
from typing import Any, Optional

from ..store import Store
from ..util import new_id
from .contracts import ProvenanceRecord
from .gpu import WorkerRecord
from .routes import Benchmark, LicenceGate, PermissionRecord

SCHEMA = """
CREATE TABLE IF NOT EXISTS asset_licence_gates (route TEXT PRIMARY KEY, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS asset_permissions (id TEXT PRIMARY KEY, route TEXT NOT NULL, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS asset_certifications (route TEXT PRIMARY KEY, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS asset_workers (worker_id TEXT PRIMARY KEY, json TEXT NOT NULL, updated_at REAL NOT NULL);
CREATE TABLE IF NOT EXISTS asset_benchmarks (
    id TEXT PRIMARY KEY, route TEXT NOT NULL, stage TEXT NOT NULL, worker_id TEXT NOT NULL,
    gpu_model TEXT NOT NULL, json TEXT NOT NULL, at REAL NOT NULL);
CREATE TABLE IF NOT EXISTS asset_lanes (
    asset_id TEXT PRIMARY KEY, kind TEXT NOT NULL, json TEXT NOT NULL, updated_at REAL NOT NULL);
CREATE TABLE IF NOT EXISTS asset_provenance (
    asset_id TEXT NOT NULL, version INTEGER NOT NULL, stage TEXT NOT NULL, json TEXT NOT NULL,
    PRIMARY KEY(asset_id, version, stage));
CREATE TABLE IF NOT EXISTS asset_jobs (
    idempotency_key TEXT PRIMARY KEY, asset_id TEXT NOT NULL, root_id TEXT, route TEXT NOT NULL,
    stage TEXT NOT NULL, provider_job_id TEXT, status TEXT NOT NULL, reservation_id TEXT,
    cost_micros INTEGER NOT NULL DEFAULT 0, json TEXT NOT NULL, created_at REAL NOT NULL, updated_at REAL NOT NULL);
"""


class AssetRegistry:
    def __init__(self, store: Store):
        self.store = store
        store.conn.executescript(SCHEMA)

    # ------------------------------------------------------------ licence gates / permissions / certification
    def record_licence_gate(self, gate: LicenceGate) -> None:
        with self.store.tx() as c:
            c.execute("INSERT INTO asset_licence_gates(route,json) VALUES(?,?) ON CONFLICT(route) DO UPDATE SET "
                      "json=excluded.json", (gate.route, json.dumps(asdict(gate))))
            self.store.append_event("asset_licence_gate_recorded", route=gate.route, by=gate.accepted_by,
                                    terms=gate.terms_name, version=gate.terms_version,
                                    territory=gate.territory_decision, public_demo=gate.public_demo_allowed)

    def licence_gates(self) -> dict[str, LicenceGate]:
        return {r["route"]: LicenceGate(**json.loads(r["json"]))
                for r in self.store.conn.execute("SELECT * FROM asset_licence_gates")}

    def record_permission(self, perm: PermissionRecord) -> str:
        pid = new_id("perm")
        with self.store.tx() as c:
            c.execute("INSERT INTO asset_permissions(id,route,json) VALUES(?,?,?)",
                      (pid, perm.route, json.dumps(asdict(perm))))
            self.store.append_event("asset_permission_recorded", route=perm.route, scope=perm.scope,
                                    document=perm.document, by=perm.by)
        return pid

    def permissions(self) -> list[PermissionRecord]:
        return [PermissionRecord(**json.loads(r["json"])) for r in self.store.conn.execute(
            "SELECT json FROM asset_permissions ORDER BY id")]

    def record_certification(self, route: str, checks: list[dict], *, by: str, version: str) -> None:
        if not checks or not all(c.get("ok") for c in checks):
            raise ValueError("a route is certified only when every adapter check passed")
        with self.store.tx() as c:
            c.execute("INSERT INTO asset_certifications(route,json) VALUES(?,?) ON CONFLICT(route) DO UPDATE SET "
                      "json=excluded.json", (route, json.dumps({"checks": checks, "by": by, "version": version,
                                                                 "at": self.store.now()})))
            self.store.append_event("asset_route_certified", route=route, by=by, version=version)

    def certified(self) -> set[str]:
        return {r["route"] for r in self.store.conn.execute("SELECT route FROM asset_certifications")}

    # ------------------------------------------------------------ workers / benchmarks
    def put_worker(self, w: WorkerRecord) -> None:
        with self.store.tx() as c:
            c.execute("INSERT INTO asset_workers(worker_id,json,updated_at) VALUES(?,?,?) ON CONFLICT(worker_id) DO "
                      "UPDATE SET json=excluded.json, updated_at=excluded.updated_at",
                      (w.worker_id, json.dumps(w.to_dict()), self.store.now()))
            self.store.append_event("worker_capability_recorded", worker_id=w.worker_id,
                                    gpus=[f"{g.name} {g.memory_total_gb} GB" for g in w.gpus], ram_gb=w.ram_gb)

    def workers(self) -> list[WorkerRecord]:
        return [WorkerRecord.from_dict(json.loads(r["json"]))
                for r in self.store.conn.execute("SELECT json FROM asset_workers ORDER BY worker_id")]

    def record_benchmark(self, b: Benchmark) -> str:
        bid = new_id("bench")
        with self.store.tx() as c:
            c.execute("INSERT INTO asset_benchmarks(id,route,stage,worker_id,gpu_model,json,at) VALUES(?,?,?,?,?,?,?)",
                      (bid, b.route, b.stage, b.worker_id, b.gpu_model, json.dumps(asdict(b)), b.at or self.store.now()))
            self.store.append_event("route_benchmark_recorded", route=b.route, stage=b.stage, worker_id=b.worker_id,
                                    gpu_model=b.gpu_model, success=b.success, peak_vram_gb=b.peak_vram_gb)
        return bid

    def benchmarks(self, route: Optional[str] = None) -> list[Benchmark]:
        q, args = "SELECT json FROM asset_benchmarks", []
        if route:
            q += " WHERE route=?"
            args.append(route)
        return [Benchmark(**json.loads(r["json"])) for r in self.store.conn.execute(q + " ORDER BY at", args)]

    # ------------------------------------------------------------ lanes / provenance
    def save_lane(self, asset_id: str, kind: str, data: dict) -> None:
        with self.store.tx() as c:
            c.execute("INSERT INTO asset_lanes(asset_id,kind,json,updated_at) VALUES(?,?,?,?) ON CONFLICT(asset_id) "
                      "DO UPDATE SET json=excluded.json, updated_at=excluded.updated_at",
                      (asset_id, kind, json.dumps(data, sort_keys=True), self.store.now()))

    def load_lane(self, asset_id: str) -> Optional[dict]:
        r = self.store.conn.execute("SELECT json FROM asset_lanes WHERE asset_id=?", (asset_id,)).fetchone()
        return json.loads(r["json"]) if r else None

    def lanes(self) -> list[dict]:
        return [json.loads(r["json"]) for r in self.store.conn.execute("SELECT json FROM asset_lanes ORDER BY asset_id")]

    def put_provenance(self, rec: ProvenanceRecord) -> None:
        with self.store.tx() as c:
            c.execute("INSERT INTO asset_provenance(asset_id,version,stage,json) VALUES(?,?,?,?) ON CONFLICT(asset_id,version,stage) DO UPDATE "
                      "SET json=excluded.json", (rec.asset_id, rec.version, rec.stage, rec.model_dump_json()))
            self.store.append_event("asset_provenance_recorded", asset_id=rec.asset_id, version=rec.version,
                                    stage=rec.stage, route=rec.route, hashes=rec.final_hashes)

    def provenance(self, asset_id: Optional[str] = None) -> list[ProvenanceRecord]:
        q, args = "SELECT json FROM asset_provenance", []
        if asset_id:
            q += " WHERE asset_id=?"
            args.append(asset_id)
        return [ProvenanceRecord.model_validate_json(r["json"])
                for r in self.store.conn.execute(q + " ORDER BY asset_id, version, stage", args)]

    # ------------------------------------------------------------ generation jobs
    def job(self, key: str) -> Optional[dict]:
        r = self.store.conn.execute("SELECT * FROM asset_jobs WHERE idempotency_key=?", (key,)).fetchone()
        if not r:
            return None
        d = dict(r)
        d["data"] = json.loads(d.pop("json"))
        return d

    def record_job(self, key: str, *, asset_id: str, root_id: Optional[str], route: str, stage: str,
                   reservation_id: Optional[str], data: dict[str, Any] | None = None) -> dict:
        existing = self.job(key)
        if existing:
            return {**existing, "created": False}
        now = self.store.now()
        with self.store.tx() as c:
            c.execute("INSERT INTO asset_jobs(idempotency_key,asset_id,root_id,route,stage,status,reservation_id,json,"
                      "created_at,updated_at) VALUES(?,?,?,?,?,?,?,?,?,?)",
                      (key, asset_id, root_id, route, stage, "SUBMITTING", reservation_id, json.dumps(data or {}),
                       now, now))
            self.store.append_event("asset_job_submitting", root_id=root_id, idempotency_key=key, asset_id=asset_id,
                                    route=route, stage=stage)
        return {**self.job(key), "created": True}

    def update_job(self, key: str, **fields: Any) -> None:
        data = fields.pop("data", None)
        fields["updated_at"] = self.store.now()
        if data is not None:
            fields["json"] = json.dumps(data, default=str)
        cols = ",".join(f"{k}=?" for k in fields)
        with self.store.tx() as c:
            c.execute(f"UPDATE asset_jobs SET {cols} WHERE idempotency_key=?", (*fields.values(), key))
            row = c.execute("SELECT root_id FROM asset_jobs WHERE idempotency_key=?", (key,)).fetchone()
            self.store.append_event("asset_job_updated", root_id=row["root_id"] if row else None, idempotency_key=key,
                                    **{k: v for k, v in fields.items() if k in ("status", "provider_job_id",
                                                                                "cost_micros")})

    def jobs(self, asset_id: Optional[str] = None, root_id: Optional[str] = None) -> list[dict]:
        q, args = "SELECT * FROM asset_jobs WHERE 1=1", []
        if asset_id:
            q += " AND asset_id=?"
            args.append(asset_id)
        if root_id:
            q += " AND root_id=?"
            args.append(root_id)
        out = []
        for r in self.store.conn.execute(q + " ORDER BY created_at", args):
            d = dict(r)
            d["data"] = json.loads(d.pop("json"))
            out.append(d)
        return out
