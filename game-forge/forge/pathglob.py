"""Path glob matching with ``**`` support (POSIX-style relative paths)."""

from __future__ import annotations

import functools
import re


@functools.lru_cache(maxsize=512)
def _compile(pattern: str) -> re.Pattern:
    pattern = pattern.strip()
    if pattern.startswith("./"):
        pattern = pattern[2:]
    if pattern.endswith("/"):
        pattern += "**"
    i, out = 0, []
    while i < len(pattern):
        ch = pattern[i]
        if pattern.startswith("**/", i):
            out.append("(?:.*/)?")
            i += 3
        elif pattern.startswith("**", i):
            out.append(".*")
            i += 2
        elif ch == "*":
            out.append("[^/]*")
            i += 1
        elif ch == "?":
            out.append("[^/]")
            i += 1
        else:
            out.append(re.escape(ch))
            i += 1
    return re.compile("^" + "".join(out) + "$")


def match_path(path: str, patterns: list[str] | tuple[str, ...]) -> bool:
    path = path.replace("\\", "/").lstrip("/")
    return any(_compile(p).match(path) for p in patterns if p)


def is_safe_relative(path: str) -> bool:
    """Reject absolute paths, parent traversal and the .git directory."""
    p = path.replace("\\", "/")
    if p.startswith("/") or re.match(r"^[A-Za-z]:", p):
        return False
    parts = [x for x in p.split("/") if x not in ("", ".")]
    if not parts or any(x == ".." for x in parts):
        return False
    if parts[0] == ".git" or ".git" in parts:
        return False
    return True
