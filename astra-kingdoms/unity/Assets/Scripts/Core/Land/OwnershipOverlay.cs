using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;

namespace AstraKingdoms.Client.Land
{
    /// <summary>A contour segment in board cell-centre coordinates (cell (x, y) has its centre at (x + 0.5, y + 0.5); y down).</summary>
    public readonly struct ContourSegment
    {
        public readonly double X0, Y0, X1, Y1;

        public ContourSegment(double x0, double y0, double x1, double y1)
        {
            X0 = x0;
            Y0 = y0;
            X1 = x1;
            Y1 = y1;
        }
    }

    /// <summary>
    /// Rendered ownership contours (ticket 37). Marching squares over the cell-centre lattice of a
    /// binary field (owned by A = 1, anything else = 0) produces the smooth border line between the
    /// kingdoms. It reads a <i>copy</i> of the ownership (a byte array), so it cannot change the
    /// logical cells; area and totals always come from exact cell counts, never from contours.
    /// Saddle cells are resolved by the centre average, which is deterministic.
    /// </summary>
    public static class OwnershipContours
    {
        public const byte Outside = 255;

        /// <summary>Snapshot of ownership: 0 = A, 1 = B, 255 = inactive, row-major by cell ID.</summary>
        public static byte[] Snapshot(Territory territory)
        {
            if (territory == null) throw new ArgumentNullException(nameof(territory));
            var owners = new byte[Board.GridCellCount];
            for (int i = 0; i < owners.Length; i++) owners[i] = Outside;
            foreach (int id in Board.ActiveCellIds) owners[id] = (byte)territory.OwnerOf(id);
            return owners;
        }

        /// <summary>Contour between <paramref name="side"/> and everything else, as segments.</summary>
        public static List<ContourSegment> Build(byte[] owners, PlayerSide side = PlayerSide.A)
        {
            if (owners == null || owners.Length != Board.GridCellCount) throw new ArgumentException("Expected a 256x256 ownership snapshot.", nameof(owners));
            var segments = new List<ContourSegment>();
            int n = Board.Size;
            byte s = (byte)side;
            // Lattice square with corners at cell centres (x, y), (x+1, y), (x+1, y+1), (x, y+1).
            for (int y = -1; y < n; y++)
            {
                for (int x = -1; x < n; x++)
                {
                    int tl = V(owners, x, y, s), tr = V(owners, x + 1, y, s), br = V(owners, x + 1, y + 1, s), bl = V(owners, x, y + 1, s);
                    int code = (tl << 3) | (tr << 2) | (br << 1) | bl;
                    if (code == 0 || code == 15) continue;
                    double cx = x + 0.5, cy = y + 0.5; // top-left corner (cell centre)
                    // Edge midpoints: top, right, bottom, left.
                    double tX = cx + 0.5, tY = cy, rX = cx + 1, rY = cy + 0.5, bX = cx + 0.5, bY = cy + 1, lX = cx, lY = cy + 0.5;
                    switch (code)
                    {
                        case 1: case 14: segments.Add(new ContourSegment(lX, lY, bX, bY)); break;
                        case 2: case 13: segments.Add(new ContourSegment(bX, bY, rX, rY)); break;
                        case 3: case 12: segments.Add(new ContourSegment(lX, lY, rX, rY)); break;
                        case 4: case 11: segments.Add(new ContourSegment(tX, tY, rX, rY)); break;
                        case 6: case 9: segments.Add(new ContourSegment(tX, tY, bX, bY)); break;
                        case 7: case 8: segments.Add(new ContourSegment(lX, lY, tX, tY)); break;
                        case 5:
                        case 10:
                            // Saddle: the average (2 of 4 = 0.5) counts as inside, so the diagonals connect.
                            bool joined = code == 5;
                            if (joined)
                            {
                                segments.Add(new ContourSegment(lX, lY, tX, tY));
                                segments.Add(new ContourSegment(bX, bY, rX, rY));
                            }
                            else
                            {
                                segments.Add(new ContourSegment(tX, tY, rX, rY));
                                segments.Add(new ContourSegment(lX, lY, bX, bY));
                            }
                            break;
                    }
                }
            }
            return segments;
        }

