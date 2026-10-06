using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AstraKingdoms.Meta.Billing;
using AstraKingdoms.Meta.Common;
using AstraKingdoms.Meta.Progression;
using AstraKingdoms.Meta.Shop;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.V2.Common;
using AstraKingdoms.V2.Integration;
using AstraKingdoms.V2.Kingdom;
using AstraKingdoms.V2.Ranked;

namespace AstraKingdoms.V2.Seasons
{
    /// <summary>Inputs of one rehearsal run. Deterministic for a given value.</summary>
    public sealed class RehearsalOptions
    {
        public ulong Seed { get; set; } = 20261006;
        public int Players { get; set; } = 24;
        public int Seasons { get; set; } = 2;
        /// <summary>The measured maximum match duration fed into the matchmaking close (PROPOSED test value).</summary>
        public TimeSpan MeasuredMaxMatch { get; set; } = TimeSpan.FromMinutes(8);
        public TimeSpan Step { get; set; } = TimeSpan.FromHours(1);
        /// <summary>Chance (percent) that an idle player joins the ranked queue in a step.</summary>
        public int QueueChancePercent { get; set; } = 10;
        public DateTimeOffset FirstStart { get; set; } = new DateTimeOffset(2027, 1, 4, 0, 0, 0, TimeSpan.Zero);
    }

    public sealed class RehearsalCheck
    {
        public string Name { get; }
        public bool Passed { get; }
        public string Detail { get; }

        public RehearsalCheck(string name, bool passed, string detail = null)
        {
            Name = name;
            Passed = passed;
            Detail = detail ?? string.Empty;
        }

        public override string ToString() => (Passed ? "PASS " : "FAIL ") + Name + (Detail.Length > 0 ? " - " + Detail : string.Empty);
    }

    public sealed class RehearsalSeasonReport
    {
        public string SeasonId { get; set; }
        public int Tickets { get; set; }
        public int Rated { get; set; }
        public int Cancelled { get; set; }
        public int Duplicates { get; set; }
        public int ExcludedCasual { get; set; }
        public int LeagueRewards { get; set; }
        public int PassFreeDelivered { get; set; }
        public int PassPaidDelivered { get; set; }
        public int PassBuyers { get; set; }
        public int LatePaidDelivered { get; set; }
        public List<RehearsalCheck> Checks { get; } = new List<RehearsalCheck>();
    }

    public sealed class RehearsalReport
    {
        public List<RehearsalSeasonReport> Seasons { get; } = new List<RehearsalSeasonReport>();
        public List<RehearsalCheck> CrossSeasonChecks { get; } = new List<RehearsalCheck>();
        /// <summary>SHA-256 over final standings, skills and grant keys: equal runs give equal digests.</summary>
        public string Digest { get; set; }

        public IEnumerable<RehearsalCheck> AllChecks => Seasons.SelectMany(s => s.Checks).Concat(CrossSeasonChecks);
        public bool AllPassed => AllChecks.All(c => c.Passed);

        public string Describe()
        {
            var sb = new StringBuilder();
            foreach (RehearsalSeasonReport s in Seasons)
            {
                sb.Append(s.SeasonId).Append(": tickets=").Append(s.Tickets).Append(" rated=").Append(s.Rated).Append(" cancelled=").Append(s.Cancelled)
                    .Append(" duplicates=").Append(s.Duplicates).Append(" casualExcluded=").Append(s.ExcludedCasual).Append(" trophies=").Append(s.LeagueRewards)
                    .Append(" passFree=").Append(s.PassFreeDelivered).Append(" passPaid=").Append(s.PassPaidDelivered).Append(" buyers=").Append(s.PassBuyers)
                    .Append(" latePaid=").Append(s.LatePaidDelivered).Append('\n');
                foreach (RehearsalCheck c in s.Checks) sb.Append("  ").Append(c).Append('\n');
            }
            foreach (RehearsalCheck c in CrossSeasonChecks) sb.Append(c).Append('\n');
            sb.Append("digest ").Append(Digest);
            return sb.ToString();
        }
    }

