using System;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Rules.Bots
{
    /// <summary>Thrown when a bot produces a command the authoritative engine rejects.</summary>
    public sealed class BotIllegalCommandException : Exception
    {
        public CommandReceipt Receipt { get; }

        public BotIllegalCommandException(CommandReceipt receipt) : base("Bot command rejected: " + receipt)
        {
            Receipt = receipt;
        }
    }

    /// <summary>
    /// Plays a whole match between two bot seats against a <see cref="MatchEngine"/>, acting as the
    /// host: whenever neither seat has anything to submit, it issues the server
    /// <see cref="AdvancePhaseCommand"/> (announcement end, replay end, or a cut window timeout when
    /// a winner declines to cut). Bots never time out a selection here.
    /// </summary>
    public static class BotMatchRunner
    {
        public static MatchEngine Run(MatchConfig config, byte[] seed, string matchId, BotPlayer botA, BotPlayer botB, int maxSteps = 1000)
        {
            MatchEngine engine = MatchEngine.Create(config, seed, matchId);
            Play(engine, seed, botA, botB, maxSteps);
            return engine;
        }

        /// <summary>Drives an existing engine to completion.</summary>
        public static void Play(MatchEngine engine, byte[] seed, BotPlayer botA, BotPlayer botB, int maxSteps = 1000)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));
            if (botA.Side != PlayerSide.A || botB.Side != PlayerSide.B) throw new ArgumentException("Bots must sit in seats A and B.");
            BotRng server = BotRng.FromMatchSeed(seed, PlayerSide.A, 0x5E4E4UL);
            for (int step = 0; step < maxSteps && !engine.IsOver; step++)
            {
                bool acted = Act(engine, botA) | Act(engine, botB);
                if (acted) continue;
                CommandReceipt r = engine.Advance(engine.CreateAdvance(server.NextUuid()));
                if (!r.Accepted) throw new InvalidOperationException("Server advance rejected: " + r);
            }
            if (!engine.IsOver) throw new InvalidOperationException("Match did not finish within " + maxSteps + " steps.");
        }

        private static bool Act(MatchEngine engine, BotPlayer bot)
        {
            if (engine.IsOver) return false;
            MatchCommand cmd = bot.Decide(engine.GetView(bot.Side));
            if (cmd == null) return false;
            CommandReceipt receipt = engine.Submit(bot.Side, cmd);
            if (!receipt.Accepted) throw new BotIllegalCommandException(receipt);
            return true;
        }

        /// <summary>Deterministic 32-byte seed and canonical match UUID for simulation index <paramref name="n"/>.</summary>
        public static void SeedFor(ulong baseSeed, long n, out byte[] seed, out string matchId)
        {
            var rng = new BotRng(baseSeed ^ unchecked((ulong)n * 0xD1B54A32D192ED03UL));
            seed = new byte[32];
            for (int i = 0; i < 4; i++)
            {
                ulong v = rng.NextULong();
                for (int b = 0; b < 8; b++) seed[i * 8 + b] = (byte)(v >> (8 * b));
            }
            matchId = rng.NextUuid();
        }
    }
}
