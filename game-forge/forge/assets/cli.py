"""``forge assets ...``: routes, licence gates, permissions, GPU workers, benchmarks, certification,
lane status, route switching, retarget mappings and the licence inventory."""

from __future__ import annotations

import json
import sys
from pathlib import Path

from ..config import load_manifest, load_project_config
from ..store import Store
from .brief import load_asset_brief
from .gpu import capability_preflight
from .lane import AssetLane
from .provenance import find_credits, inventory_csv, inventory_rows
from .registry import AssetRegistry
from .routes import ROUTES, AssetLaneKind, Benchmark, LicenceGate, PermissionRecord, RouteContext, evaluate_route
from .skeleton import load_mapping, mapping_sha256, validate_mapping


def _ctx(args):
    cfg = load_project_config(args.project_file)
    store = Store(cfg.data_dir / "forge.db")
    return cfg, store, AssetRegistry(store)


def _route_context(cfg, reg: AssetRegistry, public_demo: bool) -> RouteContext:
    pol = cfg.project.policy
    return RouteContext(distribution_countries=list(pol.intended_release_countries), public_demo=public_demo,
                        gpus=[s for w in reg.workers() for s in w.slots()], permissions=reg.permissions(),
                        licence_gates=reg.licence_gates(), benchmarks=reg.benchmarks(), certified=reg.certified(),
                        configured=set((cfg.assets.get("routes") or {}).keys()), unattended=pol.unattended_mode,
                        allowed_vendors=list(pol.allowed_vendors), allowed_data_classes=list(pol.allowed_data_classes),
                        allowed_regions=list(pol.allowed_regions))


def cmd_assets(args) -> None:
    c = args.assets_cmd
    if c == "routes":
        if args.json:
            print(json.dumps([r.to_dict() for r in ROUTES.values()], indent=2))
            return
        for r in ROUTES.values():
            vram = ", ".join(f"{s.stage} {s.vram_gb if s.vram_gb is not None else '?'} GB" for s in r.stages) or "-"
            excl = ",".join(r.licence.excluded_territories) or "-"
            flags = " ".join(f for f, on in (("MANUAL", r.manual_step), ("BENCHMARK", r.benchmark_required),
                                             ("CERTIFY", r.requires_certification),
                                             ("EXCLUDED", r.licence.excluded_entirely)) if on)
            print(f"{r.id:24} {'/'.join(x.value for x in r.lanes):18} {r.kind:12} VRAM {vram:40} excl {excl:9} {flags}")
        return
    if c == "validate-brief":
        try:
            brief, sha = load_asset_brief(Path(args.brief))
        except (ValueError, OSError) as e:
            sys.exit(f"invalid: {e}")
        print(f"ok: {brief.id} ({brief.kind}) sha256 {sha}")
        return
    if c == "retarget-check":
        m = load_mapping(args.mapping)
        problems = validate_mapping(m)
        print(json.dumps({"sha256": mapping_sha256(m), "problems": problems}, indent=2))
        if problems:
            sys.exit(1)
        return
    cfg, store, reg = _ctx(args)
    if c == "select":
        kind = AssetLaneKind(args.lane)
        ctx = _route_context(cfg, reg, args.public_demo)
        ids = args.routes.split(",") if args.routes else [r for r, d in ROUTES.items() if kind in d.lanes]
        for rid in ids:
            d = evaluate_route(rid, kind, ctx, stage=args.stage)
            print(f"{rid:24} {'ALLOWED' if d.allowed else 'refused':8} {d.qualification:28} "
                  f"{'; '.join(d.reasons)}")
    elif c == "licence-gate":
        if args.route not in ROUTES:
            sys.exit(f"unknown route {args.route}")
        reg.record_licence_gate(LicenceGate(args.route, args.terms_name, args.terms_version, args.output_rights,
                                            args.territory, args.public_demo, args.by, store.now(),
                                            args.checkpoint or ""))
        print(f"first-use licence gate recorded for {args.route}")
    elif c == "permission":
        reg.record_permission(PermissionRecord(args.route, args.scope, args.document, args.dependencies_cleared,
                                               args.by, store.now()))
        print(f"permission recorded for {args.route} ({args.scope})")
    elif c == "worker-preflight":
        rec = capability_preflight(args.worker_id, now=store.now(), disk_path=cfg.data_dir if cfg.data_dir.exists()
                                   else ".", remote_provider=args.remote_provider or "")
        reg.put_worker(rec)
        print(json.dumps(rec.to_dict(), indent=2))
        if not rec.gpus:
            print("no GPU recorded: local model routes stay unavailable on this worker", file=sys.stderr)
    elif c == "benchmark":
        w = next((x for x in reg.workers() if x.worker_id == args.worker), None)
        if w is None:
            sys.exit(f"unknown worker {args.worker}: run `forge assets worker-preflight` there first")
        g = next((x for x in w.gpus if x.uuid == args.gpu_uuid or str(x.index) == args.gpu_uuid), None)
        if g is None:
            sys.exit(f"worker {args.worker} has no GPU {args.gpu_uuid}")
        reg.record_benchmark(Benchmark(args.route, args.stage, w.worker_id, g.name, g.memory_total_gb, args.peak_vram,
                                       args.duration, not args.failed, args.config, store.now()))
        print(f"benchmark recorded: {args.route}/{args.stage} on {g.name} ({'failed' if args.failed else 'ok'})")
    elif c == "certify":
        from .config import generation_lane_from_config

        gl = generation_lane_from_config(cfg.assets, base=cfg.path.parent, project_id=cfg.project.id,
                                         repo_root=Path(cfg.project.repo_path), data_dir=cfg.data_dir,
                                         manifest=load_manifest(cfg.data_dir))
        ad = gl.adapters.get(args.route)
        if ad is None:
            sys.exit(f"{args.route} is not configured under [assets.routes]")
        checks = ad.adapter_checks()
        print(json.dumps(checks, indent=2))
        if all(x.get("ok") for x in checks) and checks:
            reg.record_certification(args.route, checks, by=args.by, version=args.version)
            print(f"{args.route} certified for unattended use")
        else:
            sys.exit(f"{args.route} NOT certified")
    elif c == "lane":
        data = reg.load_lane(args.asset)
        if not data:
            sys.exit(f"no lane for {args.asset}")
        print(json.dumps(AssetLane.model_validate(data).summary(), indent=2))
    elif c == "switch-route":
        data = reg.load_lane(args.asset)
        if not data:
            sys.exit(f"no lane for {args.asset}")
        old = AssetLane.model_validate(data)
        kind = AssetLaneKind(args.lane)
        if kind not in ROUTES[args.route].lanes:
            sys.exit(f"{args.route} does not serve the {kind.value} lane")
        new = old.propose_replacement(route_kind=kind.value, route=args.route)
        reg.save_lane(f"{old.asset_id}@v{old.version}", old.kind, json.loads(old.model_dump_json()))
        reg.save_lane(old.asset_id, new.kind, json.loads(new.model_dump_json()))
        store.append_event("asset_route_switched", asset_id=old.asset_id, from_version=old.version,
                           to_version=new.version, lane=kind.value, route=args.route, by=args.by)
        print(f"{old.asset_id}: proposed replacement v{new.version} via {args.route}; v{old.version} stays approved "
              "and untouched until the replacement passes the same contract and gates")
    elif c == "inventory":
        rows = inventory_rows(reg.provenance(), find_credits(Path(cfg.project.repo_path) / cfg.project.workdir))
        out = Path(args.out) if args.out else cfg.data_dir / "licence_inventory"
        out.mkdir(parents=True, exist_ok=True)
        (out / "licence_inventory.json").write_text(json.dumps(rows, indent=2, default=str))
        (out / "licence_inventory.csv").write_text(inventory_csv(rows))
        print(f"{len(rows)} row(s) -> {out}/licence_inventory.json and .csv")


