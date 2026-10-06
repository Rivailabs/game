using System;
using System.Collections.Generic;

namespace AstraKingdoms.V2.Ranked
{
    /// <summary>The four visible leagues (plan: Bronze, Silver, Gold and Diamond).</summary>
    public enum League : byte
    {
        Bronze = 0,
        Silver = 1,
        Gold = 2,
        Diamond = 3,
    }

    /// <summary>
    /// Versioned ranked constants. Every number is PROPOSED and needs balance approval (plan: "The
    /// exact expected-score formula, league thresholds and per-match bounds require balance approval").
    /// </summary>
    public sealed class RankedRules
    {
        public const string CurrentVersion = "AK-RANKED-1";

        public string Version { get; }
        public int PlacementMatches { get; }
        /// <summary>Starting hidden skill rating and visible season rating for a new account.</summary>
        public int StartRating { get; }
        /// <summary>Lower visible-rating bounds of Silver, Gold and Diamond (Bronze starts at 0).</summary>
        public int SilverFrom { get; }
        public int GoldFrom { get; }
        public int DiamondFrom { get; }
        /// <summary>K factor while the player is in this season's placement matches.</summary>
        public int PlacementK { get; }
        /// <summary>K factor after placement.</summary>
        public int K { get; }
        /// <summary>Per-match bound: no single result moves a rating by more than this many points.</summary>
        public int MaxDeltaPerMatch { get; }
        /// <summary>A decisive result always moves both ratings by at least this much (avoids zero-point wins).</summary>
        public int MinDecisiveDelta { get; }

        public RankedRules(string version, int placementMatches, int startRating, int silverFrom, int goldFrom, int diamondFrom,
            int placementK, int k, int maxDeltaPerMatch, int minDecisiveDelta)
        {
            if (!(0 < silverFrom && silverFrom < goldFrom && goldFrom < diamondFrom)) throw new ArgumentException("league thresholds must increase");
            if (placementMatches < 0 || k <= 0 || placementK <= 0 || maxDeltaPerMatch <= 0 || minDecisiveDelta < 0 || minDecisiveDelta > maxDeltaPerMatch)
                throw new ArgumentException("invalid ranked constants");
            Version = version;
            PlacementMatches = placementMatches;
            StartRating = startRating;
            SilverFrom = silverFrom;
            GoldFrom = goldFrom;
            DiamondFrom = diamondFrom;
            PlacementK = placementK;
            K = k;
            MaxDeltaPerMatch = maxDeltaPerMatch;
            MinDecisiveDelta = minDecisiveDelta;
        }

        /// <summary>PROPOSED: five placement matches; Silver 1100, Gold 1250, Diamond 1400; K 48 in placement, 24 after; at most 40 points per match.</summary>
        public static readonly RankedRules Default = new RankedRules(CurrentVersion, 5, 1000, 1100, 1250, 1400, 48, 24, 40, 1);

        public League LeagueFor(int seasonRating)
        {
            if (seasonRating >= DiamondFrom) return League.Diamond;
            if (seasonRating >= GoldFrom) return League.Gold;
            if (seasonRating >= SilverFrom) return League.Silver;
            return League.Bronze;
        }

        public int LowerBound(League league)
        {
            switch (league)
            {
                case League.Diamond: return DiamondFrom;
                case League.Gold: return GoldFrom;
                case League.Silver: return SilverFrom;
                default: return 0;
            }
        }

        /// <summary>
        /// Soft reset of the <b>visible</b> season rating at a season boundary: the visible league moves
        /// down by at most one league (Bronze stays Bronze). A Silver-or-better player starts the new
        /// season at the lower bound of the league below; a player who drops into Bronze starts at the
        /// starting rating (or keeps a lower Bronze rating); a Bronze player keeps the rating. The hidden
        /// skill rating used for matchmaking is never reset, so skill information is retained.
        /// </summary>
        public int SoftResetRating(int previousSeasonRating)
        {
            League previous = LeagueFor(previousSeasonRating);
            if (previous == League.Bronze) return previousSeasonRating;
            var target = (League)(previous - 1);
            if (target == League.Bronze) return Math.Min(StartRating, SilverFrom - 1);
            return LowerBound(target);
        }
    }

