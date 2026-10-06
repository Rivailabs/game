using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Land
{
    /// <summary>
    /// Authoritative in-match ownership map: one owner and one terrain ID per active cell.
    /// <para>
    /// Ownership only changes through <see cref="Transfer"/>, which validates the whole set before
    /// mutating anything (atomic) and bumps <see cref="Revision"/>. Terrain is copied from the
    /// template at creation and is never written again; <see cref="CheckInvariants"/> verifies
    /// that, together with disjoint ownership, full coverage of the active board and
    /// area(A) + area(B) = 51,040.
    /// </para>
    /// </summary>
    public sealed class Territory
    {
        /// <summary>Owner byte stored for cells outside the circular board.</summary>
        private const byte Inactive = 0xFF;

        private readonly byte[] _owner;
        private readonly TerrainType[] _terrain;
        private readonly int[] _counts;

        /// <summary>The template this territory's terrain was copied from.</summary>
        public TerrainTemplate Template { get; }

        /// <summary>Increments once per successful transfer. Cuts are validated against a revision.</summary>
        public long Revision { get; private set; }

        private Territory(TerrainTemplate template, byte[] owner, TerrainType[] terrain, int[] counts, long revision)
        {
            Template = template;
            _owner = owner;
            _terrain = terrain;
            _counts = counts;
            Revision = revision;
        }

        /// <summary>Starting position: A owns active cells with x &lt; 128, B owns the rest (25,520 each).</summary>
        public static Territory CreateInitial(TerrainTemplate template)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));
            var owner = new byte[Board.GridCellCount];
            var terrain = template.ToArray();
            var counts = new int[2];
            for (int id = 0; id < Board.GridCellCount; id++)
            {
                if (!Board.IsActive(id))
                {
                    owner[id] = Inactive;
                    continue;
                }
                PlayerSide side = Board.InitialOwner(Board.X(id));
                owner[id] = (byte)side;
                counts[(int)side]++;
            }
            return new Territory(template, owner, terrain, counts, 0);
        }

        /// <summary>Deep copy, including revision. Useful for previews, bots and simulations.</summary>
        public Territory Clone() =>
            new Territory(Template, (byte[])_owner.Clone(), (TerrainType[])_terrain.Clone(), (int[])_counts.Clone(), Revision);

        /// <summary>Exact cell count owned by a player.</summary>
        public int CellCount(PlayerSide side) => _counts[(int)side];

        /// <summary>True when the player has reached the 90% shortcut (at least 45,936 cells).</summary>
        public bool HasReachedVictory(PlayerSide side) => _counts[(int)side] >= RulesConstants.VictoryCells;

        /// <summary>True when the cell is active and owned by <paramref name="side"/> (false off-board).</summary>
        public bool IsOwnedBy(int cellId, PlayerSide side) =>
            Board.IsValidCellId(cellId) && _owner[cellId] == (byte)side;

        public bool IsOwnedBy(int x, int y, PlayerSide side) => Board.IsOnGrid(x, y) && _owner[Board.CellId(x, y)] == (byte)side;

        /// <summary>Owner of an active cell. Throws for inactive or off-grid cells.</summary>
        public PlayerSide OwnerOf(int cellId)
        {
            if (!Board.IsActive(cellId)) throw new ArgumentOutOfRangeException(nameof(cellId), "Cell is not on the active board.");
            return (PlayerSide)_owner[cellId];
        }

        /// <summary>Terrain of a cell (Plain for inactive cells).</summary>
        public TerrainType TerrainAt(int cellId) => _terrain[cellId];

        /// <summary>
        /// True when <paramref name="cellId"/> is owned by the opponent of <paramref name="winner"/>
        /// and shares a full edge with a winner-owned cell. Diagonal contact is insufficient.
        /// </summary>
        public bool IsBorderAnchor(int cellId, PlayerSide winner)
        {
            PlayerSide loser = Board.Opponent(winner);
            if (!IsOwnedBy(cellId, loser)) return false;
            int x = Board.X(cellId);
            int y = Board.Y(cellId);
            for (int d = 0; d < 4; d++)
            {
                if (IsOwnedBy(x + Board.NeighbourDx[d], y + Board.NeighbourDy[d], winner)) return true;
            }
            return false;
        }

        /// <summary>
        /// Atomically moves every cell in <paramref name="cells"/> from <paramref name="from"/> to
        /// the other player. All cells must be distinct, active and owned by <paramref name="from"/>;
        /// otherwise nothing changes and an exception is thrown. An empty set is a no-op that does
        /// not change the revision.
        /// </summary>
        public void Transfer(IReadOnlyCollection<int> cells, PlayerSide from)
        {
            if (cells == null) throw new ArgumentNullException(nameof(cells));
            if (cells.Count == 0) return;
            PlayerSide to = Board.Opponent(from);

            // Validate everything first so a failure leaves ownership untouched.
            var seen = new HashSet<int>();
            foreach (int cell in cells)
            {
                if (!Board.IsActive(cell)) throw new ArgumentException("Cell " + cell + " is not on the active board.", nameof(cells));
                if (_owner[cell] != (byte)from) throw new ArgumentException("Cell " + cell + " is not owned by " + from + ".", nameof(cells));
                if (!seen.Add(cell)) throw new ArgumentException("Cell " + cell + " appears twice.", nameof(cells));
            }

            foreach (int cell in cells) _owner[cell] = (byte)to;
            _counts[(int)from] -= cells.Count;
            _counts[(int)to] += cells.Count;
            Revision++;
        }

        /// <summary>
        /// Recomputes ownership from scratch and checks every structural invariant. Returns null
        /// when all hold, otherwise a description of the first violation.
        /// </summary>
        public string FindInvariantViolation()
        {
            int a = 0;
            int b = 0;
            for (int id = 0; id < Board.GridCellCount; id++)
            {
                byte o = _owner[id];
                if (!Board.IsActive(id))
                {
                    if (o != Inactive) return "Inactive cell " + id + " has an owner.";
                    continue;
                }
                if (o == (byte)PlayerSide.A) a++;
                else if (o == (byte)PlayerSide.B) b++;
                else return "Active cell " + id + " has no valid owner."; // union must equal the active board
                if (_terrain[id] != Template[id]) return "Terrain of cell " + id + " changed.";
            }
            if (a != _counts[0] || b != _counts[1]) return "Cached counts disagree with ownership.";
            if (a < 0 || b < 0) return "Negative cell count.";
            if (a + b != Board.ActiveCellCount) return "area(A) + area(B) != 51,040.";
            return null;
        }

        /// <summary>Throws <see cref="InvalidOperationException"/> if any invariant fails.</summary>
        public void CheckInvariants()
        {
            string violation = FindInvariantViolation();
            if (violation != null) throw new InvalidOperationException("Territory invariant violated: " + violation);
        }

        /// <summary>
        /// SHA-256 over the 65,536 owner bytes (0 = A, 1 = B, 0xFF = inactive) in cell-ID order,
        /// suitable for replay state hashes.
        /// </summary>
        public byte[] ComputeOwnershipHash()
        {
            using (SHA256 sha = SHA256.Create())
            {
                return sha.ComputeHash(_owner);
            }
        }

        /// <summary>All cells currently owned by a player, ascending.</summary>
        public List<int> CellsOwnedBy(PlayerSide side)
        {
            var list = new List<int>(_counts[(int)side]);
            foreach (int id in Board.ActiveCellIds)
            {
                if (_owner[id] == (byte)side) list.Add(id);
            }
            return list;
        }
    }
}
