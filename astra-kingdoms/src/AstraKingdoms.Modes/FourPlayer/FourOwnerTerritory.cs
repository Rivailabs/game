using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using AstraKingdoms.Rules.Land;

namespace AstraKingdoms.Modes.FourPlayer
{
    /// <summary>One owner-to-owner move of an exact cell set, applied as part of a batch.</summary>
    public sealed class CellTransfer
    {
        public Kingdom From { get; }
        public Kingdom To { get; }
        public IReadOnlyList<int> Cells { get; }

        public CellTransfer(Kingdom from, Kingdom to, IReadOnlyList<int> cells)
        {
            if (from == to) throw new ArgumentException("A transfer needs two different kingdoms.");
            From = from;
            To = to;
            Cells = cells ?? throw new ArgumentNullException(nameof(cells));
        }
    }

    /// <summary>
    /// Four-owner ownership map for the AK-4P-0 candidate. It reuses the AK-TR-1 board (the same
    /// 51,040 active cells and cell IDs) but is a separate type: the two-player
    /// <see cref="Territory"/> is not modified.
    /// <para>
    /// Every active cell is owned by exactly one of A-D or is <b>locked neutral</b> (land of a
    /// forfeited player, which nobody can cut for the rest of the match). Ownership changes only
    /// through <see cref="ApplyTransfers"/> (an atomic, validated batch) and
    /// <see cref="LockToNeutral"/>; both bump <see cref="Revision"/>. The invariant checked by
    /// <see cref="FindInvariantViolation"/> is area(A)+area(B)+area(C)+area(D)+area(neutral) = 51,040.
    /// </para>
    /// </summary>
    public sealed class FourOwnerTerritory
    {
        /// <summary>Owner byte for locked neutral land.</summary>
        public const byte NeutralOwner = 4;

        /// <summary>Owner byte stored for cells outside the circular board.</summary>
        public const byte Inactive = 0xFF;

        private const int OwnerSlots = 5; // A, B, C, D, neutral

        private readonly byte[] _owner;
        private readonly int[] _counts;

        public long Revision { get; private set; }

        private FourOwnerTerritory(byte[] owner, int[] counts, long revision)
        {
            _owner = owner;
            _counts = counts;
            Revision = revision;
        }

        /// <summary>
        /// The starting position: four mirror-image quarters split at x = 128 and y = 128. The board
        /// mask is symmetric under x → 255 − x and y → 255 − y, so every quarter holds exactly 12,760 cells.
        /// </summary>
        public static FourOwnerTerritory CreateEqualSectors()
        {
            var owner = new byte[Board.GridCellCount];
            var counts = new int[OwnerSlots];
            for (int id = 0; id < Board.GridCellCount; id++)
            {
                if (!Board.IsActive(id))
                {
                    owner[id] = Inactive;
                    continue;
                }
                Kingdom k = InitialSector(Board.X(id), Board.Y(id));
                owner[id] = (byte)k;
                counts[(int)k]++;
            }
            var t = new FourOwnerTerritory(owner, counts, 0);
            t.CheckInvariants();
            return t;
        }

        /// <summary>A = x&lt;128,y&lt;128; B = x≥128,y&lt;128; C = x≥128,y≥128; D = x&lt;128,y≥128.</summary>
        public static Kingdom InitialSector(int x, int y)
        {
            bool east = x >= Board.Size / 2;
            bool south = y >= Board.Size / 2;
            if (!south) return east ? Kingdom.B : Kingdom.A;
            return east ? Kingdom.C : Kingdom.D;
        }

        public FourOwnerTerritory Clone() => new FourOwnerTerritory((byte[])_owner.Clone(), (int[])_counts.Clone(), Revision);

        public int CellCount(Kingdom k) => _counts[(int)k];

        public int NeutralCellCount => _counts[NeutralOwner];

        /// <summary>True when the cell is active and owned by <paramref name="k"/>.</summary>
        public bool IsOwnedBy(int cellId, Kingdom k) => Board.IsValidCellId(cellId) && _owner[cellId] == (byte)k;

        public bool IsOwnedBy(int x, int y, Kingdom k) => Board.IsOnGrid(x, y) && _owner[Board.CellId(x, y)] == (byte)k;

        public bool IsNeutral(int cellId) => Board.IsValidCellId(cellId) && _owner[cellId] == NeutralOwner;

        /// <summary>Raw owner byte: 0-3 for A-D, 4 for locked neutral, 0xFF for inactive cells.</summary>
        public byte OwnerByte(int cellId) => _owner[cellId];

