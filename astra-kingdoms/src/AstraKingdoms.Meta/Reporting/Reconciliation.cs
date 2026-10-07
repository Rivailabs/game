using System;
using System.Collections.Generic;
using System.Linq;
using AstraKingdoms.Meta.Analytics;
using AstraKingdoms.Meta.Progression;

namespace AstraKingdoms.Meta.Reporting
{
    public sealed class ReconciliationReport
    {
        /// <summary>Match events whose match id has no authoritative record (fabricated, test or lost records).</summary>
        public IReadOnlyList<string> EventsWithoutRecord { get; }
        /// <summary>match_end reason or mode that disagrees with the record.</summary>
        public IReadOnlyList<string> Mismatches { get; }
        /// <summary>More match_end events for a match than it had human seats.</summary>
        public IReadOnlyList<string> ExcessEndEvents { get; }
        /// <summary>Events from real-person sessions about records that were automation/internal (flagging gaps).</summary>
        public IReadOnlyList<string> UnflaggedTestTraffic { get; }
        /// <summary>Human-started records that have at least one match_end event (consent limits this; informational).</summary>
        public Proportion EventCoverage { get; }
        public int DuplicateEventIds { get; }
        public int SchemaInvalid { get; }

        public ReconciliationReport(IReadOnlyList<string> withoutRecord, IReadOnlyList<string> mismatches, IReadOnlyList<string> excess,
            IReadOnlyList<string> unflagged, Proportion coverage, int duplicates, int invalid)
        {
            EventsWithoutRecord = withoutRecord;
            Mismatches = mismatches;
            ExcessEndEvents = excess;
            UnflaggedTestTraffic = unflagged;
            EventCoverage = coverage;
            DuplicateEventIds = duplicates;
            SchemaInvalid = invalid;
        }

        /// <summary>Clean when nothing contradicts the authoritative records.</summary>
        public bool Consistent => EventsWithoutRecord.Count == 0 && Mismatches.Count == 0 && ExcessEndEvents.Count == 0 && UnflaggedTestTraffic.Count == 0 && SchemaInvalid == 0;
    }

    /// <summary>
    /// Ticket 64: reconciles permitted analytics events with the authoritative match records. Events
    /// are a consented subset, so missing events are only coverage; contradictions are errors.
    /// </summary>
    public static class EventReconciler
    {
        public static string ModeToken(MatchKind k)
        {
            switch (k)
            {
                case MatchKind.OnlineHuman: return "online_human";
                case MatchKind.OnlineBot: return "online_bot";
                case MatchKind.LocalSharedPhone: return "shared_phone";
                default: return "practice";
            }
        }

        public static string ReasonToken(MatchEnding e)
        {
            switch (e)
            {
                case MatchEnding.EarlyVictory: return "early_victory";
                case MatchEnding.RoundsComplete: return "rounds_complete";
                case MatchEnding.VoluntaryForfeit: return "voluntary_forfeit";
                case MatchEnding.TimeoutForfeit: return "timeout_forfeit";
                default: return "technical_abort";
            }
        }

        public static ReconciliationReport Reconcile(IEnumerable<AnalyticsEvent> events, IEnumerable<MatchRecordSummary> records)
        {
            List<AnalyticsEvent> all = events.ToList();
            int duplicates = all.Count - all.Select(e => e.EventId).Distinct().Count();
            List<AnalyticsEvent> unique = all.GroupBy(e => e.EventId).Select(g => g.First()).ToList();
            int invalid = unique.Count(e => AnalyticsSchema.Validate(e).Count > 0);
            Dictionary<string, MatchRecordSummary> byId = records.GroupBy(r => r.MatchId).ToDictionary(g => g.Key, g => g.First());

            var withoutRecord = new SortedSet<string>(StringComparer.Ordinal);
            var mismatches = new List<string>();
            var unflagged = new SortedSet<string>(StringComparer.Ordinal);
            foreach (AnalyticsEvent e in unique)
            {
                string matchId = e.Token("match_id");
                if (matchId == null) continue;
                if (!byId.TryGetValue(matchId, out MatchRecordSummary r))
                {
                    withoutRecord.Add(matchId);
                    continue;
                }
                if ((r.IsAutomation || r.IsInternal) && e.IsRealPerson) unflagged.Add(matchId);
                if (e.Type == AnalyticsEventType.MatchEnd)
                {
                    if (e.Token("reason") != ReasonToken(r.Ending)) mismatches.Add(matchId + ": reason " + e.Token("reason") + " vs record " + ReasonToken(r.Ending));
                    if (e.Token("mode") != ModeToken(r.Kind)) mismatches.Add(matchId + ": mode " + e.Token("mode") + " vs record " + ModeToken(r.Kind));
                    if (e.Integer("rounds") != r.Rounds) mismatches.Add(matchId + ": rounds " + e.Integer("rounds") + " vs record " + r.Rounds);
                }
                if (e.Type == AnalyticsEventType.MatchStart && e.Token("mode") != ModeToken(r.Kind))
                    mismatches.Add(matchId + ": start mode " + e.Token("mode") + " vs record " + ModeToken(r.Kind));
            }

            var excess = new List<string>();
            foreach (IGrouping<string, AnalyticsEvent> g in unique.Where(e => e.Type == AnalyticsEventType.MatchEnd && e.Token("match_id") != null)
                         .GroupBy(e => e.Token("match_id")))
            {
                if (byId.TryGetValue(g.Key, out MatchRecordSummary r) && g.Count() > Math.Max(1, r.HumanSeats))
                    excess.Add(g.Key + ": " + g.Count() + " end events for " + r.HumanSeats + " human seats");
            }

            List<MatchRecordSummary> human = byId.Values.Where(r => r.HumanStarted).ToList();
            var ended = new HashSet<string>(unique.Where(e => e.Type == AnalyticsEventType.MatchEnd).Select(e => e.Token("match_id")).Where(x => x != null));
            var coverage = new Proportion(human.Count(r => ended.Contains(r.MatchId)), human.Count);
            return new ReconciliationReport(withoutRecord.ToList(), mismatches, excess, unflagged.ToList(), coverage, duplicates, invalid);
        }
    }
}
