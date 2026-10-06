using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Rules.Bots
{
    /// <summary>
    /// Small deterministic PRNG (SplitMix64) for bot policy decisions. Bots are not authoritative,
    /// but they must still be reproducible from the match seed, so System.Random is never used.
    /// </summary>
    public sealed class BotRng
    {
        private ulong _state;

        public BotRng(ulong seed)
        {
            _state = seed;
        }

        /// <summary>Seeds from the 32-byte match seed, the bot's seat and a policy salt.</summary>
        public static BotRng FromMatchSeed(byte[] matchSeed, PlayerSide side, ulong salt)
        {
            if (matchSeed == null || matchSeed.Length < 8) throw new ArgumentException("Seed must have at least 8 bytes.", nameof(matchSeed));
            ulong s = 0x6A09E667F3BCC908UL ^ salt ^ ((ulong)side + 1) * 0x9E3779B97F4A7C15UL;
            for (int i = 0; i < matchSeed.Length; i++)
            {
                s ^= (ulong)matchSeed[i] << (8 * (i % 8));
                if (i % 8 == 7) s = Mix(s);
            }
            return new BotRng(Mix(s));
        }

        public ulong NextULong()
        {
            _state = unchecked(_state + 0x9E3779B97F4A7C15UL);
            return Mix(_state);
        }

        private static ulong Mix(ulong z)
        {
            unchecked
            {
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        /// <summary>Integer in [0, n) (negligible modulo bias is acceptable for policy noise).</summary>
        public int Next(int n)
        {
            if (n <= 0) throw new ArgumentOutOfRangeException(nameof(n));
            return (int)(NextULong() % (ulong)n);
        }

        /// <summary>Integer in [min, max] inclusive.</summary>
        public int Range(int min, int max) => min + Next(max - min + 1);

        /// <summary>True with probability percent/100.</summary>
        public bool Chance(int percent) => Next(100) < percent;

        public T Pick<T>(IReadOnlyList<T> list) => list[Next(list.Count)];

        /// <summary>A canonical lowercase UUID (version-4 layout) for request IDs.</summary>
        public string NextUuid()
        {
            ulong hi = NextULong();
            ulong lo = NextULong();
            hi = (hi & 0xFFFFFFFFFFFF0FFFUL) | 0x0000000000004000UL;
            lo = (lo & 0x3FFFFFFFFFFFFFFFUL) | 0x8000000000000000UL;
            string h = hi.ToString("x16");
            string l = lo.ToString("x16");
            return h.Substring(0, 8) + "-" + h.Substring(8, 4) + "-" + h.Substring(12, 4) + "-" + l.Substring(0, 4) + "-" + l.Substring(4, 12);
        }
    }

    /// <summary>Bot difficulty. Difficulty changes accuracy and planning only; never information or damage.</summary>
    public enum BotDifficulty : byte
    {
        /// <summary>Random weapon and dodge, large aim error, small cut.</summary>
        Easy = 0,
        /// <summary>Partly counter-picks the last revealed element, moderate aim error, small cut search.</summary>
        Normal = 1,
        /// <summary>Exact aim search vs the opponent's baseline pose, history-based counter-elements, wide cut search.</summary>
        Hard = 2,
    }

    /// <summary>
    /// Everything a bot may observe (ticket 21): exactly one player's private <see cref="PlayerView"/>.
    /// There is deliberately no constructor taking an engine, duel state or opponent input, so a
    /// policy has no path to an unrevealed choice; the opponent's lock is visible only as a ready flag.
    /// </summary>
    public sealed class BotObservation
    {
        public PlayerView View { get; }

        public BotObservation(PlayerView view)
        {
            View = view ?? throw new ArgumentNullException(nameof(view));
        }

        public PlayerSide Self => View.Viewer;
        public PlayerSide Opponent => View.Opponent;
    }

    /// <summary>A planned cut (the SubmitCut payload without its header).</summary>
    public sealed class CutPlan
    {
        public CardId Card;
        public int AnchorCellId;
        public CardPose Pose;
        /// <summary>Cells the plan transfers according to the bot's own preview.</summary>
        public int ExpectedCells;
    }

    /// <summary>A bot policy: decisions from an observation only.</summary>
    public interface IBotPolicy
    {
        BotDifficulty Difficulty { get; }
        /// <summary>Weapons (and reserve, 0 for none) for <see cref="SubmitLoadoutCommand"/>.</summary>
        void ChooseLoadout(BotObservation observation, out int[] weapons, out int reserve);
        /// <summary>A legal choice for the open volley.</summary>
        VolleyInput ChooseVolley(BotObservation observation);
        /// <summary>A cut for the winner's open cut window, or null to let it time out.</summary>
        CutPlan ChooseCut(BotObservation observation);
    }
}
