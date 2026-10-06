using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Land
{
    /// <summary>The duel terrain chosen before a round, with the data a replay records.</summary>
    public readonly struct FrontierSelection
    {
        /// <summary>The selected defender-owned frontier cell.</summary>
        public readonly int CellId;

        /// <summary>That cell's terrain, which becomes the duel terrain.</summary>
        public readonly TerrainType Terrain;

        /// <summary>Length of the canonical frontier list the selection was drawn from.</summary>
        public readonly int FrontierCount;

        /// <summary>Terrain-stream counter after the selection (number of digests consumed).</summary>
        public readonly uint StreamCounter;

        public FrontierSelection(int cellId, TerrainType terrain, int frontierCount, uint streamCounter)
        {
            CellId = cellId;
            Terrain = terrain;
            FrontierCount = frontierCount;
            StreamCounter = streamCounter;
        }

        public CellPoint Cell => CellPoint.FromCellId(CellId);
    }

    /// <summary>
    /// Duel terrain selection. Before each duel the rules pick a defender-owned cell that shares a
    /// full edge with attacker-owned land, from the list sorted by cell ID, using the
    /// "AK-TR-1/terrain" stream for that round.
    /// </summary>
    public static class Frontier
    {
        /// <summary>Defender-owned cells edge-adjacent to attacker land, in ascending cell ID.</summary>
        public static List<int> Cells(Territory territory, PlayerSide attacker)
        {
            if (territory == null) throw new ArgumentNullException(nameof(territory));
            var list = new List<int>();
            foreach (int cell in Board.ActiveCellIds)
            {
                if (territory.IsBorderAnchor(cell, attacker)) list.Add(cell);
            }
            return list;
        }

        /// <summary>Selects the duel terrain for <paramref name="round"/> (1-8).</summary>
        public static FrontierSelection SelectDuelTerrain(Territory territory, PlayerSide attacker, byte[] seed, int round) =>
            SelectDuelTerrain(territory, attacker, seed, round, RulesConstants.MaxRounds);

        /// <summary>Selects the duel terrain for <paramref name="round"/> (1..<paramref name="maxRounds"/>, ticket 24).</summary>
        public static FrontierSelection SelectDuelTerrain(Territory territory, PlayerSide attacker, byte[] seed, int round, int maxRounds)
        {
            if (round < 1 || round > maxRounds) throw new ArgumentOutOfRangeException(nameof(round));
            List<int> frontier = Cells(territory, attacker);
            if (frontier.Count == 0) throw new InvalidOperationException("No frontier: one player owns no land.");

            SeededStream stream = SeededStream.Terrain(seed, (uint)round);
            int cell = stream.Select(frontier);
            return new FrontierSelection(cell, territory.TerrainAt(cell), frontier.Count, stream.Counter);
        }
    }
}