    /// <summary>
    /// Full season-lifecycle rehearsal (plan: "V2 uses an 18-day season only after the full season
    /// lifecycle works in a test environment"; "Validate V2 with two rehearsed season settlements").
    /// It drives the real services end to end on a manual clock: publication and frozen snapshot, a
    /// simulated population queueing (solo and coordinated groups), matches finishing, duplicate
    /// result delivery, casual results that must not count, pass purchases through Meta's
    /// PurchaseService (a fake Play verifier), a purchase attempt in the final 24 hours, a pending
    /// payment completing after settlement, a lost match cancelled under the published policy, a late
    /// but in-grace result, settlement (twice), a re-sent result flood after settlement, and the soft
    /// reset into the next season. Every invariant is reported as a named check.
    /// </summary>
    public static class SeasonRehearsal
    {
        private sealed class InFlight
        {
            public RankedMatchTicket Ticket;
            public DateTimeOffset FinishAt;
            public bool Lost;
            public bool DeliverLate;
        }

        private sealed class SimPlayer
        {
            public string Id;
            public int TrueSkill;
            public string Group;
        }

        private const string Salt = "rehearsal-salt";

        public static RehearsalReport Run(RehearsalOptions options = null)
        {
            options = options ?? new RehearsalOptions();
            var clock = new ManualClock(options.FirstStart - TimeSpan.FromDays(1));
            var env = new V2Environment(clock, options.FirstStart, options.MeasuredMaxMatch);
            var rng = new DeterministicRandom(options.Seed);
            var verifier = new FakePurchaseVerifier();
            var report = new RehearsalReport();
            var players = new List<SimPlayer>();
            for (int i = 0; i < options.Players; i++)
                players.Add(new SimPlayer
                {
                    Id = "p" + i.ToString("00", CultureInfo.InvariantCulture),
                    TrueSkill = 850 + rng.NextInt(750),
                    Group = i < 4 ? "g1" : i < 8 ? "g2" : null,
                });

            Dictionary<string, int> previousFinal = null;
            string previousSeason = null;
            for (int n = 1; n <= options.Seasons; n++)
            {
                SeasonDefinition season = env.PublishNextSeason();
                var purchases = new PurchaseService(verifier, env.Entitlements, new InMemoryAcknowledgementQueue(), env.StoreWithPasses(), clock,
                    new BillingOptions { AccountIdSalt = Salt });
                RehearsalSeasonReport sr = RunSeason(env, season, players, rng, options, clock, verifier, purchases);
                report.Seasons.Add(sr);

                if (previousFinal != null)
                {
                    // Soft reset and retained skill, checked against the previous season's final values.
                    bool resetOk = true;
                    foreach (var kv in previousFinal)
                    {
                        SeasonStanding now = env.RankedStore.GetStanding(season.Id, kv.Key);
                        if (now == null) continue;
                        int expected = env.Ranked.Rules.SoftResetRating(kv.Value);
                        League before = env.Ranked.Rules.LeagueFor(kv.Value), after = env.Ranked.Rules.LeagueFor(now.StartRating);
                        if (now.StartRating != expected || before - after > 1 || after > before) resetOk = false;
                    }
                    report.CrossSeasonChecks.Add(new RehearsalCheck(season.Id + " soft reset moves the visible league down at most one", resetOk));
                    report.CrossSeasonChecks.Add(new RehearsalCheck(season.Id + " hidden skill retained across the boundary (equals the sum of all rated changes)",
                        SkillIsSumOfRatedDeltas(env, players.Select(p => p.Id))));
                    bool frozen = env.RankedStore.Standings(previousSeason).All(s => previousFinal.TryGetValue(s.PlayerId, out int r) && r == s.SeasonRating);
                    report.CrossSeasonChecks.Add(new RehearsalCheck(previousSeason + " standings unchanged after its settlement (no decay, no late edits)", frozen));
                }
                previousFinal = env.RankedStore.Standings(season.Id).ToDictionary(s => s.PlayerId, s => s.SeasonRating, StringComparer.Ordinal);
                previousSeason = season.Id;
            }

            // Backup/restore: re-importing the whole grant ledger adds nothing.
            int restored = env.Grants.Restore(env.Grants.Export());
            report.CrossSeasonChecks.Add(new RehearsalCheck("restoring the grant-ledger backup duplicates nothing", restored == 0, "added " + restored));
            bool trophiesUnique = players.All(p => env.Grants.ForPlayer(p.Id).Count(g => g.Source == GrantSource.SeasonLeagueReward) <= options.Seasons);
            report.CrossSeasonChecks.Add(new RehearsalCheck("at most one league reward per player per season", trophiesUnique));
            report.Digest = Digest(env, players);
            return report;
        }

