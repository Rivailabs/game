"""Read a game brief (Markdown, plain text or DOCX) into ordered blocks.

A block is one bullet, one table row or one paragraph, together with the heading path it sits
under and the source line (DOCX: paragraph number). Nothing is interpreted here; this module only
recovers structure so every extracted requirement can cite where it came from.
"""

from __future__ import annotations

import hashlib
import re
from dataclasses import dataclass, field
from pathlib import Path


class IntakeError(Exception):
    pass


@dataclass(frozen=True)
class Block:
    section: tuple[str, ...]  # heading path, outermost first
    text: str
    line: int  # 1-based source line (DOCX: paragraph index)
    kind: str  # bullet | paragraph | table_row


@dataclass
class SourceDocument:
    path: str
    fmt: str  # markdown | text | docx
    sha256: str
    title: str
    blocks: list[Block] = field(default_factory=list)
    raw: bytes = b""


_HEADING = re.compile(r"^(#{1,6})\s+(.*?)\s*#*\s*$")
_BULLET = re.compile(r"^\s*(?:[-*+]|\d+[.)])\s+(.*)$")
_TABLE_SEP = re.compile(r"^\s*\|?\s*:?-{2,}")


def _clean_inline(text: str) -> str:
    text = re.sub(r"`([^`]*)`", r"\1", text)
    text = re.sub(r"\[([^\]]+)\]\([^)]+\)", r"\1", text)  # links -> text
    return re.sub(r"\s+", " ", text).strip()


def parse_markdown(text: str) -> tuple[str, list[Block]]:
    title = ""
    headings: list[tuple[int, str]] = []
    blocks: list[Block] = []
    para: list[str] = []
    para_line = 0
    bullet: list[str] = []
    bullet_line = 0
    in_code = False

    def section() -> tuple[str, ...]:
        return tuple(h for _, h in headings)

    def flush() -> None:
        nonlocal para, bullet
        if para:
            blocks.append(Block(section(), _clean_inline(" ".join(para)), para_line, "paragraph"))
            para = []
        if bullet:
            blocks.append(Block(section(), _clean_inline(" ".join(bullet)), bullet_line, "bullet"))
            bullet = []

    for n, line in enumerate(text.splitlines(), 1):
        if line.strip().startswith("```"):
            flush()
            in_code = not in_code
            continue
        if in_code:
            continue
        m = _HEADING.match(line)
        if m:
            flush()
            level, heading = len(m.group(1)), _clean_inline(m.group(2))
            if level == 1 and not title:
                title = heading
                headings = []
                continue
            headings = [(lvl, h) for lvl, h in headings if lvl < level] + [(level, heading)]
            continue
        if not line.strip():
            flush()
            continue
        if line.lstrip().startswith("|"):
            flush()
            if _TABLE_SEP.match(line):
                continue
            cells = [c.strip() for c in line.strip().strip("|").split("|")]
            blocks.append(Block(section(), _clean_inline(" | ".join(c for c in cells if c)), n, "table_row"))
            continue
        b = _BULLET.match(line)
        if b:
            flush()
            bullet, bullet_line = [b.group(1)], n
            continue
        if bullet and line.startswith((" ", "\t")):
            bullet.append(line.strip())  # continuation of a bullet
            continue
        if bullet:
            flush()
        if not para:
            para_line = n
        para.append(line.strip())
    flush()
    return title, blocks


def parse_plain_text(text: str) -> tuple[str, list[Block]]:
    """Plain text: a short capitalised line without final punctuation, after a blank line, is a heading."""
    lines = text.splitlines()
    md: list[str] = []
    for i, line in enumerate(lines):
        s = line.strip()
        is_heading = (s and len(s) <= 40 and s[0].isupper() and not s.endswith((".", ",", ";", "?", "!"))
                      and not _BULLET.match(line))
        if is_heading and (i == 0 or not lines[i - 1].strip()):
            md.append(("# " if not md else "## ") + s.rstrip(":"))
        else:
            md.append(line)
    return parse_markdown("\n".join(md))


def parse_docx(path: Path) -> tuple[str, list[Block]]:
    try:
        import docx  # python-docx (optional dependency)
    except ImportError as e:  # pragma: no cover - environment dependent
        raise IntakeError("reading .docx needs python-docx (pip install 'game-forge[intake]'), "
                          "or export the brief as Markdown") from e
    d = docx.Document(str(path))
    title = ""
    headings: list[tuple[int, str]] = []
    blocks: list[Block] = []
    for n, p in enumerate(d.paragraphs, 1):
        text = _clean_inline(p.text)
        if not text:
            continue
        style = (p.style.name if p.style is not None else "") or ""
        if style == "Title" or (style.startswith("Heading") and not title and style.endswith(" 1")):
            if not title:
                title = text
                continue
        if style.startswith("Heading"):
            try:
                level = int(style.split()[-1])
            except ValueError:
                level = 2
            headings = [(lv, h) for lv, h in headings if lv < level] + [(level, text)]
            continue
        kind = "bullet" if "List" in style else "paragraph"
        blocks.append(Block(tuple(h for _, h in headings), text, n, kind))
    for t_index, table in enumerate(d.tables, 1):
        for r_index, row in enumerate(table.rows, 1):
            cells = [_clean_inline(c.text) for c in row.cells]
            if any(cells):
                blocks.append(Block(("Table %d" % t_index,), " | ".join(c for c in cells if c),
                                    10_000 * t_index + r_index, "table_row"))
    return title, blocks


def read_document(path: str | Path) -> SourceDocument:
    path = Path(path)
    if not path.exists():
        raise IntakeError(f"no such brief: {path}")
    raw = path.read_bytes()
    suffix = path.suffix.lower()
    if suffix == ".docx":
        fmt = "docx"
        title, blocks = parse_docx(path)
    else:
        try:
            text = raw.decode("utf-8-sig")
        except UnicodeDecodeError as e:
            raise IntakeError(f"{path}: brief is not UTF-8 text") from e
        if suffix in (".md", ".markdown"):
            fmt = "markdown"
            title, blocks = parse_markdown(text)
        else:
            fmt = "text"
            title, blocks = parse_plain_text(text)
    if not blocks:
        raise IntakeError(f"{path}: no content found")
    return SourceDocument(path=str(path), fmt=fmt, sha256=hashlib.sha256(raw).hexdigest(),
                          title=title or path.stem, blocks=blocks, raw=raw)
