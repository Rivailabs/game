using System;
using System.Security.Cryptography;
using System.Text;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.World.Challenges
{
    /// <summary>The verified result of one encounter, ready for <see cref="WorldShard.Resolve"/>.</summary>
    public sealed class EncounterResult
    {
        /// <summary>SHA-256 (hex) of the canonical match record: the resolution ID.</summary>
        public string ResolutionId { get; internal set; }
        public EncounterOutcome Outcome { get; internal set; }
        public string MatchId { get; internal set; }
        public string RecordJson { get; internal set; }
        public MatchResult MatchResult { get; internal set; }
    }

    /// <summary>
    /// Runs a border encounter as an ordinary, unmodified AK-TR-1 Full match (every weapon loaned to
    /// both seats): the attacker in seat A, the defender in seat B. With automatic defence the
    /// defender seat is the existing <see cref="BotPlayer"/> driven by the published loadout and bot
    /// policy (<see cref="PublishedDefencePolicy"/>); it only ever receives the engine's private
    /// <see cref="PlayerView"/> for seat B, so it sees legal observations and never the attacker's
    /// hidden selection. The mode is taken from the challenge snapshot: a live defence is never taken
    /// over by a bot mid-encounter. Encounter statistics stay in the world: nothing feeds back into
    /// V1 duel statistics or equipment.
    /// </summary>
    public static class OfflineEncounter
    {
        public const string SeedLabel = "AK-W4-0/encounter";

        /// <summary>Deterministic 32-byte encounter seed from a server secret and the challenge ID.</summary>
        public static byte[] EncounterSeed(byte[] serverSecret, string challengeId)
        {
            if (serverSecret == null || serverSecret.Length < 16) throw new ArgumentException("Server secret must have at least 16 bytes.", nameof(serverSecret));
            var w = new CanonicalWriter();
            w.Ascii(SeedLabel).Block(serverSecret).Ascii(challengeId);
            return w.Sha256();
        }

        /// <summary>Canonical match UUID derived from the encounter seed.</summary>
        public static string EncounterMatchId(byte[] seed)
        {
            string h = Hex.Encode(new CanonicalWriter().Ascii("AK-W4-0/match").Block(seed).Sha256());
            char variant = "89ab"[h[16] % 4];
            return h.Substring(0, 8) + "-" + h.Substring(8, 4) + "-4" + h.Substring(13, 3) + "-" + variant + h.Substring(17, 3) + "-" + h.Substring(20, 12);
        }

        /// <summary>The automatic defender seat for a challenge (throws for a live defence).</summary>
        public static BotPlayer DefenderSeat(Challenge challenge, byte[] encounterSeed)
        {
            if (challenge == null) throw new ArgumentNullException(nameof(challenge));
            if (challenge.DefenceSnapshot.Mode != DefenceMode.Automatic)
                throw new InvalidOperationException("Live defence was chosen before the snapshot; no bot takes over.");
            var policy = new PublishedDefencePolicy(challenge.DefenceSnapshot, BotRng.FromMatchSeed(encounterSeed, PlayerSide.B, 0xDEF0_0001UL));
            return new BotPlayer(PlayerSide.B, policy, BotRng.FromMatchSeed(encounterSeed, PlayerSide.B, 0xDEF0_0002UL));
        }

        /// <summary>
        /// Plays an automatic-defence encounter to completion with an attacker seat (a bot here; a
        /// live attacker would submit its own commands to the same engine), verifies the record by
        /// replay and maps the AK-TR-1 result: A wins = attacker wins, B wins = defender wins,
        /// draw or void = no transfer.
        /// </summary>
        public static EncounterResult RunAutomatic(Challenge challenge, byte[] serverSecret, Func<byte[], BotPlayer> attackerSeat)
        {
            if (attackerSeat == null) throw new ArgumentNullException(nameof(attackerSeat));
            byte[] seed = EncounterSeed(serverSecret, challenge.ChallengeId);
            string matchId = EncounterMatchId(seed);
            BotPlayer defender = DefenderSeat(challenge, seed);
            BotPlayer attacker = attackerSeat(seed);
            if (attacker.Side != PlayerSide.A) throw new ArgumentException("The attacker sits in seat A.");

            MatchEngine engine = MatchEngine.Create(MatchConfig.V1Full(MatchMode.Online), seed, matchId);
            BotMatchRunner.Play(engine, seed, attacker, defender);
            MatchRecord record = MatchRecord.FromEngine(engine);
            ReplayReport replay = Replayer.Verify(record);
            if (!replay.Success) throw new InvalidOperationException("Encounter record failed verification: " + replay);

            string json = record.ToJson();
            string id;
            using (SHA256 sha = SHA256.Create())
            {
                id = Hex.Encode(sha.ComputeHash(Encoding.UTF8.GetBytes(json)));
            }
            MatchResult r = engine.Result;
            EncounterOutcome outcome = r.Winner == PlayerSide.A && !r.IsVoid ? EncounterOutcome.AttackerWins
                : r.Winner == PlayerSide.B && !r.IsVoid ? EncounterOutcome.DefenderWins
                : EncounterOutcome.Draw;
            return new EncounterResult { ResolutionId = id, Outcome = outcome, MatchId = matchId, RecordJson = json, MatchResult = r };
        }
    }
}
