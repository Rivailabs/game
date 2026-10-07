"""Git workspace isolation and the single integration writer.

* Each code attempt gets its own ``git worktree`` rooted at a pinned accepted commit.
* Workers never push to the accepted branch. The ``IntegrationWriter`` applies one
  candidate on a staging checkout of *current* accepted main, runs protected
  checks there, and promotes with an atomic compare-and-swap
  (``git update-ref <branch> <new> <expected-old>``). If accepted main moved
  in the meantime the promotion is rejected (``BaseMoved``).
* Git hooks are disabled for every Forge git call (hooks are an execution surface).
"""

from __future__ import annotations

import os
import shutil
import subprocess
from dataclasses import dataclass
from pathlib import Path

from .credentials import scrubbed_env
from .pathglob import match_path

FORGE_IDENTITY = {
    "GIT_AUTHOR_NAME": "Game Forge",
    "GIT_AUTHOR_EMAIL": "forge@localhost",
    "GIT_COMMITTER_NAME": "Game Forge",
    "GIT_COMMITTER_EMAIL": "forge@localhost",
}


class GitError(Exception):
    pass


class BaseMoved(GitError):
    pass


def git(args: list[str], cwd: str | os.PathLike, *, check: bool = True, input_: str | None = None) -> str:
    env = scrubbed_env(extra=FORGE_IDENTITY)
    env["GIT_TERMINAL_PROMPT"] = "0"
    cmd = ["git", "-c", "core.hooksPath=/dev/null", "-c", "commit.gpgsign=false", *args]
    p = subprocess.run(cmd, cwd=str(cwd), capture_output=True, text=True, env=env, input=input_)
    if check and p.returncode != 0:
        raise GitError(f"git {' '.join(args)} failed ({p.returncode}): {p.stderr.strip() or p.stdout.strip()}")
    return p.stdout


def rev_parse(repo: str | os.PathLike, ref: str) -> str:
    return git(["rev-parse", "--verify", f"{ref}^{{commit}}"], repo).strip()


def ref_exists(repo: str | os.PathLike, ref: str) -> bool:
    try:
        rev_parse(repo, ref)
        return True
    except GitError:
        return False


@dataclass
class ChangedFile:
    status: str  # A, M, D, R
    path: str
    old_path: str | None = None


class WorkspaceManager:
    def __init__(self, repo_path: str | os.PathLike, worktrees_dir: str | os.PathLike):
        self.repo = Path(repo_path).resolve()
        self.worktrees_dir = Path(worktrees_dir).resolve()
        self.worktrees_dir.mkdir(parents=True, exist_ok=True)

    def ensure_branch(self, branch: str, from_ref: str = "HEAD") -> str:
        if not ref_exists(self.repo, f"refs/heads/{branch}"):
            git(["branch", branch, from_ref], self.repo)
        return rev_parse(self.repo, branch)

    def head(self, branch: str) -> str:
        return rev_parse(self.repo, branch)

    def create(self, attempt_id: str, base_commit: str) -> tuple[Path, str]:
        path = self.worktrees_dir / attempt_id
        branch = f"forge/cand/{attempt_id}"
        if path.exists():
            # Recovery: reuse the existing worktree if it is registered.
            return path, branch
        git(["worktree", "add", "-b", branch, str(path), base_commit], self.repo)
        return path, branch

    def commit_candidate(self, path: str | os.PathLike, message: str) -> str | None:
        git(["add", "-A"], path)
        if not git(["status", "--porcelain"], path).strip():
            return None
        git(["commit", "-q", "-m", message], path)
        return rev_parse(path, "HEAD")

    def changed_files(self, base: str, candidate: str) -> list[ChangedFile]:
        out = git(["diff", "--name-status", "-M", base, candidate], self.repo)
        files: list[ChangedFile] = []
        for line in out.splitlines():
            parts = line.split("\t")
            st = parts[0]
            if st.startswith("R") and len(parts) == 3:
                files.append(ChangedFile("R", parts[2], parts[1]))
            elif len(parts) >= 2:
                files.append(ChangedFile(st[0], parts[1]))
        return files

    def diff(self, base: str, candidate: str) -> str:
        return git(["diff", "-M", "--unified=3", base, candidate], self.repo)

    def restore_protected(self, path: str | os.PathLike, base: str, protected: list[str]) -> list[str]:
        """Make protected paths in the worktree byte-identical to ``base``.

        Protected acceptance checks live outside the worker's writable scope; even
        if a candidate edited them, verification runs against the accepted copies.
        Returns the list of restored/removed paths.
        """
        if not protected:
            return []
        wt = Path(path)
        base_files = [f for f in git(["ls-tree", "-r", "--name-only", base], self.repo).splitlines()
                      if match_path(f, protected)]
        touched: list[str] = []
        # remove files added under protected paths
        for p in wt.rglob("*"):
            if ".git" in p.parts or not p.is_file():
                continue
            rel = p.relative_to(wt).as_posix()
            if match_path(rel, protected) and rel not in base_files:
                p.unlink()
                touched.append(rel)
        if base_files:
            before = {f: (wt / f).read_bytes() if (wt / f).exists() else None for f in base_files}
            git(["checkout", base, "--", *base_files], wt)
            touched += [f for f in base_files if before[f] != (wt / f).read_bytes()]
        return touched

    def remove(self, path: str | os.PathLike, branch: str | None = None) -> None:
        git(["worktree", "remove", "--force", str(path)], self.repo, check=False)
        if Path(path).exists():
            shutil.rmtree(path, ignore_errors=True)
        git(["worktree", "prune"], self.repo, check=False)
        if branch:
            git(["branch", "-D", branch], self.repo, check=False)


