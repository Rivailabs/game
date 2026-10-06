using System;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.World.Armies;

namespace AstraKingdoms.World.Challenges
{
    public enum ChallengeState : byte
    {
        /// <summary>Tile and loss allowance reserved; waiting for the authoritative result.</summary>
        Reserved = 0,
        /// <summary>The result was committed once (ownership and rewards, or no transfer).</summary>
        Resolved = 1,
        /// <summary>The reservation was cancelled without transfer (expiry, season close, technical failure).</summary>
        Cancelled = 2,
    }

    public enum EncounterOutcome : byte
    {
        AttackerWins = 0,
        DefenderWins = 1,
        /// <summary>Draw or void: no transfer.</summary>
        Draw = 2,
    }

    /// <summary>A border challenge request. The request ID makes creation idempotent across retries.</summary>
    public sealed class ChallengeRequest
    {
        public string RequestId { get; }
        public string AttackerId { get; }
        public string DefenderId { get; }
        /// <summary>Requested tile, or null to let the server pick the defender's lowest eligible tile.</summary>
        public string TileId { get; }
        public TacticalArmy AttackerArmy { get; }

        public ChallengeRequest(string requestId, string attackerId, string defenderId, string tileId = null, TacticalArmy attackerArmy = null)
        {
            if (string.IsNullOrEmpty(requestId)) throw new ArgumentException("Request ID required.", nameof(requestId));
            RequestId = requestId;
            AttackerId = attackerId;
            DefenderId = defenderId;
            TileId = tileId;
            AttackerArmy = attackerArmy ?? TacticalArmy.Empty;
        }
    }

    /// <summary>
    /// A unique challenge with its reservation and snapshots (plan: "The server creates a unique
    /// challenge, validates participants, reserves an eligible target tile and remaining loss
    /// allowance, then snapshots rules and the defender's chosen loadout").
    /// </summary>
    public sealed class Challenge
    {
        public string ChallengeId { get; internal set; }
        public string RequestId { get; internal set; }
        public string ShardId { get; internal set; }
        public string SeasonId { get; internal set; }
        public string AttackerId { get; internal set; }
        public string DefenderId { get; internal set; }
        public string TileId { get; internal set; }
        public long CreatedMs { get; internal set; }
        public long ExpiresMs { get; internal set; }
        /// <summary>The UTC day whose loss allowance this challenge reserved (and, on success, consumes).</summary>
        public long ReservedUtcDay { get; internal set; }
        /// <summary>Alliances the attacker counted for when the challenge was created (per-alliance cap).</summary>
        public string[] AttackerAlliances { get; internal set; } = Array.Empty<string>();

        // Snapshots taken at creation; later publication or rules changes never affect this challenge.
        public string WorldRulesHashHex { get; internal set; }
        public string DuelRulesHashHex { get; internal set; }
        public DefencePublication DefenceSnapshot { get; internal set; }
        public TacticalArmy AttackerArmy { get; internal set; }

        public ChallengeState State { get; internal set; }
        public EncounterOutcome? Outcome { get; internal set; }
        /// <summary>ID of the authoritative result that resolved the challenge (e.g. the match record hash).</summary>
        public string ResolutionId { get; internal set; }
        public string CancelReason { get; internal set; }

        public bool IsOpen => State == ChallengeState.Reserved;

        /// <summary>Shallow copy handed to callers so they never hold the shard's live object.</summary>
        internal object MemberwiseCloneInternal() => MemberwiseClone();

        public string SnapshotHashHex()
        {
            var w = new CanonicalWriter();
            w.Ascii(ChallengeId).Ascii(ShardId).Ascii(SeasonId).Ascii(AttackerId).Ascii(DefenderId).Ascii(TileId)
             .I64(CreatedMs).I64(ExpiresMs).Ascii(WorldRulesHashHex).Ascii(DuelRulesHashHex);
            DefenceSnapshot.WriteTo(w);
            AttackerArmy.WriteTo(w);
            return Hex.Encode(w.Sha256());
        }
    }

    /// <summary>Result of a challenge command. Rejections never change state.</summary>
    public sealed class ChallengeReceipt
    {
        public bool Accepted { get; }
        public string Code { get; }
        public Challenge Challenge { get; }
        /// <summary>True when an earlier identical request was answered again (no new challenge).</summary>
        public bool Replayed { get; }

        private ChallengeReceipt(bool accepted, string code, Challenge challenge, bool replayed)
        {
            Accepted = accepted;
            Code = code;
            Challenge = challenge;
            Replayed = replayed;
        }

        internal static ChallengeReceipt Ok(Challenge c, bool replayed = false) => new ChallengeReceipt(true, "OK", c, replayed);
        internal static ChallengeReceipt Reject(string code, Challenge c = null) => new ChallengeReceipt(false, code, c, false);

        public override string ToString() => (Accepted ? "accepted" : "rejected " + Code) + (Replayed ? " (replayed)" : string.Empty);
    }

    /// <summary>One border tile of the seasonal world. Homeland plots are never tiles.</summary>
    public sealed class BorderTile
    {
        public string TileId { get; internal set; }
        /// <summary>The account whose standard allocation created this tile this season.</summary>
        public string HomeAccountId { get; internal set; }
        public string OwnerId { get; internal set; }
        /// <summary>The challenge holding this tile, or null.</summary>
        public string ReservedBy { get; internal set; }

        internal BorderTile Clone() => (BorderTile)MemberwiseClone();
    }
}
