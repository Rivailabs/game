using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;

namespace AstraKingdoms.Client.Land
{
    /// <summary>What a board pixel shows. Each pixel is exactly one logical cell.</summary>
    public enum CellPaint : byte
    {
        Outside = 0,
        OwnerA = 1,
        OwnerB = 2,
        /// <summary>Inside the posed card envelope (the visible legal boundary), owned by A.</summary>
        EnvelopeA = 3,
        /// <summary>Inside the posed card envelope, owned by B.</summary>
        EnvelopeB = 4,
        /// <summary>Exact cells the current cut would transfer.</summary>
        Preview = 5,
        /// <summary>Eligible fragments not connected to the anchor (shown as unclaimed).</summary>
        Discarded = 6,
        Anchor = 7,
        /// <summary>The simplified cut polygon's outline.</summary>
        Stroke = 8,
        /// <summary>Preview cells of a rejected cut (for example over the allowance).</summary>
        Rejected = 9,
    }

    /// <summary>
    /// Paints the authoritative 256x256 ownership map into a per-pixel <see cref="CellPaint"/> buffer
    /// in texture order (rows bottom-up). The Unity layer maps paints to colours. Area is always the
    /// exact cell count, never a pixel estimate of a smoothed outline.
    /// </summary>
    public sealed class BoardRaster
    {
        public const int Size = Board.Size;
        public const int PixelCount = Size * Size;

        public byte[] Paint { get; } = new byte[PixelCount];

        public void PaintOwnership(Territory territory)
        {
            if (territory == null) throw new ArgumentNullException(nameof(territory));
            Array.Clear(Paint, 0, PixelCount);
            foreach (int id in Board.ActiveCellIds)
            {
                int x = Board.X(id), y = Board.Y(id);
                Paint[BoardMapping.TextureIndex(x, y)] = (byte)(territory.IsOwnedBy(id, PlayerSide.A) ? CellPaint.OwnerA : CellPaint.OwnerB);
            }
        }

        public void OverlayEnvelope(IReadOnlyList<int> envelopeCells)
        {
            if (envelopeCells == null) return;
            foreach (int id in envelopeCells)
            {
                int i = BoardMapping.TextureIndex(Board.X(id), Board.Y(id));
                if (Paint[i] == (byte)CellPaint.OwnerA) Paint[i] = (byte)CellPaint.EnvelopeA;
                else if (Paint[i] == (byte)CellPaint.OwnerB) Paint[i] = (byte)CellPaint.EnvelopeB;
            }
        }

        public void OverlayCells(IReadOnlyList<int> cells, CellPaint paint)
        {
            if (cells == null) return;
            foreach (int id in cells)
                if (Board.IsActive(id)) Paint[BoardMapping.TextureIndex(Board.X(id), Board.Y(id))] = (byte)paint;
        }

        /// <summary>Draws polygon edges between snapped vertices (Bresenham), closing the path when asked.</summary>
        public void OverlayPolyline(IReadOnlyList<CellPoint> vertices, bool closed)
        {
            if (vertices == null || vertices.Count == 0) return;
            for (int i = 0; i + 1 < vertices.Count; i++) Line(vertices[i], vertices[i + 1]);
            if (closed && vertices.Count > 2) Line(vertices[vertices.Count - 1], vertices[0]);
            if (vertices.Count == 1) Plot(vertices[0].X, vertices[0].Y);
        }

        public int Count(CellPaint paint)
        {
            int n = 0;
            byte p = (byte)paint;
            for (int i = 0; i < PixelCount; i++)
                if (Paint[i] == p) n++;
            return n;
        }

        public CellPaint At(int x, int y) => (CellPaint)Paint[BoardMapping.TextureIndex(x, y)];

        private void Line(CellPoint a, CellPoint b)
        {
            int x0 = a.X, y0 = a.Y, x1 = b.X, y1 = b.Y;
            int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            while (true)
            {
                Plot(x0, y0);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        private void Plot(int x, int y)
        {
            if (Board.IsOnGrid(x, y)) Paint[BoardMapping.TextureIndex(x, y)] = (byte)CellPaint.Stroke;
        }
    }
}
