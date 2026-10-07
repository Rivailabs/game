"""CLI: forge assets / backup / retention / sandbox, and the R2 seed files."""

from __future__ import annotations

import json
import os
from pathlib import Path

import pytest

from forge.assets.brief import load_asset_brief
from forge.assets.registry import AssetRegistry
from forge.cli import main
from forge.config import build_runtime, load_project_config
from forge.models import TaskState as S, TaskType
from forge.store import Store
from forge.taskfile import import_tasks, load_task_file

from .test_cli_and_seed import write_project

ROOT = Path(__file__).resolve().parent.parent
SEED_PROJECT = ROOT / "projects" / "astra-kingdoms" / "project.toml"
R2_TASKS = ROOT / "projects" / "astra-kingdoms" / "tasks" / "r2_assets.toml"


def test_r2_seed_tasks_and_briefs():
    store = Store(":memory:")
    created, _ = import_tasks(store, "astra-kingdoms", load_task_file(R2_TASKS))
    assert [r.ticket for r in created] == ["R2-P1", "R2-P2", "R2-A1", "R2-A2", "R2-A3", "R2-A4", "R2-A5"]
    assert all(r.state == S.DRAFT and r.task_type == TaskType.ASSET and r.visual_review_required for r in created)
    a5 = store.find_root_by_ticket("astra-kingdoms", "R2-A5")
    assert [d.root_id for d in a5.dependencies] == ["astra-kingdoms-tR2-A4"]
    production = ROOT / "briefs" / "astra_v1" / "production"
    for r in created:
        p = ROOT.parent / r.asset_brief
        assert p.exists(), r.asset_brief
        brief_path = p if p.name.endswith(".asset.json") else production / (p.stem + ".asset.json")
        brief, _ = load_asset_brief(brief_path)
        assert f"astra-kingdoms/assets/source/{brief.id}/**" in r.permitted_paths
    for f in production.glob("*.asset.json"):
        load_asset_brief(f)  # every committed production brief validates
    archer, _ = load_asset_brief(production / "astra_archer.asset.json")
    assert archer.budget == "archer" and archer.procedural_parts == ["bowstring"] and not archer.catalogue_first
    cfg = load_project_config(SEED_PROJECT)
    rt = build_runtime(cfg, providers={})
    assert rt.generation_lane is not None and rt.generation_lane.adapters == {}  # no route configured yet
    assert rt.generation_lane.cfg.briefs_dir == production


def test_assets_cli(tmp_path, repo, capsys, monkeypatch):
    proj = write_project(tmp_path, repo)
    pf = ["--project-file", str(proj / "project.toml")]
    main(pf + ["init"])
    main(pf + ["assets", "routes"])
    out = capsys.readouterr().out
    assert "hunyuan3d-2.1" in out and "EU,UK,KR" in out and "MANUAL" in out
    main(pf + ["assets", "routes", "--json"])
    assert len(json.loads(capsys.readouterr().out)) == 15
    main(pf + ["assets", "licence-gate", "meshy", "--terms-name", "Meshy ToS", "--terms-version", "2026-09",
               "--output-rights", "commercial", "--territory", "IN"])
    main(pf + ["assets", "permission", "gvhmr", "--scope", "commercial_use", "--document", "signed #1",
               "--dependencies-cleared"])
    main(pf + ["assets", "select", "mesh", "--routes", "meshy,hunyuan3d-2.1"])
    out = capsys.readouterr().out
    assert "meshy" in out and "refused" in out and "distribution countries" in out
    reg = AssetRegistry(Store(proj / "data" / "forge.db"))
    assert "meshy" in reg.licence_gates() and reg.permissions()[0].route == "gvhmr"
    from forge.checks.base import ProcResult

    import forge.assets.gpu as gpu_mod

    monkeypatch.setattr(gpu_mod.shutil, "which", lambda n: "/usr/bin/" + n)
    monkeypatch.setattr(gpu_mod, "_default_run", lambda argv, cwd, t, env=None: ProcResult(
        0, "0, GPU-1, NVIDIA RTX 4090, 24564, 23000, 550.1, 8.9\n", "", 0.0))
    main(pf + ["assets", "worker-preflight", "--worker-id", "w1"])
    main(pf + ["assets", "benchmark", "trellis2", "--worker", "w1", "--gpu-uuid", "GPU-1", "--peak-vram", "21.5",
               "--duration", "300", "--config", "trellis2@abc"])
    assert reg.benchmarks("trellis2")[0].gpu_model == "NVIDIA RTX 4090"
    with pytest.raises(SystemExit, match="not configured"):
        main(pf + ["assets", "certify", "meshy"])
    main(pf + ["assets", "inventory", "--out", str(tmp_path / "inv")])
    assert (tmp_path / "inv" / "licence_inventory.csv").read_text().startswith("asset_id,version,stage")
    main(pf + ["assets", "retarget-check", str(ROOT / "forge" / "assets" / "data" / "retarget_smpl_archer.json")])
    with pytest.raises(SystemExit, match="no lane"):
        main(pf + ["assets", "lane", "missing"])


