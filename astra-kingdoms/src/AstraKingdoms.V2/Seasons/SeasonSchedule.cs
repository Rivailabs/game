using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.V2.Seasons
{
    /// <summary>Versioned season timing rules (plan: "Retain the accepted 18-day season"; other values PROPOSED).</summary>
    public sealed class SeasonRules
    {
        public const string CurrentVersion = "AK-SEASON-1";

        public string Version { get; }
        public TimeSpan Length { get; }
        /// <summary>New pass sales stop this long before the season ends (plan: final 24 hours).</summary>
        public TimeSpan PassSalesStopBeforeEnd { get; }
        /// <summary>Added to the measured maximum match duration when closing matchmaking.</summary>
        public TimeSpan MatchmakingSafetyMargin { get; }
        /// <summary>
        /// After the end, an unresolved old-season match may still report for this long; after that the
        /// published cancellation policy applies (technical incident: cancelled, no rating change).
        /// </summary>
        public TimeSpan ReconciliationGrace { get; }

        public SeasonRules(string version, TimeSpan length, TimeSpan passSalesStop, TimeSpan safetyMargin, TimeSpan reconciliationGrace)
        {
            if (length <= TimeSpan.Zero || passSalesStop < TimeSpan.Zero || passSalesStop >= length) throw new ArgumentException("invalid season timing");
            Version = version;
            Length = length;
            PassSalesStopBeforeEnd = passSalesStop;
            MatchmakingSafetyMargin = safetyMargin;
            ReconciliationGrace = reconciliationGrace;
        }

        public static readonly SeasonRules Default = new SeasonRules(CurrentVersion, TimeSpan.FromDays(18), TimeSpan.FromHours(24),
            TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30));
    }

    /// <summary>
    /// The longest a ranked match can take. Matchmaking closes before the season boundary by the
    /// <b>measured</b> maximum (from match-service telemetry) plus a margin. The rules-clock bound is
    /// computed here only as a sanity check and as the value used before any measurement exists.
    /// </summary>
    public static class MatchDurationBudget
    {
        /// <summary>
        /// Online rules-clock upper bound: per round the terrain announcement, three volleys of
        /// (choice deadline + resolution replay) and the online cut window; eight rounds. Network
        /// latency, reconnect grace and loading are not included, which is why the measured value wins.
        /// </summary>
        public static TimeSpan RulesClockUpperBound()
        {
            long perRoundMs = RulesConstants.TerrainAnnouncementMs
                              + (long)RulesConstants.MaxVolleys * (RulesConstants.ChoiceDeadlineMs + RulesConstants.ResolutionReplayMaxMs)
                              + RulesConstants.OnlineCutWindowMs;
            return TimeSpan.FromMilliseconds(perRoundMs * RulesConstants.MaxRounds);
        }

        /// <summary>The lead used to close matchmaking: measured maximum (or the rules bound when unmeasured) plus the margin.</summary>
        public static TimeSpan CloseLead(TimeSpan? measuredMaximum, SeasonRules rules)
        {
            TimeSpan basis = measuredMaximum.HasValue && measuredMaximum.Value > TimeSpan.Zero ? measuredMaximum.Value : RulesClockUpperBound();
            return basis + rules.MatchmakingSafetyMargin;
        }
    }

    /// <summary>Where a season is in its lifecycle.</summary>
    public enum SeasonPhase : byte
    {
        Scheduled = 0,
        /// <summary>Ranked queue open; pass on sale until the final 24 hours.</summary>
        Open = 1,
        /// <summary>No new ranked matches start; in-flight matches finish.</summary>
        MatchmakingClosed = 2,
        /// <summary>The boundary passed; old-season matches are being reconciled before final rewards.</summary>
        Ended = 3,
        /// <summary>Final rewards issued; the season is closed for good.</summary>
        Settled = 4,
    }

    /// <summary>One published season. Dates are published with the season and do not move.</summary>
    public sealed class SeasonDefinition
    {
        public string Id { get; }
        public int Number { get; }
        public DateTimeOffset StartsAt { get; }
        public DateTimeOffset EndsAt { get; }
        public DateTimeOffset MatchmakingClosesAt { get; }
        public DateTimeOffset PassSalesCloseAt { get; }
        /// <summary>The Play one-time product of this season's pass (one SKU per season).</summary>
        public string PassSku { get; }
        public string RulesVersion { get; }

        public SeasonDefinition(int number, DateTimeOffset startsAt, SeasonRules rules, TimeSpan? measuredMaxMatchDuration)
        {
            if (number < 1) throw new ArgumentOutOfRangeException(nameof(number));
            Number = number;
            Id = IdFor(number);
            StartsAt = startsAt.ToUniversalTime();
            EndsAt = StartsAt + rules.Length;
            MatchmakingClosesAt = EndsAt - MatchDurationBudget.CloseLead(measuredMaxMatchDuration, rules);
            PassSalesCloseAt = EndsAt - rules.PassSalesStopBeforeEnd;
            PassSku = PassSkuFor(number);
            RulesVersion = rules.Version;
            if (MatchmakingClosesAt <= StartsAt) throw new ArgumentException("matchmaking would close before the season starts");
        }

        public static string IdFor(int number) => "S" + number.ToString(CultureInfo.InvariantCulture);
        public static string PassSkuFor(int number) => "ak.pass.season_" + number.ToString("000", CultureInfo.InvariantCulture);

        /// <summary>Time-derived phase (Settled is recorded by the settlement service, not derived from time).</summary>
        public SeasonPhase PhaseAt(DateTimeOffset now, bool settled)
        {
            if (settled) return SeasonPhase.Settled;
            if (now < StartsAt) return SeasonPhase.Scheduled;
            if (now < MatchmakingClosesAt) return SeasonPhase.Open;
            if (now < EndsAt) return SeasonPhase.MatchmakingClosed;
            return SeasonPhase.Ended;
        }

        public bool Contains(DateTimeOffset t) => t >= StartsAt && t < EndsAt;

        public override string ToString() => Id + " " + StartsAt.ToString("u", CultureInfo.InvariantCulture) + " .. " + EndsAt.ToString("u", CultureInfo.InvariantCulture);
    }

    /// <summary>Consecutive published seasons. A new season starts exactly when the previous one ends.</summary>
    public sealed class SeasonCalendar
    {
        private readonly List<SeasonDefinition> _seasons = new List<SeasonDefinition>();

        public SeasonRules Rules { get; }
        public IReadOnlyList<SeasonDefinition> Seasons => _seasons;

        public SeasonCalendar(SeasonRules rules) => Rules = rules ?? SeasonRules.Default;

        /// <summary>Publishes the next season (back to back with the last one, or at <paramref name="firstStart"/>).</summary>
        public SeasonDefinition PublishNext(DateTimeOffset firstStart, TimeSpan? measuredMaxMatchDuration)
        {
            SeasonDefinition last = _seasons.LastOrDefault();
            var s = new SeasonDefinition(last == null ? 1 : last.Number + 1, last == null ? firstStart : last.EndsAt, Rules, measuredMaxMatchDuration);
            _seasons.Add(s);
            return s;
        }

        public SeasonDefinition Get(string id) => _seasons.FirstOrDefault(s => s.Id == id);
        public SeasonDefinition At(DateTimeOffset t) => _seasons.FirstOrDefault(s => s.Contains(t));
        public SeasonDefinition Previous(SeasonDefinition s) => _seasons.LastOrDefault(x => x.Number < s.Number);
        public SeasonDefinition Next(SeasonDefinition s) => _seasons.FirstOrDefault(x => x.Number == s.Number + 1);
    }
}
