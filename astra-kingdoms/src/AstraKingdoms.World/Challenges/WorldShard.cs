using System;
using System.Collections.Generic;
using System.Globalization;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.World.Armies;
using AstraKingdoms.World.Economy;
using AstraKingdoms.World.Season;

namespace AstraKingdoms.World.Challenges
{
    public enum ShardSeasonState : byte
    {
        Open = 0,
        /// <summary>Season close in progress: no new challenges.</summary>
        Closing = 1,
        /// <summary>Settled and snapshotted; waiting for the next season's rebuild.</summary>
        Closed = 2,
    }

    /// <summary>Remaining border-loss allowance of a defender (committed losses plus open reservations count).</summary>
    public readonly struct LossAllowance
    {
        public readonly int DailyRemaining;
        public readonly int SeasonRemaining;

        public LossAllowance(int daily, int season)
        {
            DailyRemaining = daily;
            SeasonRemaining = season;
        }
    }

    /// <summary>
    /// The authoritative state of one shard for one season: border tiles, challenges, reservations,
    /// loss ledgers and the protection table (plan: "Challenges and offline defence" and "Protection
    /// armies and newcomer fairness"). Every public member takes one lock, so concurrent attacks can
    /// never reserve the same tile twice or reserve more than the defender's remaining loss allowance.
    /// <para>
    /// <b>Accounting rule (decision).</b> A challenge reserves one unit of the defender's allowance for
    /// the UTC day it was created and for the season. A successful attack consumes that reserved unit;
    /// any other ending releases it. Losses are therefore attributed to the UTC day on which their
    /// allowance was reserved, which keeps "at most two per UTC day" exact even when a ten-minute
    /// challenge crosses midnight.
    /// </para>
    /// </summary>
    public sealed class WorldShard
    {
        private readonly object _gate = new object();
        private readonly AccountRegistry _accounts;
        private readonly EconomyLedger _economy;
        private readonly HashSet<string> _members = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, BorderTile> _tiles = new Dictionary<string, BorderTile>(StringComparer.Ordinal);
        private readonly Dictionary<string, Challenge> _challenges = new Dictionary<string, Challenge>(StringComparer.Ordinal);
        private readonly Dictionary<string, Challenge> _byRequest = new Dictionary<string, Challenge>(StringComparer.Ordinal);
        private readonly Dictionary<string, DefencePublication> _defences = new Dictionary<string, DefencePublication>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _lossesOnDay = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _lossesInSeason = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _lastChallenge = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _allianceSuccesses = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _seasonEncounters = new Dictionary<string, int>(StringComparer.Ordinal);
        private long _challengeCounter;
        private SeasonSettlement _settlement;

        public ShardInfo Info { get; }
        public WorldSeason Season { get; private set; }
        public ShardSeasonState State { get; private set; }

        public WorldShard(ShardInfo info, WorldSeason season, AccountRegistry accounts, EconomyLedger economy)
        {
            Info = info ?? throw new ArgumentNullException(nameof(info));
            Season = season ?? throw new ArgumentNullException(nameof(season));
            _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
            _economy = economy ?? throw new ArgumentNullException(nameof(economy));
            if (info.RulesVersion != WorldRules.RulesId) throw new ArgumentException("Shard rules version differs from this build.", nameof(info));
            State = ShardSeasonState.Open;
        }

        // ------------------------------------------------------------------ membership and tiles

        /// <summary>
        /// Admits an account the shard directory has placed here and gives it the standard allocation of
        /// twelve border tiles. Its twelve homeland plots stay in the account and are never tiles.
        /// </summary>
        public string Admit(string accountId)
        {
            lock (_gate)
            {
                if (State != ShardSeasonState.Open) return "SEASON_NOT_OPEN";
                if (!_accounts.Exists(accountId)) return "UNKNOWN_ACCOUNT";
                if (!_members.Add(accountId)) return "ALREADY_ADMITTED";
                AllocateBorder(accountId);
                return null;
            }
        }

        private void AllocateBorder(string accountId)
        {
            for (int i = 1; i <= WorldRules.BorderTilesPerAccount; i++)
            {
                string id = accountId + "/border-" + i.ToString("00", CultureInfo.InvariantCulture);
                _tiles[id] = new BorderTile { TileId = id, HomeAccountId = accountId, OwnerId = accountId };
            }
        }

