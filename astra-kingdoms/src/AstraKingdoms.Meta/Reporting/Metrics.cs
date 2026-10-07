using System;
using System.Collections.Generic;
using System.Linq;
using AstraKingdoms.Meta.Analytics;
using AstraKingdoms.Meta.Progression;

namespace AstraKingdoms.Meta.Reporting
{
    /// <summary>A proportion with its approximate 95% Wilson score interval.</summary>
    public sealed class Proportion
    {
        public int Successes { get; }
        public int Total { get; }
        public double Rate => Total == 0 ? 0 : (double)Successes / Total;
        public double Low { get; }
        public double High { get; }

        public Proportion(int successes, int total)
        {
            if (total < 0 || successes < 0 || successes > total) throw new ArgumentOutOfRangeException(nameof(successes));
            Successes = successes;
            Total = total;
            (Low, High) = Wilson.Interval(successes, total);
        }

        public override string ToString() =>
            Successes + "/" + Total + " = " + (Rate * 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "% (95% CI " +
            (Low * 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "-" +
            (High * 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%)";
    }

    /// <summary>Wilson score interval (reporting only; not an authoritative game path, so doubles are fine).</summary>
    public static class Wilson
    {
        /// <summary>z for a two-sided 95% interval.</summary>
        public const double Z95 = 1.959963984540054;

        public static (double low, double high) Interval(int successes, int total, double z = Z95)
        {
            if (total <= 0) return (0, 0);
            double n = total;
            double p = successes / n;
            double z2 = z * z;
            double denom = 1 + z2 / n;
            double centre = (p + z2 / (2 * n)) / denom;
            double half = z * Math.Sqrt(p * (1 - p) / n + z2 / (4 * n * n)) / denom;
            return (Math.Max(0, centre - half), Math.Min(1, centre + half));
        }
    }

    /// <summary>An elapsed-time retention window measured from the first valid session.</summary>
    public sealed class RetentionWindow
    {
        public string Label { get; }
        /// <summary>Start of the window, inclusive.</summary>
        public TimeSpan From { get; }
        /// <summary>End of the window, exclusive.</summary>
        public TimeSpan To { get; }

        public RetentionWindow(string label, TimeSpan from, TimeSpan to)
        {
            Label = label;
            From = from;
            To = to;
        }

        /// <summary>Definition label printed on every report so it is never mixed with a calendar-day definition.</summary>
        public const string Definition = "elapsed-window-from-first-valid-session (start inclusive, end exclusive)";

        public static readonly RetentionWindow D1 = new RetentionWindow("D1", TimeSpan.FromHours(24), TimeSpan.FromHours(48));
        public static readonly RetentionWindow D7 = new RetentionWindow("D7", TimeSpan.FromHours(168), TimeSpan.FromHours(192));
        public static readonly RetentionWindow D30 = new RetentionWindow("D30", TimeSpan.FromHours(720), TimeSpan.FromHours(744));
    }

    public sealed class RetentionResult
    {
        public RetentionWindow Window { get; }
        public Proportion Retained { get; }
        /// <summary>People whose window has not fully elapsed yet (excluded from the denominator).</summary>
        public int Immature { get; }
        /// <summary>True when the mature cohort is below the configured minimum: report "insufficient evidence".</summary>
        public bool InsufficientEvidence { get; }

        public RetentionResult(RetentionWindow window, Proportion retained, int immature, bool insufficient)
        {
            Window = window;
            Retained = retained;
            Immature = immature;
            InsufficientEvidence = insufficient;
        }
    }

    /// <summary>
    /// D1/D7/D30 retention (plan: "Metric definitions"): the cohort is real people whose first valid
    /// foreground session falls in [cohortStart, cohortEnd); a person is retained when another valid
    /// foreground session starts inside the window; only people whose whole window has elapsed by
    /// <c>asOf</c> are counted. Bot, automation, internal and test-client sessions never count.
    /// </summary>
    public static class RetentionCalculator
    {
        public static RetentionResult Compute(IEnumerable<AnalyticsEvent> events, RetentionWindow window, DateTimeOffset asOf,
            DateTimeOffset? cohortStart = null, DateTimeOffset? cohortEnd = null, CohortSource? cohort = null, int minimumCohort = 30)
        {
            Dictionary<string, List<DateTimeOffset>> sessions = events
                .Where(e => e.Type == AnalyticsEventType.ValidSession && e.IsRealPerson && e.Boolean("foreground") == true)
                .Where(e => !cohort.HasValue || e.Cohort == cohort.Value)
                .GroupBy(e => e.EventId).Select(g => g.First()) // ingestion duplicates
                .GroupBy(e => e.AnalyticsId)
                .ToDictionary(g => g.Key, g => g.Select(e => e.OccurredAt).OrderBy(t => t).ToList());

            int mature = 0, retained = 0, immature = 0;
            foreach (List<DateTimeOffset> times in sessions.Values)
            {
                DateTimeOffset first = times[0];
                if (cohortStart.HasValue && first < cohortStart.Value) continue;
                if (cohortEnd.HasValue && first >= cohortEnd.Value) continue;
                if (first + window.To > asOf)
                {
                    immature++;
                    continue;
                }
                mature++;
                DateTimeOffset from = first + window.From, to = first + window.To;
                if (times.Any(t => t >= from && t < to)) retained++;
            }
            return new RetentionResult(window, new Proportion(retained, mature), immature, mature < minimumCohort);
        }
    }

    /// <summary>
    /// An authoritative match record summary (from the match service's records, not from analytics).
    /// <para>Server integration: the match service writes one per finished or aborted match.</para>
    /// </summary>
    public sealed class MatchRecordSummary
    {
        public string MatchId { get; }
        public MatchKind Kind { get; }
        public MatchEnding Ending { get; }
        public int HumanSeats { get; }
        public int Rounds { get; }
        public DateTimeOffset StartedAt { get; }
        public bool IsAutomation { get; }
        public bool IsInternal { get; }

        public MatchRecordSummary(string matchId, MatchKind kind, MatchEnding ending, int humanSeats, int rounds, DateTimeOffset startedAt,
            bool isAutomation = false, bool isInternal = false)
        {
            MatchId = matchId;
            Kind = kind;
            Ending = ending;
            HumanSeats = humanSeats;
            Rounds = rounds;
            StartedAt = startedAt;
            IsAutomation = isAutomation;
            IsInternal = isInternal;
        }

        public bool HumanStarted => HumanSeats > 0 && !IsAutomation && !IsInternal;
        public bool NormallyCompleted => Ending == MatchEnding.EarlyVictory || Ending == MatchEnding.RoundsComplete;
    }

    public sealed class CompletionBreakdown
    {
        public string Segment { get; }
        public Proportion Completion { get; }
        public IReadOnlyDictionary<MatchEnding, int> ByEnding { get; }

        public CompletionBreakdown(string segment, Proportion completion, IReadOnlyDictionary<MatchEnding, int> byEnding)
        {
            Segment = segment;
            Completion = completion;
            ByEnding = byEnding;
        }
    }

    /// <summary>
    /// Match completion for human-started matches, split by mode and reason. Forfeits (voluntary and
    /// lock-timeout) and technical aborts are reported separately and never count as completed, even
    /// when the server awarded a winner.
    /// </summary>
    public static class CompletionCalculator
    {
        public static IReadOnlyList<CompletionBreakdown> Compute(IEnumerable<MatchRecordSummary> records)
        {
            List<MatchRecordSummary> human = records.Where(r => r.HumanStarted).GroupBy(r => r.MatchId).Select(g => g.First()).ToList();
            var result = new List<CompletionBreakdown> { Breakdown("all-human-started", human) };
            result.Add(Breakdown("human-vs-human", human.Where(r => r.Kind == MatchKind.OnlineHuman || r.Kind == MatchKind.LocalSharedPhone)));
            result.Add(Breakdown("human-vs-bot", human.Where(r => r.Kind == MatchKind.Practice || r.Kind == MatchKind.OnlineBot)));
            foreach (MatchKind k in Enum.GetValues(typeof(MatchKind))) result.Add(Breakdown("mode:" + k, human.Where(r => r.Kind == k)));
            return result;
        }

        private static CompletionBreakdown Breakdown(string name, IEnumerable<MatchRecordSummary> rows)
        {
            List<MatchRecordSummary> list = rows.ToList();
            var byEnding = new Dictionary<MatchEnding, int>();
            foreach (MatchEnding e in Enum.GetValues(typeof(MatchEnding))) byEnding[e] = list.Count(r => r.Ending == e);
            return new CompletionBreakdown(name, new Proportion(list.Count(r => r.NormallyCompleted), list.Count), byEnding);
        }
    }

    public sealed class CrashFreeResult
    {
        public Proportion CrashFree { get; }
        public int CriticalCrashSessions { get; }
        public IReadOnlyList<string> CriticalSessionIds { get; }

        public CrashFreeResult(Proportion crashFree, IReadOnlyList<string> critical)
        {
            CrashFree = crashFree;
            CriticalSessionIds = critical;
            CriticalCrashSessions = critical.Count;
        }
    }

    /// <summary>
    /// Crash-free sessions. Denominator: every instrumented real-user session in the cohort (any
    /// session with a valid-session or crash event, including sessions that crash before a match).
    /// Numerator: those without a crash event. Critical crashes are listed for separate review.
    /// </summary>
    public static class CrashFreeCalculator
    {
        public static CrashFreeResult Compute(IEnumerable<AnalyticsEvent> events, CohortSource? cohort = null)
        {
            List<AnalyticsEvent> real = events.Where(e => e.IsRealPerson && (!cohort.HasValue || e.Cohort == cohort.Value))
                .Where(e => e.Type == AnalyticsEventType.ValidSession || e.Type == AnalyticsEventType.Crash).ToList();
            var sessions = new HashSet<string>(real.Select(e => e.SessionId));
            var crashed = new HashSet<string>(real.Where(e => e.Type == AnalyticsEventType.Crash).Select(e => e.SessionId));
            List<string> critical = real.Where(e => e.Type == AnalyticsEventType.Crash && e.Boolean("critical") == true)
                .Select(e => e.SessionId).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
            return new CrashFreeResult(new Proportion(sessions.Count - crashed.Count, sessions.Count), critical);
        }
    }

    public sealed class MatchesPerActivePlayer
    {
        /// <summary>Matches per (person, active day) → number of person-days.</summary>
        public IReadOnlyDictionary<int, int> Distribution { get; }
        public double Mean { get; }
        public int PersonDays { get; }

        public MatchesPerActivePlayer(IReadOnlyDictionary<int, int> distribution, double mean, int personDays)
        {
            Distribution = distribution;
            Mean = mean;
            PersonDays = personDays;
        }

        /// <summary>
        /// From valid-session (activity) and match-end events of real people; a day is a UTC+offset day.
        /// Active days with no match count as zero.
        /// </summary>
        public static MatchesPerActivePlayer Compute(IEnumerable<AnalyticsEvent> events, TimeSpan dayOffset)
        {
            List<AnalyticsEvent> real = events.Where(e => e.IsRealPerson).GroupBy(e => e.EventId).Select(g => g.First()).ToList();
            string Day(DateTimeOffset t) => t.ToOffset(dayOffset).Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var personDays = new HashSet<string>(real.Where(e => e.Type == AnalyticsEventType.ValidSession).Select(e => e.AnalyticsId + "|" + Day(e.OccurredAt)));
            Dictionary<string, int> counts = real.Where(e => e.Type == AnalyticsEventType.MatchEnd)
                .GroupBy(e => e.AnalyticsId + "|" + Day(e.OccurredAt)).ToDictionary(g => g.Key, g => g.Count());
            foreach (string k in counts.Keys) personDays.Add(k);
            var dist = new SortedDictionary<int, int>();
            long total = 0;
            foreach (string pd in personDays)
            {
                int n = counts.TryGetValue(pd, out int c) ? c : 0;
                total += n;
                dist[n] = dist.TryGetValue(n, out int x) ? x + 1 : 1;
            }
            return new MatchesPerActivePlayer(dist, personDays.Count == 0 ? 0 : (double)total / personDays.Count, personDays.Count);
        }
    }
}
