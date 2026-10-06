using System;
using System.Collections.Generic;
using System.Linq;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Meta.Progression
{
    /// <summary>Who the player faced and where (decides which XP rule applies).</summary>
    public enum MatchKind : byte
    {
        /// <summary>Online match against another person.</summary>
        OnlineHuman = 0,
        /// <summary>Two people on one phone. Counts as a human match for the device profile.</summary>
        LocalSharedPhone = 1,
        /// <summary>Local practice against a labelled bot.</summary>
        Practice = 2,
        /// <summary>Online, consented, labelled bot match after a queue wait (ticket 53). Practice rules.</summary>
        OnlineBot = 3,
    }

    /// <summary>The player's own result.</summary>
    public enum PlayerOutcome : byte
    {
        Loss = 0,
        Win = 1,
        Draw = 2,
        /// <summary>The result cannot be attributed to this profile (e.g. a shared-phone match on the device profile).</summary>
        Unattributed = 3,
    }

    /// <summary>How the match ended. Mirrors the plan's completion reporting categories.</summary>
    public enum MatchEnding : byte
    {
        /// <summary>A player reached the 90% territory threshold (a valid early victory).</summary>
        EarlyVictory = 0,
        /// <summary>Round eight finished and exact cell counts decided.</summary>
        RoundsComplete = 1,
        VoluntaryForfeit = 2,
        /// <summary>Two consecutive lock timeouts.</summary>
        TimeoutForfeit = 3,
        /// <summary>Server/network/crash abort; also the rules' Void outcome.</summary>
        TechnicalAbort = 4,
    }

    /// <summary>
    /// What the authority (server, or the local host for offline guest play) knows about one player's
    /// finished match. This is the only input to XP and coin grants; client-reported totals are never
    /// accepted.
    /// </summary>
    public sealed class MatchOutcomeReport
    {
        /// <summary>Unique id of the authoritative match result (the online grant key).</summary>
        public string MatchResultId { get; }
        public string PlayerId { get; }
        public MatchKind Kind { get; }
        public PlayerOutcome Outcome { get; }
        public MatchEnding Ending { get; }
        public CatalogPreset Catalog { get; }
        /// <summary>Distinct weapon ids the player fired in the match (mastery and the "two elements" task).</summary>
        public IReadOnlyList<int> WeaponsUsed { get; }
        /// <summary>Played by the automation interface (bots in both seats, smoke runs).</summary>
        public bool IsAutomation { get; }
        /// <summary>Developer/internal test session (dev build, test account, simulator).</summary>
        public bool IsDeveloperTest { get; }
        /// <summary>False when validation found the result unusable (tampering, rules-hash mismatch, replay failure).</summary>
        public bool IsValid { get; }
        public DateTimeOffset CompletedAt { get; }

        public MatchOutcomeReport(string matchResultId, string playerId, MatchKind kind, PlayerOutcome outcome, MatchEnding ending,
            DateTimeOffset completedAt, IEnumerable<int> weaponsUsed = null, CatalogPreset catalog = CatalogPreset.Starter,
            bool isAutomation = false, bool isDeveloperTest = false, bool isValid = true)
        {
            if (string.IsNullOrEmpty(matchResultId)) throw new ArgumentException("A match result id is required.", nameof(matchResultId));
            if (string.IsNullOrEmpty(playerId)) throw new ArgumentException("A player id is required.", nameof(playerId));
            MatchResultId = matchResultId;
            PlayerId = playerId;
            Kind = kind;
            Outcome = outcome;
            Ending = ending;
            CompletedAt = completedAt;
            WeaponsUsed = (weaponsUsed ?? Array.Empty<int>()).Where(WeaponCatalog.IsRegularId).Distinct().OrderBy(w => w).ToArray();
            Catalog = catalog;
            IsAutomation = isAutomation;
            IsDeveloperTest = isDeveloperTest;
            IsValid = isValid;
        }

        /// <summary>The match reached a normal terminal result under the rules (plan: completion definition).</summary>
        public bool IsNormallyCompleted => Ending == MatchEnding.EarlyVictory || Ending == MatchEnding.RoundsComplete;

        /// <summary>Distinct elements of the weapons used.</summary>
        public IReadOnlyList<Element> ElementsUsed => WeaponsUsed.Select(w => WeaponCatalog.Get(w).Element).Distinct().ToArray();

        /// <summary>Maps a rules-engine terminal reason (no voluntary-forfeit command exists in AK-TR-1 yet).</summary>
        public static MatchEnding EndingFrom(MatchResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            switch (result.Reason)
            {
                case TerminalReason.Territory90: return MatchEnding.EarlyVictory;
                case TerminalReason.RoundsComplete: return MatchEnding.RoundsComplete;
                case TerminalReason.Forfeit: return MatchEnding.TimeoutForfeit;
                default: return MatchEnding.TechnicalAbort;
            }
        }

        /// <summary>The outcome of <paramref name="side"/> in a rules-engine result.</summary>
        public static PlayerOutcome OutcomeFrom(MatchResult result, PlayerSide side)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (result.IsVoid) return PlayerOutcome.Loss;
            if (!result.Winner.HasValue) return PlayerOutcome.Draw;
            return result.Winner.Value == side ? PlayerOutcome.Win : PlayerOutcome.Loss;
        }
    }
}
