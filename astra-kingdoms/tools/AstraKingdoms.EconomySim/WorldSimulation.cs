using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.World;
using AstraKingdoms.World.Challenges;
using AstraKingdoms.World.Economy;
using AstraKingdoms.World.Season;
using AstraKingdoms.World.Social;

namespace AstraKingdoms.EconomySim;

internal enum Activity { Casual, Regular, Grinder }

internal enum Role { Honest, RingMain, RingAlt }

/// <summary>A simulated player. Hidden skill drives encounter outcomes; the world never sees it.</summary>
internal sealed class SimPlayer
{
    public required string Id;
    public required double Skill;
    public required Activity Activity;
    public required Role Role;
    public required int JoinSeason;
    public required int JoinDay;
    public long JoinedMs;
    public bool Joined;
    public long DuelCoins;
    public int SinkPurchases;
    public long WorldCoinsAtDay14 = -1;
    public long DuelCoinsAtDay14 = -1;
    public int FirstSeasonTilesAtClose = -1;
    public int AttacksReceivedWhileNewcomer;
    public int AttacksReceivedFromVeteransWhileNewcomer;
    public bool Newcomer => JoinSeason > 1 || JoinDay > 0;
}

/// <summary>Everything one season of the simulation measured.</summary>
internal sealed class SeasonMetrics
{
    public int Season;
    public int ActivePlayers;
    public int Attempts;
    public int Accepted;
    public readonly Dictionary<string, int> Rejections = new(StringComparer.Ordinal);
    public int AttackerWins;
    public int DefenderWins;
    public int Draws;
    public int Expired;
    public int MaxLossesOneDay;
    public int MaxLossesSeason;
    public int AccountsAtSeasonCap;
    public int Members;
    public int AccountsBelowAllocation;
    public int MinTilesHeld;
    public int MaxTilesHeldHonest;
    public int RingMainTiles;
    public string RingMainRecognition = "-";
    public double GiniTiles;
    public double GiniWorldCoins;
    public double GiniAllCoins;
    public double Top10ShareAllCoins;
    public long WorldMinted;
    public long WorldSpent;
    public long DuelMinted;
    public int MaxWorldCoinsOneDay;
    public bool AllRestoredAfterRebuild;
    public int ReEntryOffers;
    public int Migrations;
}

internal sealed class SimulationResult
{
    public readonly List<SeasonMetrics> Seasons = new();
    public readonly List<SimPlayer> Players = new();
    public readonly List<string> InvariantViolations = new();
    public int RealEncounters;
    public int RealAttackerWins;
    public int RealDefenderWins;
    public int RealDraws;
    public double ModelAttackerWinRate;
    public int ModelEncounters;
}

/// <summary>
/// Drives the real world rules with simulated players. Modelling assumptions (all PROPOSED, all in
/// the report): activity mix, attack attempts per day, V1 duel coin income, defender presence,
/// outcome model (Elo on hidden skill; offline defence plays at a fixed bot strength), spending.
/// </summary>
internal sealed class WorldSimulation
{
    // ---- Modelling assumptions (sim-side, not world rules) ----
    public const int DuelCoinsWin = 10, DuelCoinsLoss = 4, DuelDailyCap = 50;   // A1: V1 duel income model
    public const double OfflineBotRating = 1000;                                   // A2: Normal defence bot strength
    public const double DrawRate = 0.05, AbandonRate = 0.02, DefenderOnlineRate = 0.3;
    public const int EloK = 24;

    private readonly SimOptions _o;
    private readonly BotRng _rng;
    private readonly AccountRegistry _accounts = new();
    private readonly EconomyLedger _economy = new();
    private readonly ShardDirectory _directory = new();
    private readonly AllianceDirectory _alliances;
    private readonly List<WorldShard> _shards = new();
    private readonly SimulationResult _result = new();
    private readonly byte[] _secret;
    private readonly Dictionary<string, SimPlayer> _byId = new(StringComparer.Ordinal);
    private int _requestCounter;
    private long _duelMinted;

