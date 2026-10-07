using System;
using AstraKingdoms.Client.Match;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Match;
using UnityEngine;

namespace AstraKingdoms.Client.MatchFlow
{
    /// <summary>
    /// Unity wrapper around <see cref="LocalMatchHost"/>: drives the host clock from unscaled real
    /// time. A single frame contributes at most <see cref="MaxStepSeconds"/>, so a stall or a trip to
    /// the background cannot skip a whole private entry window.
    /// </summary>
    public sealed class MatchController : MonoBehaviour
    {
        public const float MaxStepSeconds = 0.5f;

        public LocalMatchHost Host { get; private set; }
        /// <summary>Clock multiplier (1 for people; automation may run faster).</summary>
        public float ClockSpeed { get; set; } = 1f;

        public event Action<LocalMatchHost> MatchStarted;

        /// <summary>
        /// Optional per-frame filter (seconds in, seconds out) applied before the host clock: the
        /// session lifecycle returns 0 while the app is backgrounded or covered (ticket 47).
        /// </summary>
        public Func<double, double> DeltaFilter { get; set; }

        /// <summary>Creates a fresh match: new seed, new engine, no state or secret carried over.</summary>
        public LocalMatchHost StartMatch(MatchConfig config, SeatKind seatA, SeatKind seatB, BotDifficulty difficulty,
            HostTimings timings = null, byte[] seed = null, string matchId = null, Func<string> requestIds = null)
        {
            EndMatch();
            if (seed == null || matchId == null) MatchFactory.NewLive(out seed, out matchId);
            Host = new LocalMatchHost(config, seed, matchId, seatA, seatB, difficulty, requestIds, timings);
            MatchStarted?.Invoke(Host);
            Host.Start();
            return Host;
        }

        public void EndMatch() => Host = null;

        private void Update()
        {
            if (Host == null) return;
            double dt = Mathf.Min(Time.unscaledDeltaTime, MaxStepSeconds) * ClockSpeed;
            if (DeltaFilter != null) dt = DeltaFilter(dt);
            Host.Tick(dt);
        }
    }
}