        private static bool SkillIsSumOfRatedDeltas(V2Environment env, IEnumerable<string> players)
        {
            // The hidden skill is never reset: it only ever changes through rated results.
            List<ProcessedResult> rated = env.RankedStore.ProcessedResults().Where(r => r.Status == ResultStatus.Rated).ToList();
            foreach (string player in players)
            {
                int sum = rated.Sum(r => r.PlayerA == player ? r.DeltaA : r.PlayerB == player ? r.DeltaB : 0);
                if (env.Ranked.Skill(player).Rating != Math.Max(0, env.Ranked.Rules.StartRating + sum)) return false;
            }
            return true;
        }

        private static RehearsalSeasonReport RunSeason(V2Environment env, SeasonDefinition season, List<SimPlayer> players, DeterministicRandom rng,
            RehearsalOptions options, ManualClock clock, FakePurchaseVerifier verifier, PurchaseService purchases)
        {
            var sr = new RehearsalSeasonReport { SeasonId = season.Id };
            RankedMatchmaker mm = env.Matchmaker(season.Id);
            var inFlight = new List<InFlight>();
            var busy = new HashSet<string>(StringComparer.Ordinal);
            var allResults = new List<RankedMatchResult>();
            var buyers = new HashSet<string>(StringComparer.Ordinal);
            bool incidentChosen = false, lateChosen = false;
            int casualCounter = 0;
            string pendingToken = null;
            string pendingBuyer = players[3].Id;
            int buyDayBase = 1 + rng.NextInt(10);
            bool ticketAfterClose = false;
            bool finalDayBlocked = false;

            for (DateTimeOffset t = season.StartsAt; t < season.EndsAt; t += options.Step)
            {
                // 1. Matches that finished before this step report in time order.
                foreach (InFlight f in inFlight.Where(x => x.Lost && x.FinishAt <= t))
                {
                    busy.Remove(f.Ticket.PlayerA); // the players were disconnected; the result never arrives
                    busy.Remove(f.Ticket.PlayerB);
                }
                foreach (InFlight f in inFlight.Where(x => !x.Lost && !x.DeliverLate && x.FinishAt <= t).OrderBy(x => x.FinishAt).ThenBy(x => x.Ticket.MatchId, StringComparer.Ordinal).ToList())
                {
                    clock.Set(f.FinishAt);
                    Complete(env, f, players, rng, allResults, sr);
                    inFlight.Remove(f);
                    busy.Remove(f.Ticket.PlayerA);
                    busy.Remove(f.Ticket.PlayerB);
                }
                clock.Set(t);
                int day = (int)((t - season.StartsAt).Ticks / TimeSpan.TicksPerDay);

                // 2. Pass purchases (a quarter of the population, on a seeded day), the final-24h attempt and a pending payment.
                foreach (SimPlayer p in players)
                {
                    int idx = players.IndexOf(p);
                    if (idx % 4 == 1 && day == (buyDayBase + idx) % 15 && t.Hour == 12 && !buyers.Contains(p.Id))
                    {
                        if (Buy(env, season, p.Id, verifier, purchases, pending: false) != null) buyers.Add(p.Id);
                    }
                }
                if (pendingToken == null && t >= season.PassSalesCloseAt - TimeSpan.FromHours(6))
                    pendingToken = Buy(env, season, pendingBuyer, verifier, purchases, pending: true);
                if (!finalDayBlocked && t >= season.PassSalesCloseAt + TimeSpan.FromHours(1))
                {
                    PassCheckoutPreview late = env.Pass.Preview(players[2].Id, season.Id, AudienceProfile.Adult, null);
                    PurchaseAuthorization auth = env.Pass.AuthorizeCheckout(players[2].Id, season.Id, AudienceProfile.Adult, null, env.StoreWithPasses(), Salt);
                    finalDayBlocked = true;
                    sr.Checks.Add(new RehearsalCheck("pass sales stopped in the final 24 hours", late.Block == CheckoutBlock.SalesClosed && !auth.Allowed,
                        late.Block.ToString()));
                }

                // 3. Casual matches never touch ratings (but do count for pass points).
                if (rng.Chance(1, 4))
                {
                    SimPlayer a = players[rng.NextInt(players.Count)], b = players[rng.NextInt(players.Count)];
                    if (a != b)
                    {
                        string id = season.Id + "-casual" + (++casualCounter).ToString(CultureInfo.InvariantCulture);
                        ProcessedResult r = env.Ranked.Record(new RankedMatchResult(id, season.Id, QueueKind.Casual, null, a.Id, b.Id, PlayerSide.A,
                            MatchEnding.RoundsComplete, t));
                        if (r.Status == ResultStatus.ExcludedNotRanked) sr.ExcludedCasual++;
                        Report(env, id, a.Id, PlayerOutcome.Win, MatchEnding.RoundsComplete, rng, t);
                        Report(env, id, b.Id, PlayerOutcome.Loss, MatchEnding.RoundsComplete, rng, t);
                    }
                }

                // 4. Queue and pair.
                foreach (SimPlayer p in players)
                    if (!busy.Contains(p.Id) && rng.Chance(options.QueueChancePercent, 100))
                    {
                        string group = p.Group != null && rng.Chance(1, 3) ? p.Group : null;
                        mm.Enqueue(p.Id, env.Snapshots.Current(season.Id).SnapshotId, group);
                    }
                foreach (RankedMatchTicket ticket in mm.Tick())
                {
                    if (t >= season.MatchmakingClosesAt) ticketAfterClose = true;
                    sr.Tickets++;
                    busy.Add(ticket.PlayerA);
                    busy.Add(ticket.PlayerB);
                    long span = Math.Max(1, (options.MeasuredMaxMatch - TimeSpan.FromMinutes(3)).Ticks);
                    var f = new InFlight { Ticket = ticket, FinishAt = t + TimeSpan.FromMinutes(3) + TimeSpan.FromTicks((long)(rng.NextULong() % (ulong)span)) };
                    if (!incidentChosen && day >= 9) { f.Lost = true; incidentChosen = true; }
                    // One of the last matches before the close reports after the boundary (network delay) but within grace.
                    else if (!lateChosen && t + TimeSpan.FromHours(6) >= season.MatchmakingClosesAt) { f.DeliverLate = true; lateChosen = true; }
                    inFlight.Add(f);
                }
            }

            // Every honest match finished before the boundary: the close lead covers the measured maximum.
            foreach (InFlight f in inFlight.Where(x => !x.Lost && !x.DeliverLate).OrderBy(x => x.FinishAt).ToList())
            {
                clock.Set(f.FinishAt);
                Complete(env, f, players, rng, allResults, sr);
                inFlight.Remove(f);
            }
            sr.Checks.Add(new RehearsalCheck("no ranked match created after matchmaking closed", !ticketAfterClose));
            bool closeBeforeEnd = season.MatchmakingClosesAt + options.MeasuredMaxMatch < season.EndsAt;
            sr.Checks.Add(new RehearsalCheck("matchmaking closes at least the measured maximum duration before the boundary", closeBeforeEnd,
                (season.EndsAt - season.MatchmakingClosesAt).ToString()));

            // After the boundary: a late but in-grace result still counts for the old season.
            clock.Set(season.EndsAt + TimeSpan.FromMinutes(5));
            bool lateCounted = true;
            string lateDetail = string.Empty;
            foreach (InFlight f in inFlight.Where(x => x.DeliverLate).ToList())
            {
                ResultStatus lateStatus = Complete(env, f, players, rng, allResults, sr).Status;
                lateDetail += lateStatus + " ";
                lateCounted &= lateStatus == ResultStatus.Rated;
                inFlight.Remove(f);
            }
            sr.Checks.Add(new RehearsalCheck("a result delivered after the boundary but within grace counts for the old season", lateChosen && lateCounted, lateDetail));
            clock.Set(season.EndsAt + TimeSpan.FromMinutes(10));
            SettlementReport early = env.Seasons.Settle(season.Id);
            sr.Checks.Add(new RehearsalCheck("settlement waits while an old-season match is unreconciled",
                early.Status == SettlementStatus.AwaitingReconciliation && early.UnresolvedMatchIds.Count == inFlight.Count(x => x.Lost)));
            bool earlyGrantsNone = env.Grants.Export().All(g => g.Source != GrantSource.SeasonLeagueReward || !g.Reference.StartsWith(season.Id + "/", StringComparison.Ordinal));
            sr.Checks.Add(new RehearsalCheck("no final rewards before reconciliation", earlyGrantsNone));

            clock.Set(season.EndsAt + env.Calendar.Rules.ReconciliationGrace);
            SettlementReport settled = env.Seasons.Settle(season.Id);
            sr.LeagueRewards = settled.LeagueRewards;
            sr.PassFreeDelivered = settled.PassDeliveries.FreeDelivered;
            sr.PassPaidDelivered = settled.PassDeliveries.PaidDelivered;
            sr.Cancelled += settled.CancelledByPolicy.Count;
            sr.Checks.Add(new RehearsalCheck("settled after the reconciliation grace", settled.Status == SettlementStatus.Settled, settled.Status.ToString()));
            sr.Checks.Add(new RehearsalCheck("the lost match was cancelled under the published policy", settled.CancelledByPolicy.Count == 1));
            sr.Checks.Add(new RehearsalCheck("every ticket is resolved", env.RankedStore.Unresolved(season.Id).Count == 0));
            int processedForSeason = env.RankedStore.ProcessedResults().Count(r => r.Key.StartsWith("ranked:" + season.Id + ":" + season.Id + "-m", StringComparison.Ordinal));
            sr.Checks.Add(new RehearsalCheck("processed results reconcile with tickets", processedForSeason == sr.Tickets, processedForSeason + " vs " + sr.Tickets));

            // Idempotency after settlement.
            int grantsAfter = env.Grants.Count;
            SettlementReport again = env.Seasons.Settle(season.Id);
            sr.Checks.Add(new RehearsalCheck("settling twice changes nothing", again.Status == SettlementStatus.AlreadySettled && env.Grants.Count == grantsAfter));
            var ratingsBefore = env.RankedStore.Standings(season.Id).ToDictionary(s => s.PlayerId, s => s.SeasonRating, StringComparer.Ordinal);
            bool floodOk = true;
            foreach (RankedMatchResult r in allResults)
            {
                ResultStatus s = env.Ranked.Record(r).Status;
                if (s != ResultStatus.Duplicate && s != ResultStatus.SeasonSettled) floodOk = false;
            }
            floodOk &= env.RankedStore.Standings(season.Id).All(s => ratingsBefore[s.PlayerId] == s.SeasonRating);
            sr.Checks.Add(new RehearsalCheck("re-sent results after settlement change nothing", floodOk));

            // Rating bounds.
            int max = env.Ranked.Rules.MaxDeltaPerMatch;
            bool bounded = env.RankedStore.ProcessedResults().All(r => Math.Abs(r.DeltaA) <= max && Math.Abs(r.DeltaB) <= max);
            sr.Checks.Add(new RehearsalCheck("every rating change is within the per-match bound", bounded));

            // League rewards: exactly the placed players.
            bool leagueOk = env.RankedStore.Standings(season.Id).All(s =>
                (env.Grants.Find(GrantKeys.LeagueReward(season.Id, s.PlayerId)) != null) == s.PlacementDone(env.Ranked.Rules));
            sr.Checks.Add(new RehearsalCheck("league rewards go to exactly the players who completed placement", leagueOk));

            // Late purchase: the pending payment completes after settlement and still delivers earned paid rewards.
            if (pendingToken != null)
            {
                verifier.CompletePending(pendingToken);
                PurchaseResult pr = purchases.HandlePurchaseAsync(pendingBuyer, season.PassSku, pendingToken).GetAwaiter().GetResult();
                DeliveryReport late = env.Pass.DeliverPaidForOwner(pendingBuyer, season.Id);
                sr.LatePaidDelivered = late.PaidDelivered;
                int earned = env.Pass.Pass(season.Id).EarnedTiers(env.Pass.Points(pendingBuyer, season.Id));
                sr.Checks.Add(new RehearsalCheck("a payment completed after settlement delivers the earned paid tiers",
                    pr.Status == PurchaseStatus.Granted && late.PaidDelivered == earned, pr.Status + " delivered " + late.PaidDelivered + "/" + earned));
                buyers.Add(pendingBuyer);
            }
            sr.PassBuyers = buyers.Count;

            // Pass: exactly the earned tiers, paid only for owners.
            bool passOk = true;
            SeasonPassDefinition pass = env.Pass.Pass(season.Id);
            foreach (SimPlayer p in players)
            {
                int points = env.Pass.Points(p.Id, season.Id);
                bool owns = env.Pass.OwnsPass(p.Id, season.Id);
                foreach (PassTier tier in pass.Tiers)
                {
                    bool earned = points >= tier.PointsRequired;
                    bool free = env.Grants.Find(GrantKeys.PassTier(season.Id, p.Id, tier.Tier, false)) != null;
                    bool paid = env.Grants.Find(GrantKeys.PassTier(season.Id, p.Id, tier.Tier, true)) != null;
                    if (free != (earned && tier.FreeRewardId != null)) passOk = false;
                    if (paid != (earned && owns)) passOk = false;
                }
            }
            sr.Checks.Add(new RehearsalCheck("pass rewards: every earned tier delivered, nothing unearned, paid only with the pass", passOk));
            sr.Checks.Add(new RehearsalCheck("casual results were excluded from ratings", sr.ExcludedCasual > 0));
            sr.Checks.Add(new RehearsalCheck("duplicate result deliveries were absorbed", sr.Duplicates > 0));
            return sr;
        }