    public static readonly long Day0 = WorldTime.StartOfUtcDay(20_000);

    public WorldSimulation(SimOptions options)
    {
        _o = options;
        _rng = new BotRng(options.Seed);
        _alliances = new AllianceDirectory(_accounts);
        _secret = new byte[32];
        for (int i = 0; i < 32; i++) _secret[i] = (byte)_rng.Next(256);
    }

    public SimulationResult Run()
    {
        CreatePlayers();
        var season = new WorldSeason(1, Day0);
        for (int s = 0; s < _o.Shards; s++)
        {
            var info = new ShardInfo("sim-" + s, "ap-south", _o.Players);
            _directory.AddShard(info);
            _shards.Add(new WorldShard(info, season, _accounts, _economy));
        }

        for (int seasonIndex = 1; seasonIndex <= _o.Seasons; seasonIndex++)
        {
            var m = new SeasonMetrics { Season = seasonIndex };
            long mintedBefore = _economy.TotalMinted, spentBefore = _economy.TotalSpent;
            long duelBefore = _duelMinted;
            for (int day = 0; day < WorldRules.SeasonDays; day++)
            {
                long dayStart = season.StartMs + day * WorldTime.MsPerDay;
                JoinArrivals(seasonIndex, day, dayStart + 8 * WorldTime.MsPerHour);
                PlayDay(m, seasonIndex, day, dayStart);
                foreach (WorldShard sh in _shards) m.Expired += sh.ExpireDue(dayStart + WorldTime.MsPerDay - 1);
                TrackDailyLimits(m, WorldTime.UtcDay(dayStart));
                if (day == 13) SnapshotNewcomers(seasonIndex);
            }
            foreach (WorldShard sh in _shards)
                foreach (string id in sh.Members)
                {
                    int lost = sh.LossesInSeason(id);
                    m.MaxLossesSeason = Math.Max(m.MaxLossesSeason, lost);
                    if (lost == WorldRules.MaxBorderLossesPerSeason) m.AccountsAtSeasonCap++;
                    m.Members++;
                }

            // Some players ask to move shard; it may only happen at the reconciled boundary.
            foreach (SimPlayer p in _result.Players.Where(p => p.Joined && p.Role == Role.Honest && _rng.Chance(2)))
            {
                string current = _directory.ActiveShardOf(p.Id)!;
                string target = _shards.Select(x => x.Info.ShardId).First(x => x != current || _shards.Count == 1);
                if (target != current) _directory.RequestMigration(p.Id, target);
            }

            SeasonBoundaryReport boundary = SeasonOperator.CloseAndRebuild(_directory, _shards, season.EndMs);
            m.Migrations = boundary.Migrations.Count(x => x.Applied);
            var tiles = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (SeasonSettlement st in boundary.Settlements)
                foreach (KeyValuePair<string, int> h in st.TilesHeld) tiles[h.Key] = h.Value;
            CloseMetrics(m, tiles, seasonIndex);
            m.WorldMinted = _economy.TotalMinted - mintedBefore;
            m.WorldSpent = _economy.TotalSpent - spentBefore;
            m.DuelMinted = _duelMinted - duelBefore;
            m.AllRestoredAfterRebuild = _shards.All(sh => sh.Members.All(id => sh.TilesOwnedBy(id) == WorldRules.BorderTilesPerAccount));
            m.ReEntryOffers = _result.Players.Count(p => p.Joined && _accounts.Get(p.Id)!.ReEntryOffered);
            if (!m.AllRestoredAfterRebuild) _result.InvariantViolations.Add("Season " + seasonIndex + ": borders not restored to 12.");
            if (m.MaxLossesOneDay > WorldRules.MaxBorderLossesPerUtcDay) _result.InvariantViolations.Add("Season " + seasonIndex + ": daily loss cap exceeded.");
            if (m.MaxLossesSeason > WorldRules.MaxBorderLossesPerSeason) _result.InvariantViolations.Add("Season " + seasonIndex + ": season loss cap exceeded.");
            if (m.MaxWorldCoinsOneDay > EconomyRules.DailyWorldCoinCap)
                _result.InvariantViolations.Add("Season " + seasonIndex + ": daily world coin cap exceeded.");
            _result.Seasons.Add(m);
            season = boundary.NextSeason;
        }
        return _result;
    }