        public bool IsMember(string accountId)
        {
            lock (_gate) return _members.Contains(accountId);
        }

        public IReadOnlyList<string> Members
        {
            get
            {
                lock (_gate)
                {
                    var list = new List<string>(_members);
                    list.Sort(StringComparer.Ordinal);
                    return list;
                }
            }
        }

        public BorderTile Tile(string tileId)
        {
            lock (_gate) return _tiles.TryGetValue(tileId, out BorderTile t) ? t.Clone() : null;
        }

        public int TilesOwnedBy(string accountId)
        {
            lock (_gate)
            {
                int n = 0;
                foreach (BorderTile t in _tiles.Values)
                    if (t.OwnerId == accountId) n++;
                return n;
            }
        }

        public int TotalTiles
        {
            get { lock (_gate) return _tiles.Count; }
        }

        // ------------------------------------------------------------------ defence publication

        /// <summary>Publishes a defender's loadout, bot policy, mode and army for future challenges.</summary>
        public string PublishDefence(string accountId, DefencePublication publication)
        {
            if (publication == null) throw new ArgumentNullException(nameof(publication));
            lock (_gate)
            {
                if (!_members.Contains(accountId)) return "NOT_IN_SHARD";
                _defences[accountId] = publication;
                return null;
            }
        }

        public DefencePublication DefenceOf(string accountId)
        {
            lock (_gate) return _defences.TryGetValue(accountId, out DefencePublication d) ? d : DefencePublication.Default;
        }

        // ------------------------------------------------------------------ challenges

        /// <summary>
        /// Creates a unique challenge with tile and loss-allowance reservation and snapshots, or
        /// rejects with a code. A repeated request ID returns the original challenge (no second
        /// reservation), so a timed-out client reconciles by identifier before retrying.
        /// </summary>
        public ChallengeReceipt CreateChallenge(ChallengeRequest request, long nowMs)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            lock (_gate)
            {
                if (_byRequest.TryGetValue(request.RequestId, out Challenge existing))
                {
                    if (existing.AttackerId != request.AttackerId || existing.DefenderId != request.DefenderId)
                        return ChallengeReceipt.Reject("REQUEST_ID_CONFLICT");
                    return ChallengeReceipt.Ok(Copy(existing), replayed: true);
                }
                ExpireDueLocked(nowMs);

                if (State != ShardSeasonState.Open || !Season.Contains(nowMs)) return ChallengeReceipt.Reject("SEASON_NOT_OPEN");
                if (!_members.Contains(request.AttackerId) || !_members.Contains(request.DefenderId)) return ChallengeReceipt.Reject("NOT_IN_SHARD");
                if (request.AttackerId == request.DefenderId) return ChallengeReceipt.Reject("SELF_TARGET");
                AccountSnapshot attacker = _accounts.Get(request.AttackerId);
                AccountSnapshot defender = _accounts.Get(request.DefenderId);
                if (attacker.IsStarterProtected(nowMs)) return ChallengeReceipt.Reject("ATTACKER_PROTECTED");
                if (defender.IsStarterProtected(nowMs)) return ChallengeReceipt.Reject("DEFENDER_PROTECTED");
                if (!OpponentPolicy.IsEligible(attacker, defender)) return ChallengeReceipt.Reject("OPPONENT_OUT_OF_RANGE");
                string pairKey = request.AttackerId + ">" + request.DefenderId;
                if (_lastChallenge.TryGetValue(pairKey, out long last) && nowMs - last < WorldRules.RepeatTargetWindowMs)
                    return ChallengeReceipt.Reject("REPEAT_TARGET");
                string armyViolation = ArmyRules.Validate(request.AttackerArmy.Slots);
                if (armyViolation != null) return ChallengeReceipt.Reject(armyViolation);

                long day = WorldTime.UtcDay(nowMs);
                LossAllowance allowance = AllowanceLocked(request.DefenderId, day);
                if (allowance.DailyRemaining <= 0) return ChallengeReceipt.Reject("DEFENDER_DAILY_LIMIT");
                if (allowance.SeasonRemaining <= 0) return ChallengeReceipt.Reject("DEFENDER_SEASON_LIMIT");

                var alliances = new List<string>(attacker.AlliancesForCap(nowMs));
                foreach (string alliance in alliances)
                    if (AllianceUsedLocked(alliance, request.DefenderId, day) >= WorldRules.MaxAllianceSuccessesPerDefenderPerUtcDay)
                        return ChallengeReceipt.Reject("ALLIANCE_DAILY_LIMIT");

                BorderTile tile = PickTileLocked(request.DefenderId, request.TileId, out string tileProblem);
                if (tile == null) return ChallengeReceipt.Reject(tileProblem);

                _challengeCounter++;
                var c = new Challenge
                {
                    ChallengeId = Info.ShardId + "/" + Season.SeasonId + "/c" + _challengeCounter.ToString(CultureInfo.InvariantCulture),
                    RequestId = request.RequestId,
                    ShardId = Info.ShardId,
                    SeasonId = Season.SeasonId,
                    AttackerId = request.AttackerId,
                    DefenderId = request.DefenderId,
                    TileId = tile.TileId,
                    CreatedMs = nowMs,
                    ExpiresMs = nowMs + WorldRules.ChallengeExpiryMs,
                    ReservedUtcDay = day,
                    AttackerAlliances = alliances.ToArray(),
                    WorldRulesHashHex = WorldRules.HashHex,
                    DuelRulesHashHex = RulesBundle.HashHex,
                    DefenceSnapshot = DefenceOfLocked(request.DefenderId),
                    AttackerArmy = request.AttackerArmy,
                    State = ChallengeState.Reserved,
                };
                tile.ReservedBy = c.ChallengeId;
                _challenges[c.ChallengeId] = c;
                _byRequest[c.RequestId] = c;
                _lastChallenge[pairKey] = nowMs;
                return ChallengeReceipt.Ok(Copy(c));
            }
        }