        private static string Buy(V2Environment env, SeasonDefinition season, string playerId, FakePurchaseVerifier verifier, PurchaseService purchases, bool pending)
        {
            PurchaseAuthorization auth = env.Pass.AuthorizeCheckout(playerId, season.Id, AudienceProfile.Adult, null, env.StoreWithPasses(), Salt);
            if (!auth.Allowed) return null;
            string token = "tok-" + season.Id + "-" + playerId;
            verifier.AddPurchase(season.PassSku, token, new ProductPurchase(pending ? PlayPurchaseState.Pending : PlayPurchaseState.Purchased,
                AcknowledgementState.NotAcknowledged, 0, "GPA." + token, env.Clock.UtcNow, null, auth.ObfuscatedAccountId));
            PurchaseResult r = purchases.HandlePurchaseAsync(playerId, season.PassSku, token).GetAwaiter().GetResult();
            if (r.Status == PurchaseStatus.Granted || r.Status == PurchaseStatus.AlreadyOwned) env.Pass.DeliverPaidForOwner(playerId, season.Id);
            return token;
        }

        private static ProcessedResult Complete(V2Environment env, InFlight f, List<SimPlayer> players, DeterministicRandom rng, List<RankedMatchResult> all, RehearsalSeasonReport sr)
        {
            RankedMatchTicket t = f.Ticket;
            SimPlayer a = players.First(p => p.Id == t.PlayerA), b = players.First(p => p.Id == t.PlayerB);
            PlayerSide? winner;
            MatchEnding ending = MatchEnding.RoundsComplete;
            int roll = rng.NextInt(100);
            if (roll < 5) winner = null;
            else winner = rng.NextInt(EloRating.Scale) < EloRating.ExpectedPpm(a.TrueSkill, b.TrueSkill) ? PlayerSide.A : PlayerSide.B;
            if (winner != null && roll >= 97) ending = MatchEnding.TimeoutForfeit;
            else if (winner != null && roll >= 80) ending = MatchEnding.EarlyVictory;
            // CompletedAt is when the match ended, even when the result is delivered late.
            var result = new RankedMatchResult(t.MatchId, t.SeasonId, QueueKind.Ranked, t.SnapshotId, t.PlayerA, t.PlayerB, winner, ending, f.FinishAt);
            all.Add(result);
            ProcessedResult r = env.Ranked.Record(result);
            if (r.Status == ResultStatus.Rated) sr.Rated++;
            if (r.Status == ResultStatus.Cancelled) sr.Cancelled++;
            if (rng.Chance(1, 10) && env.Ranked.Record(result).Status == ResultStatus.Duplicate) sr.Duplicates++;
            PlayerOutcome oa = winner == null ? PlayerOutcome.Draw : winner == PlayerSide.A ? PlayerOutcome.Win : PlayerOutcome.Loss;
            PlayerOutcome ob = winner == null ? PlayerOutcome.Draw : winner == PlayerSide.B ? PlayerOutcome.Win : PlayerOutcome.Loss;
            Report(env, t.MatchId, t.PlayerA, oa, ending, rng, f.FinishAt);
            Report(env, t.MatchId, t.PlayerB, ob, ending, rng, f.FinishAt);
            return r;
        }

