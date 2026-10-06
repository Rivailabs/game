using System;
using System.Collections.Generic;

namespace AstraKingdoms.Modes.FourPlayer
{
    /// <summary>What a spectator (or a bye/eliminated participant) may see at one moment.</summary>
    public sealed class SpectatorSnapshot
    {
        /// <summary>The last wave whose events are visible (0 = only the match start).</summary>
        public int VisibleThroughWave { get; internal set; }
        public bool MatchFinished { get; internal set; }
        public IReadOnlyList<FourPlayerEvent> Events { get; internal set; } = Array.Empty<FourPlayerEvent>();
        /// <summary>Owner bytes after <see cref="VisibleThroughWave"/> settled (wave 0 = starting sectors).</summary>
        public byte[] Board { get; internal set; }
    }

    /// <summary>
    /// The spectator stream (plan: "Spectators tournaments and platform expansion").
    /// <para>
    /// <b>Filtering first.</b> The source is the match's public event log, which by construction
    /// contains only explicitly public states and resolved events: the engine never emits a lock, a
    /// ready flag, a provisional cut, a bot hint or any player-only diagnostic.
    /// </para>
    /// <para>
    /// <b>Delay second.</b> While wave k is in progress (so wave k − 1 is the last settled wave) the
    /// feed shows events up to wave k − 1 − delay, i.e. at least one completed wave lies between
    /// what spectators see and active play (PROPOSED default delay 1). The delay cannot be set below
    /// <see cref="FourPlayerRules.SpectatorDelayWaves"/>. Once the match finishes everything public,
    /// including the disclosed seed, is shown. Delay supplements filtering; it is never a reason to
    /// let a secret into the source log.
    /// </para>
    /// </summary>
    public static class SpectatorFeed
    {
        public static SpectatorSnapshot Build(FourPlayerMatch match, int delayWaves = FourPlayerRules.SpectatorDelayWaves)
        {
            if (match == null) throw new ArgumentNullException(nameof(match));
            if (delayWaves < FourPlayerRules.SpectatorDelayWaves)
                throw new ArgumentOutOfRangeException(nameof(delayWaves), "Spectators must trail by at least one completed wave.");

            IReadOnlyList<FourPlayerEvent> all = match.PublicEvents;
            bool finished = match.IsFinished;
            int visible = finished ? match.Wave : Math.Max(0, match.SettledWave - delayWaves);
            var events = new List<FourPlayerEvent>();
            foreach (FourPlayerEvent e in all)
            {
                if (!IsPublicType(e.Type)) continue;
                if (finished || e.Wave <= visible) events.Add(e);
            }
            return new SpectatorSnapshot
            {
                VisibleThroughWave = visible,
                MatchFinished = finished,
                Events = events,
                Board = match.SettledBoard(finished ? match.SettledWave : visible),
            };
        }

        /// <summary>Defensive whitelist: only these event types may ever reach spectators.</summary>
        public static bool IsPublicType(FourPlayerEventType type)
        {
            switch (type)
            {
                case FourPlayerEventType.MatchCreated:
                case FourPlayerEventType.WaveStarted:
                case FourPlayerEventType.VolleyResolved:
                case FourPlayerEventType.DuelEnded:
                case FourPlayerEventType.CutCommitted:
                case FourPlayerEventType.CutTimedOut:
                case FourPlayerEventType.PlayerForfeited:
                case FourPlayerEventType.CutApplied:
                case FourPlayerEventType.WaveSettled:
                case FourPlayerEventType.Eliminated:
                case FourPlayerEventType.MatchFinished:
                    return true;
                default:
                    return false;
            }
        }
    }
}
