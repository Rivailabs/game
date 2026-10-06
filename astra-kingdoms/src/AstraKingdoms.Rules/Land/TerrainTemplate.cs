using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Land
{
    /// <summary>
    /// Immutable terrain ID per board cell. Inactive cells always hold Plain and are never read by
    /// the rules. Templates are identified by a stable ID so replays can name the map they used.
    /// </summary>
    public sealed class TerrainTemplate
    {
        private readonly TerrainType[] _terrain;
        private readonly int[] _counts;

        public string Id { get; }

        public TerrainTemplate(string id, TerrainType[] terrainPerCell)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("Template ID is required.", nameof(id));
            if (terrainPerCell == null) throw new ArgumentNullException(nameof(terrainPerCell));
            if (terrainPerCell.Length != Board.GridCellCount) throw new ArgumentException("Template must cover 65,536 grid cells.", nameof(terrainPerCell));

            Id = id;
            _terrain = (TerrainType[])terrainPerCell.Clone();
            _counts = new int[TerrainTemplates.CategoryCount];
            foreach (int cell in Board.ActiveCellIds)
            {
                TerrainType t = _terrain[cell];
                if ((int)t >= TerrainTemplates.CategoryCount) throw new ArgumentException("Unknown terrain ID at cell " + cell + ".", nameof(terrainPerCell));
                _counts[(int)t]++;
            }
        }

        public TerrainType this[int cellId] => _terrain[cellId];

        public TerrainType At(int x, int y) => _terrain[Board.CellId(x, y)];

        /// <summary>Number of active cells carrying the given terrain.</summary>
        public int Count(TerrainType terrain) => _counts[(int)terrain];

        /// <summary>Number of active cells of the given terrain inside a player's starting half.</summary>
        public int CountInStartingHalf(PlayerSide side, TerrainType terrain)
        {
            int n = 0;
            foreach (int cell in Board.ActiveCellIds)
            {
                if (Board.InitialOwner(Board.X(cell)) == side && _terrain[cell] == terrain) n++;
            }
            return n;
        }

        /// <summary>Copy of the per-cell table (length 65,536).</summary>
        public TerrainType[] ToArray() => (TerrainType[])_terrain.Clone();
    }

    /// <summary>
    /// Shipped terrain templates and their validator.
    /// <para>
    /// The Full-map template is produced by a deterministic, authored-style construction rather
    /// than random placement: a fixed list of hand-placed feature sites in A's starting half
    /// (x &lt; 128) is grown into blobs by taking the nearest still-unassigned cells, ordered by
    /// squared distance then cell ID. That fixes each category's count exactly. B's half is the
    /// mirror image across x = 127.5, so both starting halves hold 15,312 Plain and 2,552 of each
    /// special terrain. Several sites touch the centre line so the initial frontier offers
    /// special terrain. Changing a site changes the map and therefore needs a new template ID.
    /// </para>
    /// </summary>
    public static class TerrainTemplates
    {
        public const int CategoryCount = 5;

        public const string PlainId = "AK-TR-1/plain";
        public const string FullId = "AK-TR-1/full-01";

        /// <summary>Exact Full-map counts across the whole board.</summary>
        public const int FullPlainCells = 30624;
        public const int FullSpecialCellsEach = 5104;

        /// <summary>Exact Full-map counts in each starting half.</summary>
        public const int HalfPlainCells = 15312;
        public const int HalfSpecialCellsEach = 2552;

        private static readonly Lazy<TerrainTemplate> PlainLazy =
            new Lazy<TerrainTemplate>(() => new TerrainTemplate(PlainId, new TerrainType[Board.GridCellCount]));

        private static readonly Lazy<TerrainTemplate> FullLazy = new Lazy<TerrainTemplate>(BuildFull);

        /// <summary>All-Plain template used by the pilot and by default Starter rooms.</summary>
        public static TerrainTemplate PlainOnly => PlainLazy.Value;

        /// <summary>The mirrored V1 Full-map template with exact category counts.</summary>
        public static TerrainTemplate FullMirrored => FullLazy.Value;

        /// <summary>Template for a room catalog: Starter uses plain terrain, Full uses the mirrored map.</summary>
        public static TerrainTemplate ForCatalog(CatalogPreset preset) =>
            preset == CatalogPreset.Full ? FullMirrored : PlainOnly;

        // Feature sites in A's half: terrain, centre x, centre y, cell count. Per category the sizes
        // sum to 2,552. Sites are grown in list order; later sites skip cells already taken.
        private static readonly int[,] FullSites =
        {
            // Forts: four strongholds, one guarding the centre line.
            { (int)TerrainType.Fort, 121, 96, 638 },
            { (int)TerrainType.Fort, 40, 70, 638 },
            { (int)TerrainType.Fort, 30, 170, 638 },
            { (int)TerrainType.Fort, 92, 222, 638 },
            // River: eight overlapping pools along a winding course that reaches the centre line.
            { (int)TerrainType.River, 126, 140, 319 },
            { (int)TerrainType.River, 106, 146, 319 },
            { (int)TerrainType.River, 88, 136, 319 },
            { (int)TerrainType.River, 70, 126, 319 },
            { (int)TerrainType.River, 52, 120, 319 },
            { (int)TerrainType.River, 34, 128, 319 },
            { (int)TerrainType.River, 18, 138, 319 },
            { (int)TerrainType.River, 62, 22, 319 },
            // Forests: four woods, one touching the centre line.
            { (int)TerrainType.Forest, 122, 185, 638 },
            { (int)TerrainType.Forest, 80, 60, 638 },
            { (int)TerrainType.Forest, 60, 196, 638 },
            { (int)TerrainType.Forest, 12, 100, 638 },
            // Armouries: eight small caches.
            { (int)TerrainType.Armoury, 124, 40, 319 },
            { (int)TerrainType.Armoury, 125, 235, 319 },
            { (int)TerrainType.Armoury, 98, 105, 319 },
            { (int)TerrainType.Armoury, 95, 175, 319 },
            { (int)TerrainType.Armoury, 45, 40, 319 },
            { (int)TerrainType.Armoury, 25, 205, 319 },
            { (int)TerrainType.Armoury, 66, 160, 319 },
            { (int)TerrainType.Armoury, 104, 18, 319 },
        };

        private static TerrainTemplate BuildFull()
        {
            var terrain = new TerrainType[Board.GridCellCount];
            var assigned = new bool[Board.GridCellCount];

            // A's half, in canonical ascending ID order.
            var half = new List<int>(RulesConstants.InitialCellsPerPlayer);
            foreach (int cell in Board.ActiveCellIds)
            {
                if (Board.X(cell) < Board.Size / 2) half.Add(cell);
            }

            int sites = FullSites.GetLength(0);
            var keys = new long[half.Count];
            var order = new int[half.Count];
            for (int s = 0; s < sites; s++)
            {
                var type = (TerrainType)FullSites[s, 0];
                int cx = FullSites[s, 1];
                int cy = FullSites[s, 2];
                int size = FullSites[s, 3];

                // Sort by (squared distance, cell ID); IDs are < 2^16 so pack both into one key.
                for (int i = 0; i < half.Count; i++)
                {
                    int cell = half[i];
                    long dx = Board.X(cell) - cx;
                    long dy = Board.Y(cell) - cy;
                    keys[i] = ((dx * dx + dy * dy) << 16) | (long)cell;
                    order[i] = cell;
                }
                Array.Sort(keys, order);

                int taken = 0;
                for (int i = 0; i < order.Length && taken < size; i++)
                {
                    int cell = order[i];
                    if (assigned[cell]) continue;
                    assigned[cell] = true;
                    terrain[cell] = type;
                    taken++;
                }
                if (taken != size) throw new InvalidOperationException("Terrain site " + s + " could not be filled.");
            }

            // Mirror A's half onto B's half.
            foreach (int cell in half)
            {
                int mirror = Board.CellId(Board.MirrorX(Board.X(cell)), Board.Y(cell));
                terrain[mirror] = terrain[cell];
            }

            var template = new TerrainTemplate(FullId, terrain);
            IReadOnlyList<string> errors = Validate(template, requireFullCounts: true);
            if (errors.Count != 0) throw new InvalidOperationException("Generated Full template is invalid: " + errors[0]);
            return template;
        }

        /// <summary>
        /// Validates a template. Always checks that inactive cells are Plain and that the map is
        /// mirrored across x = 127.5 (equal starting halves). With <paramref name="requireFullCounts"/>
        /// it also checks the exact V1 Full-map counts on the board and in each half; otherwise it
        /// requires an all-Plain map (the pilot/Starter template). Returns an empty list when valid.
        /// </summary>
        public static IReadOnlyList<string> Validate(TerrainTemplate template, bool requireFullCounts)
        {
            if (template == null) throw new ArgumentNullException(nameof(template));
            var errors = new List<string>();

            for (int id = 0; id < Board.GridCellCount; id++)
            {
                if (!Board.IsActive(id) && template[id] != TerrainType.Plain)
                {
                    errors.Add("Inactive cell " + id + " carries non-Plain terrain.");
                    break;
                }
            }

            foreach (int cell in Board.ActiveCellIds)
            {
                int x = Board.X(cell);
                int y = Board.Y(cell);
                if (template[cell] != template.At(Board.MirrorX(x), y))
                {
                    errors.Add("Terrain at (" + x + "," + y + ") is not mirrored.");
                    break;
                }
            }

            for (int t = 0; t < CategoryCount; t++)
            {
                var type = (TerrainType)t;
                int expectedTotal;
                int expectedHalf;
                if (requireFullCounts)
                {
                    expectedTotal = type == TerrainType.Plain ? FullPlainCells : FullSpecialCellsEach;
                    expectedHalf = type == TerrainType.Plain ? HalfPlainCells : HalfSpecialCellsEach;
                }
                else
                {
                    expectedTotal = type == TerrainType.Plain ? Board.ActiveCellCount : 0;
                    expectedHalf = type == TerrainType.Plain ? RulesConstants.InitialCellsPerPlayer : 0;
                }

                if (template.Count(type) != expectedTotal)
                    errors.Add(type + " count " + template.Count(type) + " != " + expectedTotal + ".");
                foreach (PlayerSide side in new[] { PlayerSide.A, PlayerSide.B })
                {
                    int n = template.CountInStartingHalf(side, type);
                    if (n != expectedHalf) errors.Add(type + " count in half " + side + " is " + n + " != " + expectedHalf + ".");
                }
            }
            return errors;
        }
    }
}
