using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Rules.Replay
{
    public enum ReplayFailure : byte
    {
        None = 0,
        /// <summary>The record names another rules version, or its rules hash differs from this build's.</summary>
        IncompatibleRules = 1,
        /// <summary>The record cannot start a match (bad seed, match ID or config).</summary>
        Malformed = 2,
        SeedCommitmentMismatch = 3,
        InitiativeMismatch = 4,
        /// <summary>A recorded command was not accepted on re-execution.</summary>
        CommandRejected = 5,
        RoundMismatch = 6,
        ResultMismatch = 7,
    }

    /// <summary>Outcome of <see cref="Replayer.Verify"/>.</summary>
    public sealed class ReplayReport
    {
        public ReplayFailure Failure { get; }
        public string Detail { get; }
        /// <summary>The re-executed engine (null when replay could not start).</summary>
        public MatchEngine Engine { get; }
        public bool Success => Failure == ReplayFailure.None;

        internal ReplayReport(ReplayFailure failure, string detail, MatchEngine engine)
        {
            Failure = failure;
            Detail = detail;
            Engine = engine;
        }

        public override string ToString() => Success ? "Replay verified" : "Replay failed: " + Failure + " - " + Detail;
    }

    /// <summary>
    /// Re-executes a <see cref="MatchRecord"/> from its seed and command log and checks the seed
    /// commitment, initiative, every round's stream counters, volley log hashes and state hash, and
    /// the final result. Records of another rules version or hash are rejected, never reinterpreted.
    /// </summary>
    public static class Replayer
    {
        public static ReplayReport Verify(MatchRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (record.RulesVersion != RulesConstants.RulesVersion || record.Config == null || record.Config.RulesVersion != RulesConstants.RulesVersion)
                return Fail(ReplayFailure.IncompatibleRules, "Record uses rules version '" + record.RulesVersion + "'; this build runs " + RulesConstants.RulesVersion + ".");
            if (record.RulesHashHex != RulesBundle.HashHex)
                return Fail(ReplayFailure.IncompatibleRules, "Record rules hash " + record.RulesHashHex + " differs from " + RulesBundle.HashHex + ".");

            MatchEngine engine;
            try
            {
                engine = MatchEngine.Create(record.Config, Hex.Decode(record.SeedHex), record.MatchId);
            }
            catch (Exception e) when (e is ArgumentException || e is FormatException || e is RulesViolationException)
            {
                return Fail(ReplayFailure.Malformed, e.Message);
            }

            if (Hex.Encode(engine.SeedCommitment) != record.SeedCommitmentHex)
                return Fail(ReplayFailure.SeedCommitmentMismatch, "Seed commitment differs.", engine);
            if (engine.FirstAttacker != record.FirstAttacker || engine.InitiativeStreamCounter != record.InitiativeStreamCounter)
                return Fail(ReplayFailure.InitiativeMismatch, "Initiative differs.", engine);

            for (int i = 0; i < record.Commands.Count; i++)
            {
                RecordedCommand rc = record.Commands[i];
                CommandReceipt receipt;
                if (rc.Sender.HasValue)
                {
                    receipt = engine.Submit(rc.Sender.Value, rc.Command);
                }
                else
                {
                    if (!(rc.Command is AdvancePhaseCommand advance))
                        return Fail(ReplayFailure.CommandRejected, "Command " + i + " has no sender but is not an AdvancePhase.", engine);
                    receipt = engine.Advance(advance);
                }
                if (!receipt.Accepted || receipt.InputRevision != (ulong)(i + 1))
                    return Fail(ReplayFailure.CommandRejected, "Command " + i + " (" + rc.Command.Kind + "): " + receipt, engine);
            }

            IReadOnlyList<RoundRecord> actual = engine.Rounds;
            if (actual.Count != record.Rounds.Count)
                return Fail(ReplayFailure.RoundMismatch, "Replay produced " + actual.Count + " rounds; record has " + record.Rounds.Count + ".", engine);
            for (int i = 0; i < actual.Count; i++)
            {
                string diff = CompareRound(record.Rounds[i], actual[i]);
                if (diff != null) return Fail(ReplayFailure.RoundMismatch, "Round " + (i + 1) + ": " + diff, engine);
            }

            string resultDiff = CompareResult(record.Result, engine.Result);
            if (resultDiff != null) return Fail(ReplayFailure.ResultMismatch, resultDiff, engine);
            return new ReplayReport(ReplayFailure.None, null, engine);
        }

        /// <summary>Parses and verifies a serialized record; malformed text is reported, not thrown.</summary>
        public static ReplayReport Verify(string recordJson)
        {
            MatchRecord record;
            try
            {
                record = MatchRecord.FromJson(recordJson);
            }
            catch (FormatException e)
            {
                return Fail(ReplayFailure.Malformed, e.Message);
            }
            catch (OverflowException e)
            {
                return Fail(ReplayFailure.Malformed, e.Message);
            }
            return Verify(record);
        }

        private static string CompareRound(RoundRecord e, RoundRecord a)
        {
            if (e.Round != a.Round) return "round index";
            if (e.Attacker != a.Attacker) return "attacker";
            if (e.FrontierCellId != a.FrontierCellId || e.Terrain != a.Terrain || e.FrontierCount != a.FrontierCount) return "frontier terrain";
            if (e.TerrainStreamCounter != a.TerrainStreamCounter) return "terrain stream counter";
            if (e.Volleys.Count != a.Volleys.Count) return "volley count";
            for (int v = 0; v < e.Volleys.Count; v++)
            {
                VolleyRecord x = e.Volleys[v], y = a.Volleys[v];
                if (x.Volley != y.Volley || x.WeaponA != y.WeaponA || x.WeaponB != y.WeaponB || x.TimeoutA != y.TimeoutA ||
                    x.TimeoutB != y.TimeoutB || x.HpA != y.HpA || x.HpB != y.HpB || x.ResultAfter != y.ResultAfter)
                    return "volley " + x.Volley + " summary";
                if (x.LogHashHex != y.LogHashHex) return "volley " + x.Volley + " event log hash";
            }
            if (e.DuelResult != a.DuelResult || e.HpA != a.HpA || e.HpB != a.HpB || e.HpDifference != a.HpDifference) return "duel result";
            if (e.OfferedCards.Count != a.OfferedCards.Count) return "card offer";
            for (int c = 0; c < e.OfferedCards.Count; c++)
                if (e.OfferedCards[c] != a.OfferedCards[c]) return "card offer";
            if (e.CardsStreamCounter != a.CardsStreamCounter) return "cards stream counter";
            if (e.CutTimedOut != a.CutTimedOut || e.CellsTransferred != a.CellsTransferred) return "cut";
            if (e.CellsA != a.CellsA || e.CellsB != a.CellsB || e.MapRevision != a.MapRevision) return "cell counts";
            if (e.OwnershipHashHex != a.OwnershipHashHex) return "ownership hash";
            if (e.StateHashHex != a.StateHashHex) return "state hash";
            return null;
        }

        private static string CompareResult(MatchResult e, MatchResult a)
        {
            if (e == null && a == null) return null;
            if (e == null || a == null) return "Result presence differs.";
            if (e.Reason != a.Reason || e.Winner != a.Winner || e.ForfeitedBy != a.ForfeitedBy || e.CellsA != a.CellsA ||
                e.CellsB != a.CellsB || e.RoundsPlayed != a.RoundsPlayed)
                return "Result differs: recorded " + e + ", replayed " + a + ".";
            return null;
        }

        private static ReplayReport Fail(ReplayFailure failure, string detail, MatchEngine engine = null) =>
            new ReplayReport(failure, detail, engine);
    }
}