        /// <summary>Reconciliation by identifier: the challenge created for a request ID, or null.</summary>
        public Challenge LookupByRequest(string requestId)
        {
            lock (_gate) return _byRequest.TryGetValue(requestId, out Challenge c) ? Copy(c) : null;
        }

        public Challenge Lookup(string challengeId)
        {
            lock (_gate) return _challenges.TryGetValue(challengeId, out Challenge c) ? Copy(c) : null;
        }

        public IReadOnlyList<Challenge> Challenges
        {
            get
            {
                lock (_gate)
                {
                    var list = new List<Challenge>();
                    foreach (Challenge c in _challenges.Values) list.Add(Copy(c));
                    list.Sort((a, b) => a.CreatedMs != b.CreatedMs ? a.CreatedMs.CompareTo(b.CreatedMs) : string.CompareOrdinal(a.ChallengeId, b.ChallengeId));
                    return list;
                }
            }
        }

        /// <summary>
        /// Commits the authoritative encounter result once. Resolving again with the same resolution ID
        /// is idempotent; a different resolution ID is a conflict; an expired or cancelled challenge
        /// cannot be resolved. A successful attack moves exactly the reserved tile and consumes the
        /// reserved allowance; every other outcome releases the reservation without transfer.
        /// </summary>
        public ChallengeReceipt Resolve(string challengeId, string resolutionId, EncounterOutcome outcome, long nowMs)
        {
            if (string.IsNullOrEmpty(resolutionId)) throw new ArgumentException("Resolution ID required.", nameof(resolutionId));
            lock (_gate)
            {
                if (!_challenges.TryGetValue(challengeId, out Challenge c)) return ChallengeReceipt.Reject("UNKNOWN_CHALLENGE");
                if (c.State == ChallengeState.Resolved)
                    return c.ResolutionId == resolutionId ? ChallengeReceipt.Ok(Copy(c), replayed: true) : ChallengeReceipt.Reject("ALREADY_RESOLVED", Copy(c));
                if (c.State == ChallengeState.Cancelled) return ChallengeReceipt.Reject("CANCELLED", Copy(c));
                if (nowMs >= c.ExpiresMs && State == ShardSeasonState.Open)
                {
                    CancelLocked(c, "EXPIRED");
                    return ChallengeReceipt.Reject("EXPIRED", Copy(c));
                }
                CommitLocked(c, resolutionId, outcome);
                return ChallengeReceipt.Ok(Copy(c));
            }
        }