def test_assets_switch_route_keeps_the_approved_version(tmp_path, repo, capsys):
    proj = write_project(tmp_path, repo)
    pf = ["--project-file", str(proj / "project.toml")]
    main(pf + ["init"])
    from forge.assets.lane import AssetLane

    reg = AssetRegistry(Store(proj / "data" / "forge.db"))
    lane = AssetLane.new("crate", "prop", skip=[])
    lane.routes = {"mesh": "meshy"}
    reg.save_lane("crate", "prop", json.loads(lane.model_dump_json()))
    main(pf + ["assets", "switch-route", "crate", "mesh", "trellis2"])
    assert "proposed replacement v2" in capsys.readouterr().out
    assert reg.load_lane("crate")["version"] == 2 and reg.load_lane("crate@v1")["version"] == 1
    with pytest.raises(SystemExit, match="does not serve"):
        main(pf + ["assets", "switch-route", "crate", "mesh", "hy-motion-1.0"])


def test_backup_retention_and_sandbox_cli(tmp_path, repo, capsys, monkeypatch):
    proj = write_project(tmp_path, repo)
    pf = ["--project-file", str(proj / "project.toml")]
    main(pf + ["init"])
    pw = tmp_path / "secret" / "pw"
    pw.parent.mkdir()
    pw.write_text("a long backup passphrase")
    os.chmod(pw, 0o600)
    out = tmp_path / "b" / "x.fgbk"
    out.parent.mkdir()
    import forge.backup.crypto as crypto

    monkeypatch.setattr(crypto, "SCRYPT_LOG2_N", 11)
    main(pf + ["backup", "create", "--out", str(out), "--passphrase-file", str(pw)])
    assert "encrypted backup written" in capsys.readouterr().out
    with pytest.raises(SystemExit) as ei:
        main(pf + ["backup", "verify", str(out), "--passphrase-file", str(pw)])
    assert ei.value.code == 0
    with pytest.raises(SystemExit) as ei:
        main(pf + ["backup", "restore", str(out), "--to", str(tmp_path / "r"), "--passphrase-file", str(pw)])
    assert ei.value.code == 0
    with pytest.raises(SystemExit) as ei:
        main(pf + ["backup", "drill", "--passphrase-file", str(pw)])
    assert ei.value.code == 0
    capsys.readouterr()
    main(pf + ["backup", "status"])
    assert "never" not in capsys.readouterr().out
    inside = repo / "pw"
    inside.write_text("a long backup passphrase")
    os.chmod(inside, 0o600)
    with pytest.raises(SystemExit, match="outside every project"):
        main(pf + ["backup", "create", "--passphrase-file", str(inside)])
    main(pf + ["retention", "plan"])
    assert "failed candidates 14 d" in capsys.readouterr().out
    main(pf + ["retention", "apply"])
    assert "dry run" in capsys.readouterr().out
    main(pf + ["retention", "hold", "1", "--reason", "defect"])
    main(pf + ["sandbox", "detect", "--config", str(tmp_path / "none.toml")])
    assert "role generated_code_check" in capsys.readouterr().out
