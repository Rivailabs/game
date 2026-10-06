using System;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Client.Combat
{
    /// <summary>
    /// Maps real playback time to authoritative simulation time for one resolved volley. Rendering
    /// interpolates the engine's tracks at the mapped time; it never decides contacts itself.
    /// Flight longer than the flight budget is compressed linearly (consistently, by one factor), so
    /// the whole replay including the explanation hold stays within the 2.5 s resolution window.
    /// </summary>
    public sealed class PlaybackTimeline
    {
        public const double SubTicksPerSecond = (double)RulesConstants.TicksPerSecond * RulesConstants.SubTicksPerTick;
        public const double DefaultHoldSeconds = 0.7;

        /// <summary>Last authoritative time with any flight or strike event (sub-ticks since launch).</summary>
        public long SimEndSubTicks { get; }
        /// <summary>Real seconds the flight animation takes (at most the flight budget).</summary>
        public double FlightSeconds { get; }
        /// <summary>Flight plus explanation hold; never more than the total budget.</summary>
        public double TotalSeconds { get; }
        /// <summary>Simulated seconds per real second (1 when no compression was needed).</summary>
        public double Compression { get; }

        public double SimSeconds => SimEndSubTicks / SubTicksPerSecond;

        public PlaybackTimeline(long simEndSubTicks, double totalBudgetSeconds, double holdSeconds)
        {
            if (totalBudgetSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(totalBudgetSeconds));
            if (holdSeconds < 0 || holdSeconds >= totalBudgetSeconds) throw new ArgumentOutOfRangeException(nameof(holdSeconds));
            SimEndSubTicks = Math.Max(0, simEndSubTicks);
            double flightBudget = totalBudgetSeconds - holdSeconds;
            double sim = SimSeconds;
            FlightSeconds = Math.Min(sim, flightBudget);
            Compression = FlightSeconds > 0 ? sim / FlightSeconds : 1.0;
            TotalSeconds = FlightSeconds + holdSeconds;
        }

        public static PlaybackTimeline For(VolleyResult result, double totalBudgetSeconds = RulesConstants.ResolutionReplayMaxMs / 1000.0,
            double holdSeconds = DefaultHoldSeconds)
        {
            long end = 0;
            if (result != null)
            {
                if (result.Simulation != null)
                    foreach (ProjectileTrack t in result.Simulation.Tracks) end = Math.Max(end, t.EndTimeSubTicks);
                if (result.Log != null)
                    foreach (CombatEvent e in result.Log.Events) end = Math.Max(end, e.TimeSubTicks);
            }
            return new PlaybackTimeline(end, totalBudgetSeconds, holdSeconds);
        }

        /// <summary>Authoritative time (sub-ticks) to show at a real playback time; clamps at the end.</summary>
        public long SimTimeAt(double playbackSeconds)
        {
            if (playbackSeconds <= 0 || FlightSeconds <= 0) return playbackSeconds <= 0 ? 0 : SimEndSubTicks;
            if (playbackSeconds >= FlightSeconds) return SimEndSubTicks;
            return (long)Math.Round(playbackSeconds / FlightSeconds * SimEndSubTicks);
        }

        public bool FlightFinished(double playbackSeconds) => playbackSeconds >= FlightSeconds;
    }
}