    // ------------------------------------------------------------------ population

    private void CreatePlayers()
    {
        int ring = 1 + _o.RingAlts;
        int honest = _o.Players - ring;
        int seasonsForArrivals = _o.Seasons;
        for (int i = 0; i < honest; i++)
        {
            int roll = _rng.Next(100);
            Activity a = roll < 55 ? Activity.Casual : roll < 90 ? Activity.Regular : Activity.Grinder;
            bool newcomer = i >= honest * 8 / 10;
            int joinSeason = newcomer ? 1 + _rng.Next(seasonsForArrivals) : 1;
            int joinDay = newcomer ? (joinSeason == 1 ? 1 + _rng.Next(WorldRules.SeasonDays - 1) : _rng.Next(WorldRules.SeasonDays)) : 0;
            Add(new SimPlayer
            {
                Id = "p" + i.ToString("0000", System.Globalization.CultureInfo.InvariantCulture),
                Skill = 1000 + Normal() * 150,
                Activity = a,
                Role = Role.Honest,
                JoinSeason = joinSeason,
                JoinDay = joinDay,
            });
        }
        Add(new SimPlayer { Id = "ring-main", Skill = 1000, Activity = Activity.Grinder, Role = Role.RingMain, JoinSeason = 1, JoinDay = 0 });
        for (int i = 0; i < _o.RingAlts; i++)
            Add(new SimPlayer { Id = "ring-alt-" + i, Skill = 1000, Activity = Activity.Casual, Role = Role.RingAlt, JoinSeason = 1, JoinDay = 0 });
    }

    private void Add(SimPlayer p)
    {
        _result.Players.Add(p);
        _byId[p.Id] = p;
    }

    private void JoinArrivals(int season, int day, long nowMs)
    {
        int shard = 0;
        foreach (SimPlayer p in _result.Players)
        {
            if (p.Joined || p.JoinSeason != season || p.JoinDay != day) continue;
            // Players present from launch were created 30 days earlier (no starter protection left).
            long created = p.Newcomer ? nowMs : nowMs - 30 * WorldTime.MsPerDay;
            _accounts.Create(p.Id, created, 1000);
            string shardId = p.Role == Role.Honest ? _shards[shard++ % _shards.Count].Info.ShardId : _shards[0].Info.ShardId;
            string problem = _directory.Assign(p.Id, shardId);
            if (problem != null) { _result.InvariantViolations.Add("Admission failed: " + problem); continue; }
            _shards.First(x => x.Info.ShardId == shardId).Admit(p.Id);
            p.Joined = true;
            p.JoinedMs = nowMs;
            if (p.Role == Role.RingAlt)
            {
                // The alts publish a "live" defence and never show up: every ring encounter is a forfeit.
                WorldShard sh = ShardOf(p.Id);
                sh.PublishDefence(p.Id, DefencePublication.Create(new[] { 1 }, 0, BotDifficulty.Easy, DefenceMode.Live, null));
            }
            else if (p.Role == Role.Honest && _rng.Chance(30))
            {
                JoinSomeAlliance(p.Id, nowMs);
            }
        }
    }

    private void JoinSomeAlliance(string id, long nowMs)
    {
        for (int a = 0; a < 40; a++)
        {
            string alliance = "alliance-" + a;
            if (_alliances.MemberCount(alliance) == 0)
            {
                _alliances.Create(alliance, id, nowMs);
                return;
            }
            if (_alliances.MemberCount(alliance) >= WorldRules.AllianceMaxMembers) continue;
            string leader = _accounts.AllIds.First(x => _alliances.RoleOf(alliance, x) == AllianceRole.Leader);
            if (_alliances.Invite(leader, alliance, id, nowMs) == null && _alliances.Accept(id, alliance, nowMs) == null) return;
        }
    }

    // ------------------------------------------------------------------ one day