        /// <summary>Cancels a reservation without transfer (e.g. a verified technical failure).</summary>
        public ChallengeReceipt Cancel(string challengeId, string reason)
        {
            lock (_gate)
            {
                if (!_challenges.TryGetValue(challengeId, out Challenge c)) return ChallengeReceipt.Reject("UNKNOWN_CHALLENGE");
                if (c.State == ChallengeState.Cancelled) return ChallengeReceipt.Ok(Copy(c), replayed: true);
                if (c.State == ChallengeState.Resolved) return ChallengeReceipt.Reject("ALREADY_RESOLVED", Copy(c));
                CancelLocked(c, reason ?? "CANCELLED");
                return ChallengeReceipt.Ok(Copy(c));
            }
        }

        /// <summary>Cancels every reservation whose ten-minute expiry has passed. Returns how many.</summary>
        public int ExpireDue(long nowMs)
        {
            lock (_gate) return ExpireDueLocked(nowMs);
        }

        public LossAllowance Allowance(string defenderId, long nowMs)
        {
            lock (_gate) return AllowanceLocked(defenderId, WorldTime.UtcDay(nowMs));
        }

        /// <summary>Committed border losses of an account on a UTC day (homeland is never counted).</summary>
        public int LossesOnDay(string accountId, long utcDay)
        {
            lock (_gate) return Get(_lossesOnDay, accountId + "@" + utcDay);
        }

        public int LossesInSeason(string accountId)
        {
            lock (_gate) return Get(_lossesInSeason, accountId);
        }

        // ------------------------------------------------------------------ season close

        /// <summary>
        /// Season close (plan: "stop new challenges, settle or cancel outstanding reservations,
        /// snapshot final ownership, grant rewards once and rebuild the next world's borders").
        /// <paramref name="settle"/> may supply an authoritative result for an open challenge (its
        /// resolution ID and outcome); anything it does not settle is cancelled without transfer.
        /// Calling Close again returns the same settlement and grants nothing new.
        /// </summary>
        public SeasonSettlement Close(long nowMs, Func<Challenge, KeyValuePair<string, EncounterOutcome>?> settle = null)
        {
            lock (_gate)
            {
                if (_settlement != null) return _settlement;
                State = ShardSeasonState.Closing;
                int settled = 0, cancelled = 0;
                var open = new List<Challenge>();
                foreach (Challenge c in _challenges.Values)
                    if (c.IsOpen) open.Add(c);
                open.Sort((a, b) => string.CompareOrdinal(a.ChallengeId, b.ChallengeId));
                foreach (Challenge c in open)
                {
                    KeyValuePair<string, EncounterOutcome>? result = settle?.Invoke(Copy(c));
                    if (result.HasValue)
                    {
                        CommitLocked(c, result.Value.Key, result.Value.Value);
                        settled++;
                    }
                    else
                    {
                        CancelLocked(c, "SEASON_CLOSE");
                        cancelled++;
                    }
                }

                var holdings = new SortedDictionary<string, int>(StringComparer.Ordinal);
                foreach (string m in _members) holdings[m] = 0;
                var ownership = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (BorderTile t in _tiles.Values)
                {
                    ownership[t.TileId] = t.OwnerId;
                    holdings[t.OwnerId] = Get(holdings, t.OwnerId) + 1;
                }
                var w = new CanonicalWriter();
                w.Ascii("AK-W4-0/snapshot").Ascii(Info.ShardId).Ascii(Season.SeasonId).U32((uint)ownership.Count);
                foreach (KeyValuePair<string, string> kv in ownership) w.Ascii(kv.Key).Ascii(kv.Value);

                int grants = 0;
                long day = WorldTime.UtcDay(nowMs);
                foreach (KeyValuePair<string, int> h in holdings)
                {
                    string prefix = "season/" + Season.SeasonId + "/" + h.Key;
                    if (Get(_seasonEncounters, h.Key) > 0 &&
                        _economy.Credit(prefix + "/participation", h.Key, EconomyRules.SeasonParticipationCoins, day, CoinSource.SeasonParticipation) > 0)
                        grants++;
                    string recognition = EconomyRules.RecognitionFor(h.Value);
                    if (recognition != null && _accounts.GrantCosmetic(prefix + "/recognition", h.Key, recognition + "/" + Season.SeasonId)) grants++;
                }

                _settlement = new SeasonSettlement
                {
                    ShardId = Info.ShardId,
                    SeasonId = Season.SeasonId,
                    ClosedMs = nowMs,
                    Settled = settled,
                    Cancelled = cancelled,
                    TileOwnership = ownership,
                    TilesHeld = holdings,
                    SnapshotHashHex = Hex.Encode(w.Sha256()),
                    GrantsIssued = grants,
                    Reconciled = true,
                };
                State = ShardSeasonState.Closed;
                return _settlement;
            }
        }