    /// <summary>The result of one rated match from one player's point of view.</summary>
    public enum RatedOutcome : byte
    {
        Loss = 0,
        Draw = 1,
        Win = 2,
    }

    /// <summary>
    /// Integer Elo rating update.
    /// <para><b>Choice: Elo, not Glicko-2.</b> Glicko-2 needs exp/log/sqrt on doubles and a rating period
    /// model; Elo with a published table is fully integer, reproducible on any server, easy to audit
    /// when a player disputes a change, and good enough for a two-player game with a hidden
    /// matchmaking rating. Uncertainty is approximated by the larger placement K factor.</para>
    /// <para><b>Expected score.</b> E_A = 1 / (1 + 10^((R_B − R_A) / 400)), the standard logistic Elo
    /// curve. It is evaluated from <see cref="LowerExpectedPpm"/>: E of the lower-rated player, in parts
    /// per million, at rating differences 0, 25, ..., 800 (each entry is round(1e6 / (1 + 10^(d/400)))),
    /// linearly interpolated between entries; differences above 800 are clamped to 800. A test checks
    /// the table against the closed form.</para>
    /// <para><b>Update.</b> Δ = round_half_away_from_zero(K × (S − E)), S = 1 / 0.5 / 0 for win / draw /
    /// loss; then |Δ| ≤ MaxDeltaPerMatch, and a win gains (a loss costs) at least MinDecisiveDelta.
    /// Only the outcome enters the formula: damage, money spent and ads are not inputs.</para>
    /// </summary>
    public static class EloRating
    {
        public const int Scale = 1000000;
        public const int TableStep = 25;
        public const int MaxDifference = 800;

        public static readonly IReadOnlyList<int> LowerExpectedPpm = new[]
        {
            500000, 464084, 428537, 393712, 359935, 327490, 296615, 267493, 240253, 214973, 191682, 170367, 150980, 133443, 117662, 103523,
            90909, 79695, 69758, 60978, 53240, 46435, 40463, 35231, 30653, 26654, 23164, 20122, 17472, 15166, 13160, 11416, 9901,
        };

        /// <summary>Expected score of a player rated <paramref name="rating"/> against <paramref name="opponent"/>, in parts per million.</summary>
        public static int ExpectedPpm(int rating, int opponent)
        {
            int diff = Math.Min(MaxDifference, Math.Abs(rating - opponent));
            int index = diff / TableStep;
            int remainder = diff % TableStep;
            int low = LowerExpectedPpm[index];
            int interpolated = remainder == 0 ? low : low + (LowerExpectedPpm[index + 1] - low) * remainder / TableStep;
            return rating >= opponent ? Scale - interpolated : interpolated;
        }

        /// <summary>The bounded rating change for one player.</summary>
        public static int Delta(int rating, int opponent, RatedOutcome outcome, int k, RankedRules rules)
        {
            long s = outcome == RatedOutcome.Win ? Scale : outcome == RatedOutcome.Draw ? Scale / 2 : 0;
            long numerator = (long)k * (s - ExpectedPpm(rating, opponent));
            long delta = numerator >= 0 ? (numerator + Scale / 2) / Scale : -((-numerator + Scale / 2) / Scale);
            if (outcome == RatedOutcome.Win) delta = Math.Max(delta, rules.MinDecisiveDelta);
            if (outcome == RatedOutcome.Loss) delta = Math.Min(delta, -rules.MinDecisiveDelta);
            return (int)Math.Max(-rules.MaxDeltaPerMatch, Math.Min(rules.MaxDeltaPerMatch, delta));
        }
    }
}