    private void PlayDay(SeasonMetrics m, int season, int day, long dayStart)
    {
        long now = dayStart + 9 * WorldTime.MsPerHour;
        long utcDay = WorldTime.UtcDay(now);
        var order = _result.Players.Where(p => p.Joined).ToList();
        for (int i = order.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        m.ActivePlayers = Math.Max(m.ActivePlayers, order.Count);
        foreach (SimPlayer p in order)
        {
            int presence = p.Activity == Activity.Casual ? 40 : p.Activity == Activity.Regular ? 80 : 100;
            if (p.Role == Role.RingAlt || !_rng.Chance(presence)) continue;
            PlayDuels(p);
            AccountSnapshot acct = _accounts.Get(p.Id)!;
            if (acct.IsStarterProtected(now))
            {
                _accounts.RecordTrainingEncounter(p.Id);
                if (p.Activity != Activity.Casual) _accounts.EndProtectionEarly(p.Id); // keen players opt out after 5 trainings
                continue;
            }
            if (p.Role == Role.RingMain) RingDay(m, p, now);
            else
            {
                int attempts = p.Activity == Activity.Casual ? 1 : p.Activity == Activity.Regular ? 2 : 4;
                for (int k = 0; k < attempts; k++) HonestAttack(m, p, now + k * 20 * 60_000L, season);
            }
            Spend(p, season, day);
            now += 1000;
        }
        foreach (SimPlayer p in order) m.MaxWorldCoinsOneDay = Math.Max(m.MaxWorldCoinsOneDay, _economy.EarnedOnDay(p.Id, utcDay));
    }

    private void PlayDuels(SimPlayer p)
    {
        int games = p.Activity == Activity.Casual ? 2 : p.Activity == Activity.Regular ? 5 : 15;
        int earned = 0;
        for (int g = 0; g < games && earned < DuelDailyCap; g++)
        {
            double pWin = Expected(p.Skill, 1000 + Normal() * 150);
            int coins = _rng.Next(1_000_000) < pWin * 1_000_000 ? DuelCoinsWin : DuelCoinsLoss;
            coins = Math.Min(coins, DuelDailyCap - earned);
            earned += coins;
        }
        p.DuelCoins += earned;
        _duelMinted += earned;
    }

    private void HonestAttack(SeasonMetrics m, SimPlayer attacker, long now, int season)
    {
        WorldShard shard = ShardOf(attacker.Id);
        IReadOnlyList<string> members = shard.Members;
        for (int tries = 0; tries < 6; tries++)
        {
            string target = members[_rng.Next(members.Count)];
            if (target == attacker.Id) continue;
            m.Attempts++;
            ChallengeReceipt r = shard.CreateChallenge(new ChallengeRequest(NextRequest(), attacker.Id, target), now);
            if (!r.Accepted)
            {
                m.Rejections[r.Code] = m.Rejections.TryGetValue(r.Code, out int n) ? n + 1 : 1;
                continue;
            }
            m.Accepted++;
            SimPlayer defender = _byId[target];
            AccountSnapshot aSnap = _accounts.Get(attacker.Id)!, dSnap = _accounts.Get(target)!;
            if (defender.Newcomer && defender.JoinSeason == season)
            {
                defender.AttacksReceivedWhileNewcomer++;
                if (aSnap.Band == ExperienceBand.Veteran && dSnap.Band == ExperienceBand.Newcomer) defender.AttacksReceivedFromVeteransWhileNewcomer++;
            }
            if (_rng.Chance((int)(AbandonRate * 100))) return; // attacker walks away: the reservation expires
            Resolve(m, shard, r.Challenge, attacker, defender, now);
            return;
        }
    }

    private void Resolve(SeasonMetrics m, WorldShard shard, Challenge c, SimPlayer attacker, SimPlayer defender, long now)
    {
        EncounterOutcome outcome;
        string resolutionId;
        if (_result.RealEncounters < _o.RealEncounters && c.DefenceSnapshot.Mode == DefenceMode.Automatic)
        {
            BotDifficulty d = attacker.Skill > 1100 ? BotDifficulty.Hard : attacker.Skill < 900 ? BotDifficulty.Easy : BotDifficulty.Normal;
            EncounterResult real = OfflineEncounter.RunAutomatic(c, _secret, seed => BotPlayer.Create(PlayerSide.A, d, seed));
            outcome = real.Outcome;
            resolutionId = real.ResolutionId;
            _result.RealEncounters++;
            if (outcome == EncounterOutcome.AttackerWins) _result.RealAttackerWins++;
            else if (outcome == EncounterOutcome.DefenderWins) _result.RealDefenderWins++;
            else _result.RealDraws++;
        }
        else
        {
            bool online = _rng.Chance((int)(DefenderOnlineRate * 100));
            double defence = online ? defender.Skill : OfflineBotRating;
            double pA = Expected(attacker.Skill, defence);
            int roll = _rng.Next(1_000_000);
            outcome = roll < DrawRate * 1_000_000 ? EncounterOutcome.Draw
                : roll < (DrawRate + (1 - DrawRate) * pA) * 1_000_000 ? EncounterOutcome.AttackerWins
                : EncounterOutcome.DefenderWins;
            resolutionId = "model/" + c.ChallengeId;
            _result.ModelEncounters++;
            if (outcome == EncounterOutcome.AttackerWins) _result.ModelAttackerWinRate++;
        }
        ChallengeReceipt done = shard.Resolve(c.ChallengeId, resolutionId, outcome, now + 5 * 60_000L);
        if (!done.Accepted) { _result.InvariantViolations.Add("Resolve rejected: " + done.Code); return; }
        if (outcome == EncounterOutcome.AttackerWins) m.AttackerWins++;
        else if (outcome == EncounterOutcome.DefenderWins) m.DefenderWins++;
        else m.Draws++;
        UpdateRatings(attacker, defender, outcome);
    }

    private void RingDay(SeasonMetrics m, SimPlayer main, long now)
    {
        WorldShard shard = ShardOf(main.Id);
        foreach (SimPlayer alt in _result.Players.Where(x => x.Role == Role.RingAlt && x.Joined))
        {
            m.Attempts++;
            ChallengeReceipt r = shard.CreateChallenge(new ChallengeRequest(NextRequest(), main.Id, alt.Id), now);
            if (!r.Accepted)
            {
                m.Rejections[r.Code] = m.Rejections.TryGetValue(r.Code, out int n) ? n + 1 : 1;
                continue;
            }
            m.Accepted++;
            // The alt's live defence never shows up: the AK-TR-1 timeout rule forfeits the match.
            shard.Resolve(r.Challenge.ChallengeId, "forfeit/" + r.Challenge.ChallengeId, EncounterOutcome.AttackerWins, now + 60_000);
            m.AttackerWins++;
        }
    }

    private void Spend(SimPlayer p, int season, int day)
    {
        foreach (KeyValuePair<string, int> sink in EconomyRules.Sinks.OrderByDescending(s => s.Value))
        {
            if (_economy.Balance(p.Id) >= sink.Value && _rng.Chance(40) &&
                _economy.Spend("spend/" + p.Id + "/" + season + "/" + day + "/" + sink.Key, p.Id, sink.Key))
                p.SinkPurchases++;
            if (p.DuelCoins >= sink.Value && _rng.Chance(25))
            {
                p.DuelCoins -= sink.Value;
                p.SinkPurchases++;
            }
        }
    }

    // ------------------------------------------------------------------ metrics

    private void TrackDailyLimits(SeasonMetrics m, long utcDay)
    {
        foreach (WorldShard sh in _shards)
            foreach (string id in sh.Members) m.MaxLossesOneDay = Math.Max(m.MaxLossesOneDay, sh.LossesOnDay(id, utcDay));
    }

    private void SnapshotNewcomers(int season)
    {
        foreach (SimPlayer p in _result.Players.Where(p => p.Joined && p.Newcomer && p.JoinSeason == season && p.WorldCoinsAtDay14 < 0))
        {
            p.WorldCoinsAtDay14 = _economy.Balance(p.Id);
            p.DuelCoinsAtDay14 = p.DuelCoins;
        }
    }

    private void CloseMetrics(SeasonMetrics m, Dictionary<string, int> tiles, int season)
    {
        var honest = tiles.Where(t => _byId[t.Key].Role == Role.Honest).ToList();
        m.AccountsBelowAllocation = tiles.Count(t => t.Value < WorldRules.BorderTilesPerAccount);
        m.MinTilesHeld = tiles.Values.DefaultIfEmpty(0).Min();
        m.MaxTilesHeldHonest = honest.Select(t => t.Value).DefaultIfEmpty(0).Max();
        m.RingMainTiles = tiles.TryGetValue("ring-main", out int rm) ? rm : 0;
        m.RingMainRecognition = EconomyRules.RecognitionFor(m.RingMainTiles) ?? "-";
        m.GiniTiles = Stats.Gini(tiles.Values.Select(v => (double)v));
        var joined = _result.Players.Where(p => p.Joined && p.Role != Role.RingAlt).ToList();
        m.GiniWorldCoins = Stats.Gini(joined.Select(p => (double)_economy.Balance(p.Id)));
        var all = joined.Select(p => (double)(_economy.Balance(p.Id) + p.DuelCoins)).ToList();
        m.GiniAllCoins = Stats.Gini(all);
        m.Top10ShareAllCoins = Stats.TopShare(all, 0.10);
        foreach (KeyValuePair<string, int> t in tiles)
        {
            SimPlayer p = _byId[t.Key];
            if (p.Newcomer && p.JoinSeason == season && p.FirstSeasonTilesAtClose < 0) p.FirstSeasonTilesAtClose = t.Value;
        }
    }

    // ------------------------------------------------------------------ helpers

    private void UpdateRatings(SimPlayer a, SimPlayer d, EncounterOutcome o)
    {
        // The simulation plays the ranked service that owns ratings; the world only reads them.
        AccountSnapshot sa = _accounts.Get(a.Id)!, sd = _accounts.Get(d.Id)!;
        double ea = Expected(sa.Rating, sd.Rating);
        double score = o == EncounterOutcome.AttackerWins ? 1 : o == EncounterOutcome.Draw ? 0.5 : 0;
        int delta = (int)Math.Round(EloK * (score - ea));
        _accounts.SetRating(a.Id, sa.Rating + delta);
        _accounts.SetRating(d.Id, sd.Rating - delta);
    }

    private WorldShard ShardOf(string id)
    {
        string shardId = _directory.ActiveShardOf(id)!;
        return _shards.First(s => s.Info.ShardId == shardId);
    }

    private string NextRequest() => "sim-req-" + (++_requestCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static double Expected(double ra, double rb) => 1.0 / (1.0 + Math.Pow(10, (rb - ra) / 400.0));

    /// <summary>Standard normal sample (Box-Muller on the seeded RNG; modelling only).</summary>
    private double Normal()
    {
        double u1 = (_rng.Next(1_000_000) + 1) / 1_000_001.0, u2 = _rng.Next(1_000_000) / 1_000_000.0;
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}

internal static class Stats
{
    /// <summary>Gini coefficient (0 = equal, 1 = one holder has everything).</summary>
    public static double Gini(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToArray();
        if (v.Length == 0) return 0;
        double sum = v.Sum();
        if (sum <= 0) return 0;
        double weighted = 0;
        for (int i = 0; i < v.Length; i++) weighted += (i + 1) * v[i];
        return 2 * weighted / (v.Length * sum) - (v.Length + 1.0) / v.Length;
    }

    public static double TopShare(IReadOnlyList<double> values, double fraction)
    {
        if (values.Count == 0) return 0;
        double sum = values.Sum();
        if (sum <= 0) return 0;
        int k = Math.Max(1, (int)Math.Ceiling(values.Count * fraction));
        return values.OrderByDescending(x => x).Take(k).Sum() / sum;
    }

    public static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToArray();
        if (v.Length == 0) return 0;
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }
}