        private static void Report(V2Environment env, string matchId, string playerId, PlayerOutcome outcome, MatchEnding ending, DeterministicRandom rng,
            DateTimeOffset completedAt)
        {
            var weapons = new[] { 1 + rng.NextInt(20), 1 + rng.NextInt(20) };
            env.OnMatchReport(new MatchOutcomeReport(matchId, playerId, MatchKind.OnlineHuman, outcome, ending, completedAt, weapons, CatalogPreset.Full));
        }

        private static string Digest(V2Environment env, List<SimPlayer> players)
        {
            var sb = new StringBuilder();
            foreach (SeasonDefinition s in env.Calendar.Seasons)
                foreach (SeasonStanding st in env.RankedStore.Standings(s.Id))
                    sb.Append(s.Id).Append('|').Append(st.PlayerId).Append('|').Append(st.SeasonRating).Append('|').Append(st.Matches).Append('\n');
            foreach (SimPlayer p in players) sb.Append(p.Id).Append('=').Append(env.Ranked.Skill(p.Id).Rating).Append('\n');
            foreach (GrantRecord g in env.Grants.Export().OrderBy(g => g.Key, StringComparer.Ordinal)) sb.Append(g.Key).Append('\n');
            return StableHash.Sha256Hex(sb.ToString());
        }
    }
}
