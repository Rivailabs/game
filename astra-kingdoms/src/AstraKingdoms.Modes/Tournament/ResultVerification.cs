using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.Modes.Tournament
{
    /// <summary>Outcome of a two-player fixture from seat A's point of view.</summary>
    public enum FixtureOutcome : byte
    {
        AWins = 0,
        BWins = 1,
        Draw = 2,
        /// <summary>Both seats forfeited (void) — handled like a double absence.</summary>
        Void = 3,
    }

    /// <summary>A match result whose record replayed to identical state hashes.</summary>
    public sealed class VerifiedMatchResult
    {
        /// <summary>SHA-256 (hex) of the canonical match record: the verified result ID.</summary>
        public string ResultId { get; }
        public string MatchId { get; }
        public FixtureOutcome Outcome { get; }

        public VerifiedMatchResult(string resultId, string matchId, FixtureOutcome outcome)
        {
            ResultId = resultId ?? throw new ArgumentNullException(nameof(resultId));
            MatchId = matchId ?? throw new ArgumentNullException(nameof(matchId));
            Outcome = outcome;
        }
    }

    /// <summary>Turns a submitted match record into a verified result, or explains why not.</summary>
    public interface IMatchResultVerifier
    {
        /// <summary>Returns null and a reason when the record cannot be verified.</summary>
        VerifiedMatchResult Verify(string recordJson, out string failure);
    }

    /// <summary>
    /// Verifies with the authoritative replayer: the record must parse, carry the AK-TR-1 rules hash,
    /// replay to identical state hashes and be finished. The result ID is the SHA-256 of the
    /// record's canonical JSON, so the same match always yields the same ID.
    /// </summary>
    public sealed class ReplayMatchResultVerifier : IMatchResultVerifier
    {
        public VerifiedMatchResult Verify(string recordJson, out string failure)
        {
            failure = null;
            if (string.IsNullOrEmpty(recordJson))
            {
                failure = "Empty record.";
                return null;
            }
            MatchRecord record;
            try
            {
                record = MatchRecord.FromJson(recordJson);
            }
            catch (Exception e) when (e is FormatException || e is OverflowException || e is KeyNotFoundException || e is ArgumentException || e is InvalidOperationException || e is RulesViolationException)
            {
                failure = "Unreadable record: " + e.Message;
                return null;
            }
            ReplayReport report = Replayer.Verify(record);
            if (!report.Success)
            {
                failure = report.ToString();
                return null;
            }
            MatchResult result = report.Engine.Result;
            if (result == null)
            {
                failure = "The match has not finished.";
                return null;
            }
            FixtureOutcome outcome = result.IsVoid ? FixtureOutcome.Void
                : result.Winner == PlayerSide.A ? FixtureOutcome.AWins
                : result.Winner == PlayerSide.B ? FixtureOutcome.BWins
                : FixtureOutcome.Draw;
            string canonical = record.ToJson();
            string id;
            using (SHA256 sha = SHA256.Create())
            {
                id = Hex.Encode(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
            }
            return new VerifiedMatchResult(id, record.MatchId, outcome);
        }
    }

    /// <summary>A cosmetic entitlement granted exactly once per grant ID.</summary>
    public sealed class CosmeticGrant
    {
        public string GrantId { get; }
        public string AccountId { get; }
        public string CosmeticId { get; }

        public CosmeticGrant(string grantId, string accountId, string cosmeticId)
        {
            GrantId = grantId;
            AccountId = accountId;
            CosmeticId = cosmeticId;
        }
    }

    /// <summary>
    /// Thread-safe, idempotent entitlement ledger: the first grant with an ID is recorded, every
    /// later grant with that ID (a retry, a duplicate settlement, a restored backup replaying its
    /// log) is ignored. Cosmetics are non-transferable: there is no transfer or removal API.
    /// </summary>
    public sealed class CosmeticGrantLedger
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, CosmeticGrant> _byId = new Dictionary<string, CosmeticGrant>(StringComparer.Ordinal);
        private readonly List<CosmeticGrant> _ordered = new List<CosmeticGrant>();

        /// <summary>Returns true when this call created the grant; false when the ID was already granted.</summary>
        public bool Grant(string grantId, string accountId, string cosmeticId)
        {
            if (string.IsNullOrEmpty(grantId)) throw new ArgumentException("Grant ID required.", nameof(grantId));
            lock (_gate)
            {
                if (_byId.ContainsKey(grantId)) return false;
                var g = new CosmeticGrant(grantId, accountId, cosmeticId);
                _byId.Add(grantId, g);
                _ordered.Add(g);
                return true;
            }
        }

        public IReadOnlyList<CosmeticGrant> All
        {
            get { lock (_gate) return _ordered.ToArray(); }
        }

        public int CountFor(string accountId)
        {
            lock (_gate)
            {
                int n = 0;
                foreach (CosmeticGrant g in _ordered)
                    if (g.AccountId == accountId) n++;
                return n;
            }
        }
    }
}
