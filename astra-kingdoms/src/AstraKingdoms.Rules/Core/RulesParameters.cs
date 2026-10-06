using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Balance;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Rules.Core
{
    /// <summary>
    /// The immutable set of balance-tunable values a match runs with (ticket 24). It covers exactly
    /// the closed list in <see cref="TunableSchema"/>: match and duel lengths, starting HP, victory
    /// threshold, land quota floor, Vajra gate, phase timers, damage side effects, card caps and each
    /// weapon's damage, mass and unlock level. Everything else (board, physics, trig tables, launch
    /// geometry, projectile counts, abilities, terrain templates) stays compiled and frozen.
    /// <para>
    /// A match is pinned to one instance for its whole life through <c>MatchConfig.Parameters</c>;
    /// the duel state carries the same reference so the resolver, validators and land rules read
    /// one consistent snapshot. Instances are created only from a validated
    /// <see cref="BalanceBundle"/>, so <see cref="RulesHash"/> is always the bundle's effective
    /// rules hash and a tuned match can never be resolved or replayed under another configuration.
    /// </para>
    /// <see cref="Default"/> is AK-TR-1 itself: its values equal <see cref="RulesConstants"/>, the
    /// compiled weapon catalog and card caps, and its hash equals <c>RulesBundle.Hash</c>, so
    /// default matches are byte-for-byte identical to the compiled engine.
    /// </summary>
    public sealed class RulesParameters
    {
        private static readonly Lazy<RulesParameters> DefaultLazy =
            new Lazy<RulesParameters>(() => FromBundle(BalanceBundle.Baseline()));

        private readonly WeaponDefinition[] _weapons;
        private readonly int[] _cardCaps; // indexed by card wire ID (1..6)
        private readonly Lazy<byte[]> _rulesHash;

        /// <summary>The AK-TR-1 baseline (no overrides). Shared, immutable.</summary>
        public static RulesParameters Default => DefaultLazy.Value;

        /// <summary>
        /// Rules version the match records: the balance bundle ID (<c>AK-TR-1</c> for the
        /// baseline, for example <c>AK-TR-1.b2</c> for a tuned release).
        /// </summary>
        public string RulesVersion { get; }
        /// <summary>The frozen engine family these values tune (always <c>AK-TR-1</c> in this build).</summary>
        public string BaseRulesVersion { get; }
        /// <summary>SHA-256 of the balance bundle file content that produced these values.</summary>
        public string BalanceContentHashHex { get; }
        /// <summary>True for the AK-TR-1 baseline (no overrides).</summary>
        public bool IsDefault { get; }

        // ---- Match and duel ----
        public int MaxRounds { get; }
        public int VictoryCells { get; }
        public int MaxVolleys { get; }
        public int StartHpUnits { get; }

        // ---- Land ----
        public long QuotaFloorPpm { get; }
        public int VajraMinExclusiveDiffUnits { get; }

        // ---- Timing (milliseconds) ----
        public int TerrainAnnouncementMs { get; }
        public int ChoiceDeadlineMs { get; }
        public int SharedPhoneHandoverMs { get; }
        public int ResolutionReplayMaxMs { get; }
        public int OnlineCutWindowMs { get; }
        public int SharedPhoneCutWindowMs { get; }

        // ---- Damage side effects (HP units) ----
        public int ChainBonusUnits { get; }
        public int BurnUnits { get; }
        public int OceanHealUnits { get; }
        public int RiverHealUnits { get; }

        private RulesParameters(BalanceBundle bundle)
        {
            RulesVersion = bundle.BundleId;
            BaseRulesVersion = bundle.BaseRulesVersion;
            BalanceContentHashHex = bundle.ContentHashHex;
            IsDefault = bundle.IsBaseline;

            int I(string name) => checked((int)bundle.ValueOf(name));
            MaxRounds = I("Match.MaxRounds");
            VictoryCells = I("Match.VictoryCells");
            MaxVolleys = I("Duel.MaxVolleys");
            StartHpUnits = I("Duel.StartHpUnits");
            QuotaFloorPpm = bundle.ValueOf("Land.QuotaFloorPpm");
            VajraMinExclusiveDiffUnits = I("Land.VajraMinExclusiveDiffUnits");
            TerrainAnnouncementMs = I("Timing.TerrainAnnouncementMs");
            ChoiceDeadlineMs = I("Timing.ChoiceDeadlineMs");
            SharedPhoneHandoverMs = I("Timing.SharedPhoneHandoverMs");
            ResolutionReplayMaxMs = I("Timing.ResolutionReplayMaxMs");
            OnlineCutWindowMs = I("Timing.OnlineCutWindowMs");
            SharedPhoneCutWindowMs = I("Timing.SharedPhoneCutWindowMs");
            ChainBonusUnits = I("Damage.ChainBonusUnits");
            BurnUnits = I("Damage.BurnUnits");
            OceanHealUnits = I("Damage.OceanHealUnits");
            RiverHealUnits = I("Damage.RiverHealUnits");

            _cardCaps = new int[Cards.All.Count + 1];
            foreach (CardId card in Cards.All) _cardCaps[(int)card] = I(TunableSchema.CardCapName(card));

            IReadOnlyList<WeaponDefinition> catalog = WeaponCatalog.All;
            _weapons = new WeaponDefinition[catalog.Count];
            for (int i = 0; i < catalog.Count; i++)
            {
                WeaponDefinition w = catalog[i];
                _weapons[i] = w.WithTunables(I(TunableSchema.WeaponDamageName(w.Id)), I(TunableSchema.WeaponMassName(w.Id)),
                    I(TunableSchema.WeaponUnlockName(w.Id)));
            }

            BalanceBundle captured = bundle;
            _rulesHash = new Lazy<byte[]>(() => captured.EffectiveRulesHash);
        }

        /// <summary>
        /// Builds the parameters of a balance bundle. Throws <see cref="ArgumentException"/> when the
        /// bundle fails <see cref="BalanceValidator"/> (an invalid release can never run).
        /// Prefer <see cref="BalanceBundle.ToParameters"/>, which caches the result.
        /// </summary>
        public static RulesParameters FromBundle(BalanceBundle bundle)
        {
            if (bundle == null) throw new ArgumentNullException(nameof(bundle));
            IReadOnlyList<BalanceIssue> issues = BalanceValidator.Validate(bundle);
            if (issues.Count > 0)
                throw new ArgumentException("Balance bundle " + bundle.BundleId + " is invalid: " + string.Join("; ", issues), nameof(bundle));
            return new RulesParameters(bundle);
        }

        /// <summary>The effective 32-byte rules hash (a fresh copy on every read).</summary>
        public byte[] RulesHash => (byte[])_rulesHash.Value.Clone();

        public string RulesHashHex => Hex.Encode(_rulesHash.Value);

        /// <summary>The tuned record of regular weapon <paramref name="id"/> (1-20).</summary>
        public WeaponDefinition Weapon(int id)
        {
            if (!WeaponCatalog.IsRegularId(id)) throw new ArgumentOutOfRangeException(nameof(id), "Unknown regular weapon id " + id);
            return _weapons[id - 1];
        }

        /// <summary>All twenty tuned weapons, ascending by ID.</summary>
        public IReadOnlyList<WeaponDefinition> Weapons => _weapons;

        /// <summary>Card cap percent in force for <paramref name="card"/>.</summary>
        public int CardCapPercent(CardId card)
        {
            int id = (int)card;
            if (id < 1 || id >= _cardCaps.Length) throw new ArgumentOutOfRangeException(nameof(card));
            return _cardCaps[id];
        }

        /// <summary>Clamps a health total to 0..<see cref="StartHpUnits"/>.</summary>
        public int ClampHp(long units) => Hp.Clamp(units, StartHpUnits);

        public override string ToString() => RulesVersion + " (" + RulesHashHex.Substring(0, 12) + ")";
    }
}