def register(sub) -> None:
    sp = sub.add_parser("assets", help="R2 asset routes, licence gates, GPU workers, lanes and licence inventory")
    a = sp.add_subparsers(dest="assets_cmd", required=True)
    p = a.add_parser("routes", help="the plan's model-route table")
    p.add_argument("--json", action="store_true")
    p = a.add_parser("select", help="which routes this project may use for a lane, and why not")
    p.add_argument("lane", choices=[k.value for k in AssetLaneKind])
    p.add_argument("--routes")
    p.add_argument("--stage")
    p.add_argument("--public-demo", action="store_true")
    p = a.add_parser("licence-gate", help="record the owner's first-use licence decision for a route")
    p.add_argument("route")
    p.add_argument("--terms-name", required=True)
    p.add_argument("--terms-version", required=True)
    p.add_argument("--output-rights", required=True)
    p.add_argument("--territory", required=True, help="e.g. 'India only' or 'worldwide'")
    p.add_argument("--public-demo", action="store_true", help="outputs may appear in public demos/marketing")
    p.add_argument("--checkpoint", help="exact model checkpoint / service plan")
    p = a.add_parser("permission", help="record documented rights that lift a licence restriction")
    p.add_argument("route")
    p.add_argument("--scope", choices=["territory", "commercial_use"], required=True)
    p.add_argument("--document", required=True)
    p.add_argument("--dependencies-cleared", action="store_true")
    p = a.add_parser("worker-preflight", help="record this worker's GPU capability (nvidia-smi)")
    p.add_argument("--worker-id", default="local")
    p.add_argument("--remote-provider", help="e.g. azure for a rented GPU (a data destination)")
    p = a.add_parser("benchmark", help="record a route benchmark on one GPU")
    p.add_argument("route")
    p.add_argument("--stage", default="default")
    p.add_argument("--worker", required=True)
    p.add_argument("--gpu-uuid", required=True)
    p.add_argument("--peak-vram", type=float)
    p.add_argument("--duration", type=float, required=True)
    p.add_argument("--failed", action="store_true")
    p.add_argument("--config", default="")
    p = a.add_parser("certify", help="run a configured adapter's checks with real credentials")
    p.add_argument("route")
    p.add_argument("--version", default="")
    p = a.add_parser("lane", help="show an asset's lane state")
    p.add_argument("asset")
    p = a.add_parser("switch-route", help="propose a replacement version via another route")
    p.add_argument("asset")
    p.add_argument("lane", choices=[k.value for k in AssetLaneKind])
    p.add_argument("route")
    p = a.add_parser("inventory", help="export the licence inventory (JSON + CSV)")
    p.add_argument("--out")
    p = a.add_parser("validate-brief", help="validate an <id>.asset.json production brief")
    p.add_argument("brief")
    p = a.add_parser("retarget-check", help="validate a forge-retarget/1 mapping and print its sha256")
    p.add_argument("mapping")
    for name, parser in a.choices.items():
        parser.set_defaults(fn=cmd_assets)
