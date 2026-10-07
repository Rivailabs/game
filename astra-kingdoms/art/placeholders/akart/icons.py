"""Placeholder UI and store icons (V1 ticket 69): one vector definition -> SVG source + PNG.

Each icon is a short list of drawing operations in a [-1, 1] square with **y pointing up** (the
same convention as ``unity/Assets/Scripts/Core/Presentation/ElementGlyphs.cs``, whose element
outlines are reused here so the counter explanations and the icons show the same symbols).
The same operations are written to an SVG file (the editable source) and rasterised with Pillow
(4x supersampled, then downsampled) to a PNG. No fonts, images or third-party shapes are used.

Element recognition never relies on colour alone (plan: "Element recognition uses icon shape,
label and effect pattern as well as colour"): every element has a distinct silhouette, and
``silhouette_distinctness`` lets the tests prove that the five element masks differ clearly.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterable, Sequence

from PIL import Image, ImageDraw

Point = tuple[float, float]

# --------------------------------------------------------------------------------------------
# Drawing operations
# --------------------------------------------------------------------------------------------


@dataclass(frozen=True)
class Op:
    """One drawing operation. ``kind`` is poly | cut | line | cutline | circle | cutcircle."""

    kind: str
    points: tuple[Point, ...] = ()
    width: float = 0.0
    centre: Point = (0.0, 0.0)
    radius: float = 0.0

    @property
    def erases(self) -> bool:
        return self.kind.startswith("cut")


def poly(pts: Iterable[Point]) -> Op:
    return Op("poly", tuple(pts))


def cut(pts: Iterable[Point]) -> Op:
    return Op("cut", tuple(pts))


def line(pts: Iterable[Point], width: float) -> Op:
    return Op("line", tuple(pts), width=width)


def cutline(pts: Iterable[Point], width: float) -> Op:
    return Op("cutline", tuple(pts), width=width)


def circle(cx: float, cy: float, r: float) -> Op:
    return Op("circle", centre=(cx, cy), radius=r)


def cutcircle(cx: float, cy: float, r: float) -> Op:
    return Op("cutcircle", centre=(cx, cy), radius=r)


def ring(cx: float, cy: float, r: float, w: float) -> list[Op]:
    return [circle(cx, cy, r + w / 2), cutcircle(cx, cy, r - w / 2)]


def rect(x0: float, y0: float, x1: float, y1: float, erase: bool = False) -> Op:
    pts = ((x0, y0), (x1, y0), (x1, y1), (x0, y1))
    return cut(pts) if erase else poly(pts)


def arc(cx: float, cy: float, r: float, a0: float, a1: float, n: int = 48) -> list[Point]:
    """Points on an arc from angle a0 to a1 (degrees, counter-clockwise, y up)."""
    return [(cx + r * math.cos(math.radians(a0 + (a1 - a0) * i / n)),
             cy + r * math.sin(math.radians(a0 + (a1 - a0) * i / n))) for i in range(n + 1)]


def pairs(flat: Sequence[float]) -> list[Point]:
    return [(flat[i], flat[i + 1]) for i in range(0, len(flat), 2)]


def transform(pts: Iterable[Point], scale: float = 1.0, dx: float = 0.0, dy: float = 0.0,
              sx: float | None = None, sy: float | None = None) -> list[Point]:
    kx = scale if sx is None else sx
    ky = scale if sy is None else sy
    return [(x * kx + dx, y * ky + dy) for x, y in pts]


def rotate(pts: Iterable[Point], degrees: float) -> list[Point]:
    c, s = math.cos(math.radians(degrees)), math.sin(math.radians(degrees))
    return [(x * c - y * s, x * s + y * c) for x, y in pts]


# --------------------------------------------------------------------------------------------
# Shapes shared with ElementGlyphs.cs (same numbers, y up)
# --------------------------------------------------------------------------------------------

FLAME_OUTER = pairs([0.0, 0.92, 0.38, 0.2, 0.62, -0.28, 0.52, -0.62, 0.0, -0.88, -0.52, -0.62, -0.62, -0.28, -0.38, 0.2])
FLAME_TONGUE = [(-0.16, -0.55), (0.16, -0.55), (0.0, 0.05)]
STONE = pairs([-0.78, -0.5, -0.3, -0.82, 0.48, -0.74, 0.86, -0.18, 0.62, 0.52, 0.04, 0.8, -0.62, 0.42, -0.9, -0.06])
BOLT = pairs([0.18, 0.95, -0.52, 0.02, -0.06, 0.02, -0.3, -0.95, 0.52, 0.12, 0.06, 0.12, 0.42, 0.95])


def spiral_points() -> list[Point]:
    a = 0.32 / (2 * math.pi)
    pts, theta = [], 0.0
    while True:
        r = a * theta + 0.05
        if r > 0.88:
            break
        pts.append((r * math.cos(theta), r * math.sin(theta)))
        theta += 0.08
    return pts


def wave_points(band: int) -> list[Point]:
    return [(x, 0.42 - band * 0.42 + 0.16 * math.sin(math.pi * 1.5 * (x + 1)))
            for x in [-0.92 + 1.84 * i / 60 for i in range(61)]]


def arrow(direction: str) -> list[Point]:
    """A block arrow pointing right, rotated to ``direction`` (right|left|up|down)."""
    base = [(-0.75, -0.18), (0.1, -0.18), (0.1, -0.5), (0.8, 0.0), (0.1, 0.5), (0.1, 0.18), (-0.75, 0.18)]
    return rotate(base, {"right": 0, "up": 90, "left": 180, "down": 270}[direction])


# --------------------------------------------------------------------------------------------
# The forty icons (list and purpose also in art/briefs/69-ui-store-icons.md)
# --------------------------------------------------------------------------------------------


@dataclass
class Icon:
    id: str
    category: str
    purpose: str
    tile: str  # tile colour, #rrggbb (secondary cue; the silhouette is the primary cue)
    ops: list[Op] = field(default_factory=list)


TILE = {
    "agni": "#a8381c", "vayu": "#2f7f6f", "prithvi": "#6f5228", "vidyut": "#5e45a3", "varuna": "#1c579c",
    "neutral": "#4e4e58", "terrain": "#38463a", "card": "#2b3758", "status": "#5a3434", "dodge": "#34465a",
    "ui": "#26262e",
}
GLYPH = "#ffffff"


def _icons() -> list[Icon]:
    I: list[Icon] = []

    def add(id_: str, category: str, purpose: str, tile: str, *ops):
        flat: list[Op] = []
        for o in ops:
            flat.extend(o if isinstance(o, list) else [o])
        I.append(Icon(id_, category, purpose, TILE[tile], flat))

    # Elements (5) + neutral ------------------------------------------------------------------
    add("element-agni", "element", "Agni element: flame", "agni", poly(FLAME_OUTER), cut(FLAME_TONGUE))
    add("element-vayu", "element", "Vayu element: spiral", "vayu", line(spiral_points(), 0.15))
    add("element-prithvi", "element", "Prithvi element: stone", "prithvi", poly(STONE), rect(-0.3, 0.15, 0.25, 0.25, erase=True))
    add("element-vidyut", "element", "Vidyut element: bolt", "vidyut", poly(BOLT))
    add("element-varuna", "element", "Varuna element: wave", "varuna", *[line(wave_points(b), 0.18) for b in range(3)])
    add("element-neutral", "element", "Neutral (Pass, Brahmastra): ring", "neutral", ring(0, 0, 0.72, 0.25))

    # Terrain (5) -----------------------------------------------------------------------------
    add("terrain-plain", "terrain", "Plain terrain: open ground", "terrain",
        rect(-0.85, -0.45, 0.85, -0.3), line([(-0.5, -0.3), (-0.6, 0.0)], 0.1), line([(0.0, -0.3), (0.0, 0.1)], 0.1),
        line([(0.5, -0.3), (0.6, 0.0)], 0.1), ring(0.45, 0.55, 0.18, 0.1))
    add("terrain-fort", "terrain", "Fort terrain: crenellated wall (cover)", "terrain",
        poly([(-0.85, -0.7), (0.85, -0.7), (0.85, 0.5), (0.55, 0.5), (0.55, 0.25), (0.3, 0.25), (0.3, 0.5),
              (0.0, 0.5), (0.0, 0.25), (-0.3, 0.25), (-0.3, 0.5), (-0.55, 0.5), (-0.55, 0.25), (-0.85, 0.25)]),
        cut(arc(0, -0.7, 0.3, 0, 180, 24)))
    add("terrain-river", "terrain", "River terrain: winding channel between banks", "terrain",
        line([(-0.55 + 0.25 * math.sin(math.pi * t / 20), -0.9 + 1.8 * t / 40) for t in range(41)], 0.12),
        line([(0.25 + 0.25 * math.sin(math.pi * t / 20), -0.9 + 1.8 * t / 40) for t in range(41)], 0.12),
        line([(-0.15 + 0.25 * math.sin(math.pi * t / 20), -0.5 + 1.0 * t / 40) for t in range(41)], 0.06))
    tree = [(0.0, 0.75), (0.4, 0.05), (0.18, 0.05), (0.45, -0.4), (-0.45, -0.4), (-0.18, 0.05), (-0.4, 0.05)]
    add("terrain-forest", "terrain", "Forest terrain: two trees", "terrain",
        poly(transform(tree, 0.9, -0.38, 0.05)), rect(-0.45, -0.75, -0.31, -0.32),
        poly(transform(tree, 0.7, 0.45, -0.1)), rect(0.4, -0.75, 0.5, -0.38))
    add("terrain-armoury", "terrain", "Armoury terrain: weapon rack (reserve weapon)", "terrain",
        rect(-0.85, -0.75, 0.85, -0.6), rect(-0.85, 0.45, 0.85, 0.6),
        *[poly(transform(arrow("up"), 0.55, x, -0.1, sx=0.4)) for x in (-0.5, 0.0, 0.5)])

    # Formation cards (6): the canonical rules envelopes ---------------------------------------
    add("card-chakra", "card", "Chakra card envelope: disk", "card", circle(0, 0, 0.78))
    add("card-garuda", "card", "Garuda card envelope: broad diamond", "card", poly([(-0.9, 0), (0, 0.45), (0.9, 0), (0, -0.45)]))
    add("card-suchi", "card", "Suchi card envelope: narrow forward triangle", "card",
        poly(transform([(-0.5, -0.5), (-0.5, 0.5), (2.0, 0.0)], 0.7, -0.525, 0.0)))
    add("card-makara", "card", "Makara card envelope: hook (L polygon)", "card",
        poly(transform([(-1, -0.5), (1.5, -0.5), (1.5, 1.5), (0.5, 1.5), (0.5, 0.5), (-1, 0.5)], 0.68, -0.17, -0.34)))
    add("card-padma", "card", "Padma card envelope: rounded petals", "card",
        *[circle(x * 0.85, y * 0.85, 0.55 * 0.85) for x, y in ((0, 0), (0.45, 0), (-0.45, 0), (0, 0.45), (0, -0.45))])
    add("card-vajra", "card", "Vajra card envelope: diamond with extended ends", "card",
        poly(transform([(-1, 0), (0, 1), (1, 0), (0, -1)], 0.5)), rect(-0.9, -0.125, 0.9, 0.125))

    # Statuses (5) ----------------------------------------------------------------------------
    add("status-burn", "status", "Burn status: outlined flame", "status",
        poly(FLAME_OUTER), cut(transform(FLAME_OUTER, 0.62, 0.0, -0.08)))
    add("status-shock", "status", "Shock status: bolt under a suppression ring", "status",
        ring(0, 0, 0.78, 0.14), poly(transform(BOLT, 0.6)), line([(-0.55, -0.55), (0.55, 0.55)], 0.14))
    add("status-cover", "status", "Cover: low brick wall", "status",
        rect(-0.85, -0.6, 0.85, 0.2), line([(-0.85, -0.2), (0.85, -0.2)], 0.06),
        cutline([(-0.85, -0.2), (0.85, -0.2)], 0.08), cutline([(-0.3, 0.2), (-0.3, -0.2)], 0.08),
        cutline([(0.35, 0.2), (0.35, -0.2)], 0.08), cutline([(0.0, -0.2), (0.0, -0.6)], 0.08))
    add("status-shield", "status", "Ash Shield block: round shield with boss", "status",
        ring(0, 0, 0.7, 0.18), circle(0, 0, 0.22))
    eye = arc(0, -0.55, 0.95, 35, 145, 24) + arc(0, 0.55, 0.95, 215, 325, 24)
    add("status-concealed", "status", "Mist Veil concealment: crossed-out eye", "status",
        poly(eye), cutcircle(0, 0, 0.2), cutline([(-0.75, -0.6), (0.75, 0.6)], 0.26), line([(-0.75, -0.6), (0.75, 0.6)], 0.1))

    # Dodge choices (4) -----------------------------------------------------------------------
    add("dodge-none", "dodge", "Dodge: none (stand)", "dodge", ring(0, 0, 0.6, 0.16), circle(0, 0, 0.2))
    add("dodge-left", "dodge", "Dodge: left", "dodge", poly(arrow("left")))
    add("dodge-right", "dodge", "Dodge: right", "dodge", poly(arrow("right")))
    add("dodge-jump", "dodge", "Dodge: jump", "dodge", poly(transform(arrow("up"), 0.85, 0, 0.12)), rect(-0.7, -0.85, 0.7, -0.72))

    # Interface (14) --------------------------------------------------------------------------
    add("ui-lock", "ui", "Lock choice", "ui",
        ring(0, 0.3, 0.34, 0.14), rect(-0.6, -0.2, 0.6, 0.3, erase=True), rect(-0.41, 0.0, -0.27, 0.3),
        rect(0.27, 0.0, 0.41, 0.3), rect(-0.55, -0.8, 0.55, 0.05),
        cutcircle(0, -0.3, 0.11), rect(-0.05, -0.62, 0.05, -0.3, erase=True))
    add("ui-timer", "ui", "Selection timer", "ui",
        ring(0, -0.1, 0.62, 0.14), rect(-0.15, 0.62, 0.15, 0.85), line([(0, -0.1), (0, 0.3)], 0.1), line([(0, -0.1), (0.3, -0.25)], 0.1))
    teeth = []
    for k in range(8):
        teeth.append(poly(rotate([(-0.13, 0.5), (0.13, 0.5), (0.11, 0.85), (-0.11, 0.85)], k * 45)))
    add("ui-settings", "ui", "Settings", "ui", circle(0, 0, 0.6), *teeth, cutcircle(0, 0, 0.25))
    add("ui-home", "ui", "Home", "ui", poly([(0, 0.85), (0.85, 0.05), (0.6, 0.05), (0.6, -0.8), (-0.6, -0.8), (-0.6, 0.05), (-0.85, 0.05)]),
        rect(-0.18, -0.8, 0.18, -0.25, erase=True))
    add("ui-replay", "ui", "Replay viewer", "ui", line(arc(0, 0, 0.62, 100, 400, 40), 0.16),
        poly([(-0.32, 0.35), (-0.02, 0.7), (-0.34, 0.95)]))
    add("ui-shop", "ui", "Shop", "ui", ring(0, 0.3, 0.32, 0.12), rect(-0.6, -0.1, 0.6, 0.3, erase=True), rect(-0.68, -0.8, 0.68, 0.35))
    add("ui-coin", "ui", "Earned coins", "ui", circle(0, 0, 0.82), cutcircle(0, 0, 0.66), circle(0, 0, 0.56),
        cut([(0, 0.32), (0.22, 0), (0, -0.32), (-0.22, 0)]))
    add("ui-back", "ui", "Back", "ui", line([(0.3, 0.6), (-0.3, 0.0), (0.3, -0.6)], 0.2))
    add("ui-close", "ui", "Close", "ui", line([(-0.55, -0.55), (0.55, 0.55)], 0.2), line([(-0.55, 0.55), (0.55, -0.55)], 0.2))
    add("ui-check", "ui", "Confirm / selected", "ui", line([(-0.6, 0.0), (-0.15, -0.45), (0.65, 0.5)], 0.2))
    add("ui-sound", "ui", "Sound effects volume", "ui",
        poly([(-0.8, -0.25), (-0.45, -0.25), (0.0, -0.65), (0.0, 0.65), (-0.45, 0.25), (-0.8, 0.25)]),
        line(arc(0.05, 0, 0.35, -50, 50, 16), 0.1), line(arc(0.05, 0, 0.65, -50, 50, 20), 0.1))
    add("ui-music", "ui", "Music volume", "ui", circle(-0.3, -0.5, 0.25), rect(-0.12, -0.5, 0.0, 0.75),
        poly([(0.0, 0.75), (0.55, 0.45), (0.55, 0.25), (0.0, 0.5)]))
    add("ui-haptics", "ui", "Haptics", "ui", rect(-0.3, -0.75, 0.3, 0.75), rect(-0.2, -0.6, 0.2, 0.6, erase=True),
        line([(-0.5, 0.4), (-0.65, 0.2), (-0.5, 0.0), (-0.65, -0.2), (-0.5, -0.4)], 0.08),
        line([(0.5, 0.4), (0.65, 0.2), (0.5, 0.0), (0.65, -0.2), (0.5, -0.4)], 0.08))
    add("ui-crown", "ui", "Match winner", "ui",
        poly([(-0.8, -0.5), (0.8, -0.5), (0.85, 0.5), (0.4, 0.05), (0.0, 0.65), (-0.4, 0.05), (-0.85, 0.5)]),
        rect(-0.8, -0.75, 0.8, -0.6))
    return I


ICONS: list[Icon] = _icons()

# --------------------------------------------------------------------------------------------
# Output: SVG and PNG from the same operations
# --------------------------------------------------------------------------------------------

MARGIN = 0.14  # fraction of the tile left around the glyph


def _to_px(p: Point, size: float) -> tuple[float, float]:
    inner = size * (1 - 2 * MARGIN)
    return (size * MARGIN + (p[0] + 1) / 2 * inner, size * MARGIN + (1 - p[1]) / 2 * inner)


def _fmt(v: float) -> str:
    return f"{v:.2f}".rstrip("0").rstrip(".")


def to_svg(icon: Icon, size: int = 128) -> str:
    s = float(size)
    out = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" viewBox="0 0 {size} {size}">',
           f"  <!-- {icon.id}: {icon.purpose}. Original placeholder generated by art/placeholders (no external input). -->",
           f'  <rect x="0" y="0" width="{size}" height="{size}" rx="{_fmt(s * 0.18)}" fill="{icon.tile}"/>']
    scale = s * (1 - 2 * MARGIN) / 2
    for op in icon.ops:
        colour = icon.tile if op.erases else GLYPH
        if op.kind in ("poly", "cut"):
            d = " ".join(f"{_fmt(x)},{_fmt(y)}" for x, y in (_to_px(p, s) for p in op.points))
            out.append(f'  <polygon points="{d}" fill="{colour}"/>')
        elif op.kind in ("line", "cutline"):
            d = " ".join(f"{_fmt(x)},{_fmt(y)}" for x, y in (_to_px(p, s) for p in op.points))
            out.append(f'  <polyline points="{d}" fill="none" stroke="{colour}" stroke-width="{_fmt(op.width * scale)}" '
                       f'stroke-linecap="round" stroke-linejoin="round"/>')
        elif op.kind in ("circle", "cutcircle"):
            cx, cy = _to_px(op.centre, s)
            out.append(f'  <circle cx="{_fmt(cx)}" cy="{_fmt(cy)}" r="{_fmt(op.radius * scale)}" fill="{colour}"/>')
        else:  # pragma: no cover - guarded by the constructors above
            raise ValueError(op.kind)
    out.append("</svg>")
    return "\n".join(out) + "\n"


def _hex(c: str) -> tuple[int, int, int, int]:
    return int(c[1:3], 16), int(c[3:5], 16), int(c[5:7], 16), 255


def rasterise(icon: Icon, size: int = 128, supersample: int = 4, tile: bool = True) -> Image.Image:
    """RGBA image of the icon. With ``tile=False`` only the glyph mask is drawn (for tests)."""
    big = size * supersample
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    bg = _hex(icon.tile) if tile else (0, 0, 0, 255)
    fg = _hex(GLYPH)
    if tile:
        d.rounded_rectangle((0, 0, big - 1, big - 1), radius=big * 0.18, fill=bg)
    else:
        d.rectangle((0, 0, big - 1, big - 1), fill=bg)
    scale = big * (1 - 2 * MARGIN) / 2
    for op in icon.ops:
        colour = bg if op.erases else fg
        if op.kind in ("poly", "cut"):
            d.polygon([_to_px(p, big) for p in op.points], fill=colour)
        elif op.kind in ("line", "cutline"):
            pts = [_to_px(p, big) for p in op.points]
            w = max(1, int(round(op.width * scale)))
            d.line(pts, fill=colour, width=w, joint="curve")
            r = w / 2  # round caps, as in the SVG
            for x, y in (pts[0], pts[-1]):
                d.ellipse((x - r, y - r, x + r, y + r), fill=colour)
        elif op.kind in ("circle", "cutcircle"):
            cx, cy = _to_px(op.centre, big)
            r = op.radius * scale
            d.ellipse((cx - r, cy - r, cx + r, cy + r), fill=colour)
    return img.resize((size, size), Image.Resampling.LANCZOS)


def glyph_mask(icon: Icon, size: int = 64) -> list[bool]:
    """Binary glyph coverage (True = glyph ink) used for distinctness checks."""
    img = rasterise(icon, size=size, supersample=2, tile=False).convert("L")
    return [v > 127 for v in img.tobytes()]


def silhouette_distinctness(a: Icon, b: Icon, size: int = 64) -> float:
    """1 - IoU of the two glyph masks (0 = identical silhouettes, 1 = no overlap)."""
    ma, mb = glyph_mask(a, size), glyph_mask(b, size)
    inter = sum(1 for x, y in zip(ma, mb) if x and y)
    union = sum(1 for x, y in zip(ma, mb) if x or y)
    return 1.0 - (inter / union if union else 1.0)


def contrast_ratio(fg: str, bg: str) -> float:
    """WCAG 2.x contrast ratio between two #rrggbb colours."""

    def lum(c: str) -> float:
        ch = []
        for i in (1, 3, 5):
            v = int(c[i:i + 2], 16) / 255
            ch.append(v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4)
        return 0.2126 * ch[0] + 0.7152 * ch[1] + 0.0722 * ch[2]

    la, lb = sorted((lum(fg), lum(bg)), reverse=True)
    return (la + 0.05) / (lb + 0.05)


def generate(png_dir: Path, svg_dir: Path, size: int = 128) -> list[tuple[Icon, Path, Path]]:
    png_dir.mkdir(parents=True, exist_ok=True)
    svg_dir.mkdir(parents=True, exist_ok=True)
    out = []
    for icon in ICONS:
        svg_path = svg_dir / f"{icon.id}.svg"
        svg_path.write_text(to_svg(icon, size), encoding="ascii", newline="\n")
        png_path = png_dir / f"{icon.id}.png"
        # optimize=False and no metadata keep the bytes a pure function of the pixels + zlib.
        rasterise(icon, size).save(png_path, format="PNG", optimize=False)
        out.append((icon, png_path, svg_path))
    return out