        /// <summary>The owner of an active cell, or null when it is locked neutral.</summary>
        public Kingdom? OwnerOf(int cellId)
        {
            if (!Board.IsActive(cellId)) throw new ArgumentOutOfRangeException(nameof(cellId), "Cell is not on the active board.");
            byte o = _owner[cellId];
            return o == NeutralOwner ? (Kingdom?)null : (Kingdom)o;
        }

        /// <summary>
        /// Applies every transfer of a batch atomically. All cells must be active, distinct across the
        /// whole batch and owned by the transfer's <see cref="CellTransfer.From"/>; otherwise nothing
        /// changes and an exception is thrown. Neutral land can never be a source or destination here.
        /// A batch with no cells is a no-op that keeps the revision.
        /// </summary>
        public void ApplyTransfers(IReadOnlyList<CellTransfer> transfers)
        {
            if (transfers == null) throw new ArgumentNullException(nameof(transfers));
            var seen = new HashSet<int>();
            int total = 0;
            foreach (CellTransfer t in transfers)
            {
                if (t == null) throw new ArgumentException("Null transfer.", nameof(transfers));
                foreach (int cell in t.Cells)
                {
                    if (!Board.IsActive(cell)) throw new ArgumentException("Cell " + cell + " is not on the active board.", nameof(transfers));
                    if (_owner[cell] != (byte)t.From) throw new ArgumentException("Cell " + cell + " is not owned by " + t.From + ".", nameof(transfers));
                    if (!seen.Add(cell)) throw new ArgumentException("Cell " + cell + " appears in two transfers (transfers must be disjoint).", nameof(transfers));
                    total++;
                }
            }
            if (total == 0) return;

            foreach (CellTransfer t in transfers)
            {
                foreach (int cell in t.Cells) _owner[cell] = (byte)t.To;
                _counts[(int)t.From] -= t.Cells.Count;
                _counts[(int)t.To] += t.Cells.Count;
            }
            Revision++;
            CheckInvariants();
        }

        /// <summary>
        /// Turns every cell of <paramref name="k"/> into locked neutral land (a forfeit). Returns the
        /// number of cells locked. The land goes to nobody: no automatic windfall.
        /// </summary>
        public int LockToNeutral(Kingdom k)
        {
            int n = _counts[(int)k];
            if (n == 0) return 0;
            foreach (int id in Board.ActiveCellIds)
                if (_owner[id] == (byte)k) _owner[id] = NeutralOwner;
            _counts[NeutralOwner] += n;
            _counts[(int)k] = 0;
            Revision++;
            CheckInvariants();
            return n;
        }

        /// <summary>All cells currently owned by <paramref name="k"/>, ascending.</summary>
        public List<int> CellsOwnedBy(Kingdom k)
        {
            var list = new List<int>(_counts[(int)k]);
            foreach (int id in Board.ActiveCellIds)
                if (_owner[id] == (byte)k) list.Add(id);
            return list;
        }

        /// <summary>
        /// Recomputes ownership and checks: inactive cells unowned, every active cell owned by A-D or
        /// neutral, cached counts exact, and the five areas summing to 51,040. Null when all hold.
        /// </summary>
        public string FindInvariantViolation()
        {
            var counts = new int[OwnerSlots];
            for (int id = 0; id < Board.GridCellCount; id++)
            {
                byte o = _owner[id];
                if (!Board.IsActive(id))
                {
                    if (o != Inactive) return "Inactive cell " + id + " has an owner.";
                    continue;
                }
                if (o >= OwnerSlots) return "Active cell " + id + " has no valid owner.";
                counts[o]++;
            }
            int sum = 0;
            for (int i = 0; i < OwnerSlots; i++)
            {
                if (counts[i] != _counts[i]) return "Cached count of owner " + i + " disagrees with ownership.";
                sum += counts[i];
            }
            if (sum != Board.ActiveCellCount) return "Areas of A-D and neutral do not sum to 51,040.";
            return null;
        }

        public void CheckInvariants()
        {
            string v = FindInvariantViolation();
            if (v != null) throw new InvalidOperationException("Four-owner territory invariant violated: " + v);
        }

        /// <summary>SHA-256 over the 65,536 owner bytes in cell-ID order.</summary>
        public byte[] ComputeOwnershipHash()
        {
            using (SHA256 sha = SHA256.Create())
            {
                return sha.ComputeHash(_owner);
            }
        }

        /// <summary>A copy of the owner bytes (public board state for spectators and clients).</summary>
        public byte[] ToOwnerBytes() => (byte[])_owner.Clone();
    }
}