@dataclass
class StagedResult:
    ok: bool
    staged_sha: str | None
    base_main: str
    path: Path
    conflict: bool = False
    fast_forward: bool = False
    detail: str = ""


class IntegrationWriter:
    """The only component that advances the accepted branch."""

    def __init__(self, repo_path: str | os.PathLike, accepted_branch: str, staging_dir: str | os.PathLike):
        self.repo = Path(repo_path).resolve()
        self.branch = accepted_branch
        self.staging = Path(staging_dir).resolve()

    def _prepare_staging(self, main_sha: str) -> None:
        if not (self.staging / ".git").exists():
            if self.staging.exists():
                shutil.rmtree(self.staging)
            self.staging.parent.mkdir(parents=True, exist_ok=True)
            git(["worktree", "add", "--detach", str(self.staging), main_sha], self.repo)
        else:
            git(["merge", "--abort"], self.staging, check=False)
            git(["checkout", "-q", "--detach", "-f", main_sha], self.staging)
            git(["reset", "-q", "--hard", main_sha], self.staging)
            git(["clean", "-q", "-fd"], self.staging)

    def stage(self, candidate_sha: str, message: str = "") -> StagedResult:
        main = rev_parse(self.repo, self.branch)
        self._prepare_staging(main)
        mb = git(["merge-base", main, candidate_sha], self.repo).strip()
        if mb == main:
            git(["checkout", "-q", "--detach", candidate_sha], self.staging)
            return StagedResult(True, candidate_sha, main, self.staging, fast_forward=True)
        p = subprocess.run(
            ["git", "-c", "core.hooksPath=/dev/null", "merge", "--no-ff", "--no-edit", "-m",
             message or f"Forge integration of {candidate_sha[:12]}", candidate_sha],
            cwd=self.staging, capture_output=True, text=True, env=scrubbed_env(extra=FORGE_IDENTITY),
        )
        if p.returncode != 0:
            git(["merge", "--abort"], self.staging, check=False)
            return StagedResult(False, None, main, self.staging, conflict=True,
                                detail=(p.stdout + p.stderr).strip()[-2000:])
        return StagedResult(True, rev_parse(self.staging, "HEAD"), main, self.staging)

    def promote(self, staged_sha: str, expected_main: str) -> str:
        """Atomic compare-and-swap of the accepted branch. Raises ``BaseMoved``."""
        current = rev_parse(self.repo, self.branch)
        if current != expected_main:
            raise BaseMoved(f"{self.branch} moved from {expected_main[:12]} to {current[:12]}")
        try:
            git(["update-ref", f"refs/heads/{self.branch}", staged_sha, expected_main], self.repo)
        except GitError as e:
            raise BaseMoved(str(e)) from e
        return staged_sha

    def current_main(self) -> str:
        return rev_parse(self.repo, self.branch)
