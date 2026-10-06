"""CLI wiring for the R3-R5 command groups, run end to end on a freshly scaffolded Rune Duel project."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from forge.cli import main

from .conftest import git

RUNE = Path(__file__).resolve().parent.parent / "templates" / "turn-duel-2p" / "examples" / "rune-duel-brief.md"


@pytest.fixture
def project(tmp_path):
    main(["scaffold", str(tmp_path / "rune"), "--name", "Rune Duel"])
    repo = tmp_path / "rune"
    git(repo, "init", "-q", "-b", "main")
    git(repo, "config", "user.email", "t@example.com")
    git(repo, "config", "user.name", "T")
    git(repo, "add", "-A")
    git(repo, "commit", "-q", "-m", "scaffold")
    return repo / "forge" / "project.toml"


def run(pf, *argv):
    main(["--project-file", str(pf), "--by", "owner", *argv])


def test_r3_cli_flow(project, capsys, tmp_path):
    run(project, "init")
    run(project, "spec", "ingest", str(RUNE))
    out = capsys.readouterr().out
    assert "First version" in out and "[DRAFT]" in out
    with pytest.raises(SystemExit, match="approve"):
        run(project, "plan")
    run(project, "spec", "approve")
    assert "approved spec v1" in capsys.readouterr().out
    task_file = tmp_path / "backlog.json"
    run(project, "plan", "--out", str(task_file), "--max-parallel", "3")
    out = capsys.readouterr().out
    assert "Makespan" in out and "forge import" in out
    tasks = json.loads(task_file.read_text())["tasks"]
    assert {t["ticket"] for t in tasks} >= {"M1-rules", "M3-release"}
    run(project, "import", str(task_file))
    assert f"imported {len(tasks)} task(s) as DRAFT" in capsys.readouterr().out
    run(project, "trace")
    assert "Complete: **True**" in capsys.readouterr().out
    run(project, "trace", "--csv")
    assert capsys.readouterr().out.startswith("requirement,kind,status")


def test_r3_cli_questions(project, capsys, tmp_path):
    flawed = Path(__file__).resolve().parent / "data" / "flawed-brief.md"
    run(project, "spec", "ingest", str(flawed))
    out = capsys.readouterr().out
    assert "[NEEDS_INPUT]" in out and "BLOCKING" in out
    qid = next(line.split()[1] for line in out.splitlines() if "ambiguous" in line)
    run(project, "spec", "dismiss", qid, "--reason", "bonus damage is cut")
    assert qid not in capsys.readouterr().out
    with pytest.raises(SystemExit, match="open questions"):
        run(project, "spec", "approve")


def test_r4_r5_cli_commands(project, capsys, tmp_path, monkeypatch):
    monkeypatch.delenv("FORGE_HOSTED_ENABLED", raising=False)
    run(project, "telemetry", "status")
    assert "off (default)" in capsys.readouterr().out
    run(project, "licences")
    assert "| game-forge |" in capsys.readouterr().out
    run(project, "init")
    run(project, "diag", "preview")
    out = capsys.readouterr().out
    digest = out.split("--confirm ")[1].split()[0]
    run(project, "diag", "export", "--confirm", digest, "--out", str(tmp_path / "diag.zip"))
    assert (tmp_path / "diag.zip").exists()
    run(project, "setup", "--skip-sample")
    assert "Prerequisites" in capsys.readouterr().out
    run(project, "uninstall", "--install-root", str(tmp_path / "install"))
    assert "dry run" in capsys.readouterr().out
    run(project, "export", "--out", str(tmp_path / "export"))
    assert "verification: OK" in capsys.readouterr().out
    run(project, "hosted", "status")
    out = capsys.readouterr().out
    assert "disabled (default)" in out and "NOT declared" in out
    run(project, "economics", "--price", "999", "--tax-inclusive", "--customers", "50")
    out = capsys.readouterr().out
    assert "₹49,950" in out and "₹5,99,400" in out
    run(project, "metrics", "--scope", "empty test project")
    assert "Sample: 0" in capsys.readouterr().out
