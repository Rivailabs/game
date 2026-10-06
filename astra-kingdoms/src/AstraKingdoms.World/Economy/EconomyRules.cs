using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.World.Economy
{
    /// <summary>Where earned coins came from. World sources share one daily cap.</summary>
    public enum CoinSource : byte
    {
        EncounterWin = 1,
        EncounterParticipation = 2,
        SeasonParticipation = 3,
        AllianceObjective = 4,
        /// <summary>Recorded incident compensation (never arbitrary currency creation).</summary>
        IncidentCompensation = 5,
    }

    /// <summary>
    /// PROPOSED economy table for world discovery. Coins are the existing earned cosmetic currency:
    /// they buy decorations and appearance only, never combat power, protection, extra attacks or
    /// deployment points (there is no sink of that kind to buy). World-earned coins share a daily cap
    /// so neither spending nor unlimited grinding compounds. Border ownership grants bounded cosmetic
    /// recognition at season close, not production.
    /// </summary>
    public static class EconomyRules
    {
        public const int CoinsPerEncounterWin = 20;
        public const int CoinsPerEncounterParticipation = 5;
        /// <summary>World-earned coins per account per UTC day (all world sources together).</summary>
        public const int DailyWorldCoinCap = 60;
        public const int SeasonParticipationCoins = 100;

        /// <summary>Season-close recognition tiers by border tiles held: (minimum tiles, cosmetic ID).</summary>
        public static readonly IReadOnlyList<KeyValuePair<int, string>> RecognitionTiers = new[]
        {
            new KeyValuePair<int, string>(18, "border-marshal-banner"),
            new KeyValuePair<int, string>(15, "border-warden-banner"),
            new KeyValuePair<int, string>(12, "border-keeper-banner"),
        };

        /// <summary>Cosmetic sinks (decorations); prices in coins.</summary>
        public static readonly IReadOnlyList<KeyValuePair<string, int>> Sinks = new[]
        {
            new KeyValuePair<string, int>("decoration-small", 80),
            new KeyValuePair<string, int>("decoration-garden", 150),
            new KeyValuePair<string, int>("banner-redye", 30),
        };

        public static bool IsWorldSource(CoinSource s) => s != CoinSource.IncidentCompensation;

        /// <summary>The single best recognition cosmetic for a tile count, or null.</summary>
        public static string RecognitionFor(int tilesHeld)
        {
            foreach (KeyValuePair<int, string> t in RecognitionTiers)
                if (tilesHeld >= t.Key) return t.Value;
            return null;
        }

        public static int SinkPrice(string sinkId)
        {
            foreach (KeyValuePair<string, int> s in Sinks)
                if (s.Key == sinkId) return s.Value;
            return -1;
        }

        internal static void WriteTo(CanonicalWriter w)
        {
            w.Ascii("economy").I32(CoinsPerEncounterWin).I32(CoinsPerEncounterParticipation).I32(DailyWorldCoinCap).I32(SeasonParticipationCoins);
            foreach (KeyValuePair<int, string> t in RecognitionTiers) w.I32(t.Key).Ascii(t.Value);
            foreach (KeyValuePair<string, int> s in Sinks) w.Ascii(s.Key).I32(s.Value);
        }
    }

    /// <summary>
    /// Thread-safe, idempotent coin ledger. Every credit and spend carries a unique key; repeating a
    /// key (retry, duplicated settlement, restored backup) changes nothing. World credits are clipped
    /// to the daily cap; spends never overdraw; there is no transfer between accounts.
    /// </summary>
    public sealed class EconomyLedger
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, int> _applied = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _balance = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _earnedOnDay = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _owned = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        public long TotalMinted { get; private set; }
        public long TotalSpent { get; private set; }

        /// <summary>Credits up to <paramref name="amount"/> coins once per key; returns the amount actually credited.</summary>
        public int Credit(string key, string account, int amount, long utcDay, CoinSource source)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key required.", nameof(key));
            if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
            lock (_gate)
            {
                if (_applied.ContainsKey(key)) return 0;
                int credited = amount;
                if (EconomyRules.IsWorldSource(source))
                {
                    string dayKey = account + "@" + utcDay;
                    _earnedOnDay.TryGetValue(dayKey, out int earned);
                    credited = Math.Max(0, Math.Min(amount, EconomyRules.DailyWorldCoinCap - earned));
                    _earnedOnDay[dayKey] = earned + credited;
                }
                _applied[key] = credited;
                _balance.TryGetValue(account, out long b);
                _balance[account] = b + credited;
                TotalMinted += credited;
                return credited;
            }
        }

        /// <summary>Spends coins on a cosmetic sink once per key. False when unaffordable or unknown.</summary>
        public bool Spend(string key, string account, string sinkId)
        {
            int price = EconomyRules.SinkPrice(sinkId);
            if (price < 0) return false;
            lock (_gate)
            {
                if (_applied.ContainsKey(key)) return true;
                _balance.TryGetValue(account, out long b);
                if (b < price) return false;
                _applied[key] = -price;
                _balance[account] = b - price;
                TotalSpent += price;
                if (!_owned.TryGetValue(account, out HashSet<string> set)) _owned[account] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(sinkId + "#" + key);
                return true;
            }
        }

        public long Balance(string account)
        {
            lock (_gate) return _balance.TryGetValue(account, out long b) ? b : 0;
        }

        public int EarnedOnDay(string account, long utcDay)
        {
            lock (_gate) return _earnedOnDay.TryGetValue(account + "@" + utcDay, out int e) ? e : 0;
        }

        public int CosmeticsBought(string account)
        {
            lock (_gate) return _owned.TryGetValue(account, out HashSet<string> s) ? s.Count : 0;
        }
    }
}
