using System;
using System.Collections.Generic;
using System.Linq;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.V2.Replays
{
    public enum ClipEventKind : byte
    {
        RoundIntro = 0,
        Volley = 1,
        DuelEnd = 2,
        Cut = 3,
        MatchEnd = 4,
    }

    /// <summary>One segment of the clip timeline with its interest score.</summary>
    public sealed class ClipEvent
    {
        public ClipEventKind Kind { get; }
        public int Round { get; }
        public int Volley { get; }
        public int StartMs { get; }
        public int DurationMs { get; }
        public int Score { get; }

        public ClipEvent(ClipEventKind kind, int round, int volley, int startMs, int durationMs, int score)
        {
            Kind = kind;
            Round = round;
            Volley = volley;
            StartMs = startMs;
            DurationMs = durationMs;
            Score = score;
        }

        public int EndMs => StartMs + DurationMs;
        public override string ToString() => Kind + " r" + Round + "v" + Volley + " @" + StartMs + "+" + DurationMs + " s" + Score;
    }

    /// <summary>
    /// Fixed presentation timing used by the clip renderer (the clip is a re-rendered replay, not a
    /// screen recording, so its timeline is defined here rather than by how long players took).
    /// PROPOSED values; they follow the rules' announcement and replay durations.
    /// </summary>
    public sealed class ClipTiming
    {
        public int RoundIntroMs { get; set; } = RulesConstants.TerrainAnnouncementMs;
        public int VolleyMs { get; set; } = RulesConstants.ResolutionReplayMaxMs;
        public int DuelEndMs { get; set; } = 1000;
        public int CutMs { get; set; } = 3000;
        public int MatchEndMs { get; set; } = 3000;

        public static readonly ClipTiming Default = new ClipTiming();
    }

    /// <summary>The chosen highlight: [StartMs, EndMs) of the clip timeline and the events it contains.</summary>
    public sealed class HighlightWindow
    {
        public int StartMs { get; }
        public int EndMs { get; }
        public int Score { get; }
        public IReadOnlyList<ClipEvent> Events { get; }
        public int TimelineMs { get; }

        public HighlightWindow(int startMs, int endMs, int score, IReadOnlyList<ClipEvent> events, int timelineMs)
        {
            StartMs = startMs;
            EndMs = endMs;
            Score = score;
            Events = events;
            TimelineMs = timelineMs;
        }

        public int DurationMs => EndMs - StartMs;
        public IEnumerable<int> Rounds => Events.Select(e => e.Round).Distinct();
    }

    /// <summary>
    /// Deterministic highlight selection from a terminal match record (plan: "a selected thirty-second
    /// ... highlight"). The timeline is built from the authoritative round and volley records; each
    /// segment gets an integer interest score:
    /// <list type="bullet">
    /// <item>volley: HP removed (in whole HP, both players) + 40 when a player is knocked out − 3 per timed-out (Pass) side;</item>
    /// <item>cut: cells transferred / 400, + 30 when that cut won the match by territory;</item>
    /// <item>match end card: 20.</item>
    /// </list>
    /// Candidate windows start at every segment start (and one ends at the timeline end); a window
    /// scores the segments wholly inside it; the highest score wins, ties go to the earliest start.
    /// The same record always yields the same window.
    /// </summary>
    public static class HighlightSelector
    {
        public const int DefaultWindowMs = 30000;

        public static IReadOnlyList<ClipEvent> Timeline(MatchRecord record, ClipTiming timing = null)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            timing = timing ?? ClipTiming.Default;
            var events = new List<ClipEvent>();
            int t = 0;
            int lastRound = record.Rounds.Count == 0 ? 0 : record.Rounds.Max(r => r.Round);
            foreach (RoundRecord round in record.Rounds.OrderBy(r => r.Round))
            {
                events.Add(new ClipEvent(ClipEventKind.RoundIntro, round.Round, 0, t, timing.RoundIntroMs, 0));
                t += timing.RoundIntroMs;
                int hpA = RulesConstants.StartHpUnits, hpB = RulesConstants.StartHpUnits;
                foreach (VolleyRecord v in round.Volleys.OrderBy(v => v.Volley))
                {
                    int removed = Math.Max(0, hpA - v.HpA) + Math.Max(0, hpB - v.HpB);
                    int score = removed / RulesConstants.HpUnitsPerHp;
                    if ((v.HpA <= 0 && hpA > 0) || (v.HpB <= 0 && hpB > 0)) score += 40;
                    score -= 3 * ((v.TimeoutA ? 1 : 0) + (v.TimeoutB ? 1 : 0));
                    events.Add(new ClipEvent(ClipEventKind.Volley, round.Round, v.Volley, t, timing.VolleyMs, Math.Max(0, score)));
                    t += timing.VolleyMs;
                    hpA = v.HpA;
                    hpB = v.HpB;
                }
                events.Add(new ClipEvent(ClipEventKind.DuelEnd, round.Round, 0, t, timing.DuelEndMs, 0));
                t += timing.DuelEndMs;
                if (round.CellsTransferred > 0)
                {
                    int score = round.CellsTransferred / 400;
                    if (round.Round == lastRound && record.Result != null && record.Result.Reason == TerminalReason.Territory90) score += 30;
                    events.Add(new ClipEvent(ClipEventKind.Cut, round.Round, 0, t, timing.CutMs, score));
                    t += timing.CutMs;
                }
            }
            events.Add(new ClipEvent(ClipEventKind.MatchEnd, lastRound, 0, t, timing.MatchEndMs, 20));
            return events;
        }

        public static HighlightWindow Select(MatchRecord record, int windowMs = DefaultWindowMs, ClipTiming timing = null)
        {
            if (windowMs <= 0) throw new ArgumentOutOfRangeException(nameof(windowMs));
            IReadOnlyList<ClipEvent> events = Timeline(record, timing);
            int total = events.Count == 0 ? 0 : events[events.Count - 1].EndMs;
            if (total <= windowMs)
                return new HighlightWindow(0, total, events.Sum(e => e.Score), events, total);
            var starts = new SortedSet<int>(events.Select(e => e.StartMs).Where(s => s + windowMs <= total)) { total - windowMs };
            int bestStart = 0, bestScore = -1;
            foreach (int s in starts)
            {
                int score = events.Where(e => e.StartMs >= s && e.EndMs <= s + windowMs).Sum(e => e.Score);
                if (score > bestScore) { bestScore = score; bestStart = s; }
            }
            ClipEvent[] inside = events.Where(e => e.StartMs >= bestStart && e.EndMs <= bestStart + windowMs).ToArray();
            return new HighlightWindow(bestStart, bestStart + windowMs, bestScore, inside, total);
        }
    }
}