        private static int V(byte[] owners, int x, int y, byte side)
        {
            if (!Board.IsOnGrid(x, y)) return 0;
            return owners[Board.CellId(x, y)] == side ? 1 : 0;
        }
    }

    /// <summary>What an overlay pixel shows on top of the per-cell ownership colours.</summary>
    public enum OverlayInk : byte
    {
        None = 0,
        /// <summary>The owner's pattern (A: diagonal hatch, B: dots), so ownership reads without colour.</summary>
        Pattern = 1,
        /// <summary>The rendered border contour.</summary>
        Contour = 2,
        /// <summary>Terrain marker glyph pixels.</summary>
        Terrain = 3,
    }

    /// <summary>
    /// High-resolution land overlay (ticket 41): owner patterns plus contour lines in a
    /// <see cref="Scale"/> x board-size mask, regenerated only when ownership changes. Pattern A is a
    /// diagonal hatch and pattern B a dot grid, so the two kingdoms are distinguishable with colour
    /// removed (greyscale or colour-vision differences).
    /// </summary>
    public sealed class OwnershipOverlay
    {
        public const int Scale = 4;
        public const int Size = Board.Size * Scale;
        /// <summary>Pattern period in overlay pixels.</summary>
        public const int Period = 6;

        public byte[] Ink { get; } = new byte[Size * Size];

        /// <summary>True when overlay pixel (px, py) (y down) carries the pattern ink of <paramref name="owner"/>.</summary>
        public static bool PatternInk(PlayerSide owner, int px, int py)
        {
            if (owner == PlayerSide.A) return (px + py) % Period == 0; // diagonal hatch
            return px % Period == 3 && py % Period == 3 || (px % Period == 4 && py % Period == 3) ||
                   (px % Period == 3 && py % Period == 4) || (px % Period == 4 && py % Period == 4); // 2x2 dots
        }

        /// <summary>Repaints from an ownership snapshot (never from or into the territory itself).</summary>
        public void Paint(byte[] owners, bool patterns, IReadOnlyList<ContourSegment> contour)
        {
            Array.Clear(Ink, 0, Ink.Length);
            if (patterns)
            {
                for (int py = 0; py < Size; py++)
                {
                    int cy = py / Scale;
                    for (int px = 0; px < Size; px++)
                    {
                        byte o = owners[Board.CellId(px / Scale, cy)];
                        if (o == OwnershipContours.Outside) continue;
                        if (PatternInk((PlayerSide)o, px, py)) Ink[TextureIndex(px, py)] = (byte)OverlayInk.Pattern;
                    }
                }
            }
            if (contour != null)
                foreach (ContourSegment s in contour) Line(s.X0 * Scale, s.Y0 * Scale, s.X1 * Scale, s.Y1 * Scale, OverlayInk.Contour);
        }

        /// <summary>Marks a terrain glyph (a small diamond) centred on a cell.</summary>
        public void MarkTerrain(int cellX, int cellY)
        {
            int cx = cellX * Scale + Scale / 2, cy = cellY * Scale + Scale / 2;
            for (int dy = -5; dy <= 5; dy++)
                for (int dx = -5; dx <= 5; dx++)
                    if (Math.Abs(dx) + Math.Abs(dy) == 5 || Math.Abs(dx) + Math.Abs(dy) == 4) Set(cx + dx, cy + dy, OverlayInk.Terrain);
        }

        public OverlayInk At(int px, int py) => (OverlayInk)Ink[TextureIndex(px, py)];

        /// <summary>Texture order (rows bottom-up) for a y-down overlay pixel.</summary>
        public static int TextureIndex(int px, int py) => (Size - 1 - py) * Size + px;

        private void Set(int px, int py, OverlayInk ink)
        {
            if ((uint)px < Size && (uint)py < Size) Ink[TextureIndex(px, py)] = (byte)ink;
        }

        private void Line(double x0, double y0, double x1, double y1, OverlayInk ink)
        {
            int steps = (int)Math.Ceiling(Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0))) + 1;
            for (int i = 0; i <= steps; i++)
            {
                double t = (double)i / steps;
                Set((int)Math.Floor(x0 + (x1 - x0) * t), (int)Math.Floor(y0 + (y1 - y0) * t), ink);
            }
        }
    }
}