        /// <summary>
        /// Rebuilds the next season's borders after a close: every member gets the standard twelve
        /// tiles back, loss ledgers and challenges reset, and homeland, purchases, earned cosmetics and
        /// coins (all in the account and ledger, not the shard) are untouched. Accounts that ended the
        /// season below their allocation are offered the safe practice/re-entry route.
        /// </summary>
        public string RebuildForNextSeason(WorldSeason next, IReadOnlyList<string> members)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));
            lock (_gate)
            {
                if (State != ShardSeasonState.Closed || _settlement == null) return "NOT_CLOSED";
                if (next.Index != Season.Index + 1) return "SEASON_ORDER";
                foreach (KeyValuePair<string, int> h in _settlement.TilesHeld)
                    if (_accounts.Exists(h.Key)) _accounts.SetReEntryOffered(h.Key, h.Value < WorldRules.BorderTilesPerAccount);
                _members.Clear();
                _tiles.Clear();
                _challenges.Clear();
                _byRequest.Clear();
                _lossesOnDay.Clear();
                _lossesInSeason.Clear();
                _lastChallenge.Clear();
                _allianceSuccesses.Clear();
                _seasonEncounters.Clear();
                _challengeCounter = 0;
                _settlement = null;
                Season = next;
                State = ShardSeasonState.Open;
                foreach (string m in members)
                {
                    if (!_accounts.Exists(m) || !_members.Add(m)) continue;
                    AllocateBorder(m);
                }
                return null;
            }
        }

        // ------------------------------------------------------------------ internals

        private void CommitLocked(Challenge c, string resolutionId, EncounterOutcome outcome)
        {
            BorderTile tile = _tiles[c.TileId];
            tile.ReservedBy = null;
            c.State = ChallengeState.Resolved;
            c.Outcome = outcome;
            c.ResolutionId = resolutionId;
            long day = WorldTime.UtcDay(c.CreatedMs);
            if (outcome == EncounterOutcome.AttackerWins)
            {
                if (tile.OwnerId != c.DefenderId) throw new InvalidOperationException("Reserved tile changed owner: " + tile.TileId);
                tile.OwnerId = c.AttackerId;
                Increment(_lossesOnDay, c.DefenderId + "@" + c.ReservedUtcDay);
                Increment(_lossesInSeason, c.DefenderId);
                foreach (string alliance in c.AttackerAlliances)
                    Increment(_allianceSuccesses, alliance + "|" + c.DefenderId + "@" + c.ReservedUtcDay);
            }
            Increment(_seasonEncounters, c.AttackerId);
            Increment(_seasonEncounters, c.DefenderId);
            _accounts.RecordWorldEncounter(c.AttackerId);
            _accounts.RecordWorldEncounter(c.DefenderId);

            // Rewards once per challenge and role (earned cosmetic currency, daily-capped).
            int attackerCoins = outcome == EncounterOutcome.AttackerWins ? EconomyRules.CoinsPerEncounterWin : EconomyRules.CoinsPerEncounterParticipation;
            int defenderCoins = outcome == EncounterOutcome.DefenderWins ? EconomyRules.CoinsPerEncounterWin : EconomyRules.CoinsPerEncounterParticipation;
            _economy.Credit("encounter/" + c.ChallengeId + "/attacker", c.AttackerId, attackerCoins, day,
                outcome == EncounterOutcome.AttackerWins ? CoinSource.EncounterWin : CoinSource.EncounterParticipation);
            _economy.Credit("encounter/" + c.ChallengeId + "/defender", c.DefenderId, defenderCoins, day,
                outcome == EncounterOutcome.DefenderWins ? CoinSource.EncounterWin : CoinSource.EncounterParticipation);
        }

        private void CancelLocked(Challenge c, string reason)
        {
            if (_tiles.TryGetValue(c.TileId, out BorderTile tile) && tile.ReservedBy == c.ChallengeId) tile.ReservedBy = null;
            c.State = ChallengeState.Cancelled;
            c.CancelReason = reason;
        }

        private int ExpireDueLocked(long nowMs)
        {
            int n = 0;
            foreach (Challenge c in _challenges.Values)
            {
                if (c.IsOpen && nowMs >= c.ExpiresMs)
                {
                    CancelLocked(c, "EXPIRED");
                    n++;
                }
            }
            return n;
        }

        private LossAllowance AllowanceLocked(string defenderId, long day)
        {
            int reservedDay = 0, reservedSeason = 0;
            foreach (Challenge c in _challenges.Values)
            {
                if (!c.IsOpen || c.DefenderId != defenderId) continue;
                reservedSeason++;
                if (c.ReservedUtcDay == day) reservedDay++;
            }
            int daily = WorldRules.MaxBorderLossesPerUtcDay - Get(_lossesOnDay, defenderId + "@" + day) - reservedDay;
            int season = WorldRules.MaxBorderLossesPerSeason - Get(_lossesInSeason, defenderId) - reservedSeason;
            return new LossAllowance(daily, season);
        }

        private int AllianceUsedLocked(string alliance, string defenderId, long day)
        {
            int used = Get(_allianceSuccesses, alliance + "|" + defenderId + "@" + day);
            foreach (Challenge c in _challenges.Values)
            {
                if (!c.IsOpen || c.DefenderId != defenderId || c.ReservedUtcDay != day) continue;
                if (Array.IndexOf(c.AttackerAlliances, alliance) >= 0) used++;
            }
            return used;
        }

        private BorderTile PickTileLocked(string defenderId, string requested, out string problem)
        {
            problem = null;
            if (requested != null)
            {
                if (!_tiles.TryGetValue(requested, out BorderTile t) || t.OwnerId != defenderId)
                {
                    problem = "TILE_NOT_ELIGIBLE";
                    return null;
                }
                if (t.ReservedBy != null)
                {
                    problem = "TILE_RESERVED";
                    return null;
                }
                return t;
            }
            BorderTile best = null;
            foreach (BorderTile t in _tiles.Values)
            {
                if (t.OwnerId != defenderId || t.ReservedBy != null) continue;
                if (best == null || string.CompareOrdinal(t.TileId, best.TileId) < 0) best = t;
            }
            if (best == null) problem = "NO_ELIGIBLE_TILE";
            return best;
        }

        private DefencePublication DefenceOfLocked(string accountId) =>
            _defences.TryGetValue(accountId, out DefencePublication d) ? d : DefencePublication.Default;

        private static Challenge Copy(Challenge c) => (Challenge)c.MemberwiseCloneInternal();

        private static int Get<TKey>(IDictionary<TKey, int> d, TKey key) => d.TryGetValue(key, out int v) ? v : 0;

        private static void Increment(Dictionary<string, int> d, string key) => d[key] = Get(d, key) + 1;
    }

    /// <summary>The result of closing one shard's season.</summary>
    public sealed class SeasonSettlement
    {
        public string ShardId { get; internal set; }
        public string SeasonId { get; internal set; }
        public long ClosedMs { get; internal set; }
        public int Settled { get; internal set; }
        public int Cancelled { get; internal set; }
        /// <summary>Final tile → owner map (immutable snapshot).</summary>
        public IReadOnlyDictionary<string, string> TileOwnership { get; internal set; }
        public IReadOnlyDictionary<string, int> TilesHeld { get; internal set; }
        public string SnapshotHashHex { get; internal set; }
        public int GrantsIssued { get; internal set; }
        /// <summary>True when no reservation remains open.</summary>
        public bool Reconciled { get; internal set; }
    }
}
