using System;
using System.Security.Cryptography;
using AstraKingdoms.Rules.Bots;

namespace AstraKingdoms.Client.Match
{
    /// <summary>Seeds and match IDs for local matches.</summary>
    public static class MatchFactory
    {
        /// <summary>A fresh secret 32-byte seed and canonical match UUID for a live local match.</summary>
        public static void NewLive(out byte[] seed, out string matchId)
        {
            seed = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(seed);
            matchId = Guid.NewGuid().ToString("D");
        }

        /// <summary>
        /// Deterministic seed and match ID for an automation/regression seed number. Uses the same
        /// derivation as the bot simulator (<see cref="BotMatchRunner.SeedFor"/>), index 0.
        /// </summary>
        public static void ForAutoplay(ulong seedNumber, out byte[] seed, out string matchId) =>
            BotMatchRunner.SeedFor(seedNumber, 0, out seed, out matchId);

        /// <summary>Deterministic request IDs for reproducible automation records.</summary>
        public static Func<string> DeterministicRequestIds(ulong seedNumber)
        {
            var rng = new BotRng(seedNumber ^ 0x5EED_1D5UL);
            return rng.NextUuid;
        }
    }
}
