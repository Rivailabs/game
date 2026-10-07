using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Land
{
    /// <summary>
    /// Integer cell coordinate on the 256x256 board. x grows to the right and y grows downward
    /// (the displayed map is y-down), so "North" is y - 1. Also used for cut vertices, which are
    /// snapped cell-centre coordinates.
    /// </summary>
    public readonly struct CellPoint : IEquatable<CellPoint>
    {
        public readonly int X;
        public readonly int Y;

        public CellPoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public bool IsOnGrid => Board.IsOnGrid(X, Y);

        /// <summary>Cell ID y*256+x. Only meaningful when <see cref="IsOnGrid"/>.</summary>
        public int CellId => Board.CellId(X, Y);

        public static CellPoint FromCellId(int cellId) => new CellPoint(Board.X(cellId), Board.Y(cellId));

        public bool Equals(CellPoint other) => X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is CellPoint p && Equals(p);
        public override int GetHashCode() => (X * 397) ^ Y;
        public static bool operator ==(CellPoint a, CellPoint b) => a.Equals(b);
        public static bool operator !=(CellPoint a, CellPoint b) => !a.Equals(b);
        public override string ToString() => "(" + X + "," + Y + ")";
    }

    /// <summary>
    /// The fixed circular board of AK-TR-1: a 256x256 integer grid whose active cells satisfy
    /// (2x-255)^2 + (2y-255)^2 &lt;= 255^2. There are exactly 51,040 active cells. Cell IDs are
    /// y*256+x, so ascending cell ID is row-major canonical order.
    /// </summary>
    public static class Board
    {
        public const int Size = RulesConstants.BoardSize;
        public const int GridCellCount = Size * Size;
        public const int ActiveCellCount = RulesConstants.ActiveCells;

        /// <summary>Neighbour offsets in the fixed order North (y-1), East (x+1), South (y+1), West (x-1).</summary>
        public static readonly IReadOnlyList<int> NeighbourDx = new[] { 0, 1, 0, -1 };
        public static readonly IReadOnlyList<int> NeighbourDy = new[] { -1, 0, 1, 0 };

        private static readonly bool[] ActiveMask = BuildMask();
        private static readonly int[] ActiveIds = BuildActiveIds();

        /// <summary>All active cell IDs in ascending order.</summary>
        public static IReadOnlyList<int> ActiveCellIds => ActiveIds;

        public static int CellId(int x, int y) => y * Size + x;
        public static int X(int cellId) => cellId % Size;
        public static int Y(int cellId) => cellId / Size;

        public static bool IsOnGrid(int x, int y) => (uint)x < Size && (uint)y < Size;
        public static bool IsValidCellId(int cellId) => (uint)cellId < GridCellCount;

        /// <summary>Exact circular mask test for integer coordinates (false off the grid).</summary>
        public static bool IsActive(int x, int y)
        {
            if (!IsOnGrid(x, y)) return false;
            return ActiveMask[CellId(x, y)];
        }

        public static bool IsActive(int cellId) => IsValidCellId(cellId) && ActiveMask[cellId];

        /// <summary>The mask formula itself, independent of the cached table.</summary>
        public static bool ComputeActive(int x, int y)
        {
            int a = 2 * x - 255;
            int b = 2 * y - 255;
            return a * a + b * b <= 255 * 255;
        }

        /// <summary>Initial owner: A owns active cells with x &lt; 128, B owns the remainder.</summary>
        public static PlayerSide InitialOwner(int x) => x < Size / 2 ? PlayerSide.A : PlayerSide.B;

        /// <summary>Mirror of x across the board's vertical centre line x = 127.5.</summary>
        public static int MirrorX(int x) => Size - 1 - x;

        public static PlayerSide Opponent(PlayerSide side) => side == PlayerSide.A ? PlayerSide.B : PlayerSide.A;

        private static bool[] BuildMask()
        {
            var mask = new bool[GridCellCount];
            int count = 0;
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    if (ComputeActive(x, y))
                    {
                        mask[CellId(x, y)] = true;
                        count++;
                    }
                }
            }
            if (count != ActiveCellCount) throw new InvalidOperationException("Board mask does not contain 51,040 cells.");
            return mask;
        }

        private static int[] BuildActiveIds()
        {
            var ids = new int[ActiveCellCount];
            int n = 0;
            for (int id = 0; id < GridCellCount; id++)
            {
                if (ActiveMask[id]) ids[n++] = id;
            }
            return ids;
        }
    }
}
