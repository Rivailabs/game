using System;
using System.Collections.Generic;
using AstraKingdoms.Meta.Progression;
using AstraKingdoms.V2.Ranked;

namespace AstraKingdoms.V2.Integration
{
    /// <summary>
    /// What the online match service (astra-kingdoms/server, built separately) calls into V2. The V2
    /// library never calls the match service; the server owns transport, sessions and persistence.
    /// </summary>
    public interface IV2MatchServiceHook
    {
        /// <summary>
        /// Ranked queue: called when the matchmaker created <paramref name="ticket"/>; the server then
        /// starts an authoritative match with <see cref="RankedMatchTicket.MatchId"/> and the season snapshot's
        /// rules hash. Never called for casual, practice or friend rooms.
        /// </summary>
        void OnRankedMatchCreated(RankedMatchTicket ticket);

        /// <summary>
        /// Every terminal match: one call per human seat with Meta's report (XP/coins, pass points,
        /// clan contribution, homeland milestones), plus one <see cref="RankedMatchResult"/> for ranked
        /// matches. All handlers are idempotent per match result id, so retries are safe.
        /// </summary>
        void OnMatchTerminal(IReadOnlyList<MatchOutcomeReport> seatReports, RankedMatchResult rankedResult);
    }

    /// <summary>
    /// Match-duration telemetry used to close ranked matchmaking before a season boundary. The server
    /// reports the maximum observed ranked match duration (wall clock from creation to terminal
    /// result) over a trailing window; seasons are published with that value.
    /// </summary>
    public interface IMatchDurationTelemetry
    {
        /// <summary>Null until enough matches were measured; the rules-clock bound is used meanwhile.</summary>
        TimeSpan? MeasuredMaximum { get; }
    }

    /// <summary>A fixed measurement (tests, rehearsals, and seasons published from an offline report).</summary>
    public sealed class FixedMatchDurationTelemetry : IMatchDurationTelemetry
    {
        public FixedMatchDurationTelemetry(TimeSpan? measured) => MeasuredMaximum = measured;
        public TimeSpan? MeasuredMaximum { get; }
    }
}
