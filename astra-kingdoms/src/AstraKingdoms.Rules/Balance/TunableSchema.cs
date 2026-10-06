using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Rules.Balance
{
    /// <summary>Where a tunable lives inside <see cref="RulesBundleContents"/>.</summary>
    public enum TunableTarget : byte
    {
        /// <summary>A named entry of <see cref="RulesBundleContents.Constants"/>.</summary>
        Constant = 0,
        /// <summary><see cref="WeaponRow.DamageUnits"/> of one weapon.</summary>
        WeaponDamage = 1,
        /// <summary><see cref="WeaponRow.Mass"/> of one weapon.</summary>
        WeaponMass = 2,
        /// <summary><see cref="WeaponRow.UnlockLevel"/> (presentation and practice only).</summary>
        WeaponUnlockLevel = 3,
        /// <summary>A card cap percent in <see cref="RulesBundleContents.CardCaps"/>.</summary>
        CardCap = 4,
    }

    /// <summary>
    /// One value that a balance bundle may override, with its AK-TR-1 baseline and an inclusive legal
    /// range. Ranges are sanity bounds that stop obviously broken releases (a zero-HP duel, a card
    /// larger than half the board); they are not balance targets.
    /// </summary>
    public sealed class TunableDefinition
    {
        public string Name { get; }
        public TunableTarget Target { get; }
        /// <summary>Constant name, weapon ID or card wire ID, depending on <see cref="Target"/>.</summary>
        public string ConstantName { get; }
        public int EntityId { get; }
        public long Baseline { get; }
        public long Min { get; }
        public long Max { get; }
        public string Unit { get; }

        internal TunableDefinition(string name, TunableTarget target, string constantName, int entityId, long baseline, long min, long max, string unit)
        {
            Name = name;
            Target = target;
            ConstantName = constantName;
            EntityId = entityId;
            Baseline = baseline;
            Min = min;
            Max = max;
            Unit = unit;
        }

        public bool InRange(long value) => value >= Min && value <= Max;

        public override string ToString() => Name + " [" + Min + ".." + Max + " " + Unit + "] baseline " + Baseline;
    }

    /// <summary>
    /// The closed list of balance-tunable values (ticket 24). Everything else in the rules bundle is
    /// structural and frozen for the AK-TR-1 family: board size and active cells, physics tick rate,
    /// fixed-point format, trig tables, launch geometry, projectile counts, abilities, terrain
    /// templates and stream labels. Changing those needs a new rules version (a new engine), not a
    /// balance bundle. Names are stable identifiers used in bundle files; never rename one.
    /// </summary>
    public static class TunableSchema
    {
        private static readonly Lazy<IReadOnlyList<TunableDefinition>> AllLazy = new Lazy<IReadOnlyList<TunableDefinition>>(Build);
        private static readonly Lazy<Dictionary<string, TunableDefinition>> ByNameLazy = new Lazy<Dictionary<string, TunableDefinition>>(() =>
        {
            var d = new Dictionary<string, TunableDefinition>(StringComparer.Ordinal);
            foreach (TunableDefinition t in AllLazy.Value) d.Add(t.Name, t);
            return d;
        });

        /// <summary>Every tunable in a stable order (constants, then cards, then weapons by ID).</summary>
        public static IReadOnlyList<TunableDefinition> All => AllLazy.Value;

        public static bool TryGet(string name, out TunableDefinition definition)
        {
            if (name == null)
            {
                definition = null;
                return false;
            }
            return ByNameLazy.Value.TryGetValue(name, out definition);
        }

        public static string WeaponDamageName(int weaponId) => "Weapon." + weaponId + ".DamageUnits";
        public static string WeaponMassName(int weaponId) => "Weapon." + weaponId + ".Mass";
        public static string WeaponUnlockName(int weaponId) => "Weapon." + weaponId + ".UnlockLevel";
        public static string CardCapName(CardId card) => "Card." + card + ".CapPercent";

        private static IReadOnlyList<TunableDefinition> Build()
        {
            var list = new List<TunableDefinition>();
            void C(string name, string constant, long baseline, long min, long max, string unit) =>
                list.Add(new TunableDefinition(name, TunableTarget.Constant, constant, 0, baseline, min, max, unit));

            // Match and duel.
            C("Match.MaxRounds", "MaxRounds", RulesConstants.MaxRounds, 4, 12, "rounds");
            C("Match.VictoryCells", "VictoryCells", RulesConstants.VictoryCells, RulesConstants.InitialCellsPerPlayer + 1, RulesConstants.ActiveCells, "cells");
            C("Duel.MaxVolleys", "MaxVolleys", RulesConstants.MaxVolleys, 1, 5, "volleys");
            C("Duel.StartHpUnits", "StartHpUnits", RulesConstants.StartHpUnits, 50 * RulesConstants.HpUnitsPerHp, 200 * RulesConstants.HpUnitsPerHp, "HP/100");
            // Land.
            C("Land.QuotaFloorPpm", "QuotaFloorPpm", RulesConstants.QuotaFloorPpm, 0, 200000, "ppm");
            C("Land.VajraMinExclusiveDiffUnits", "VajraMinExclusiveDiffUnits", RulesConstants.VajraMinExclusiveDiffUnits, 0, 100 * RulesConstants.HpUnitsPerHp, "HP/100");
            // Timing.
            C("Timing.TerrainAnnouncementMs", "TerrainAnnouncementMs", RulesConstants.TerrainAnnouncementMs, 1000, 5000, "ms");
            C("Timing.ChoiceDeadlineMs", "ChoiceDeadlineMs", RulesConstants.ChoiceDeadlineMs, 5000, 30000, "ms");
            C("Timing.SharedPhoneHandoverMs", "SharedPhoneHandoverMs", RulesConstants.SharedPhoneHandoverMs, 2000, 10000, "ms");
            C("Timing.ResolutionReplayMaxMs", "ResolutionReplayMaxMs", RulesConstants.ResolutionReplayMaxMs, 1500, 5000, "ms");
            C("Timing.OnlineCutWindowMs", "OnlineCutWindowMs", RulesConstants.OnlineCutWindowMs, 8000, 30000, "ms");
            C("Timing.SharedPhoneCutWindowMs", "SharedPhoneCutWindowMs", RulesConstants.SharedPhoneCutWindowMs, 8000, 40000, "ms");
            // Damage side effects.
            C("Damage.ChainBonusUnits", "ChainBonusUnits", DamageCalculator.ChainBonusUnits, 0, 50 * RulesConstants.HpUnitsPerHp, "HP/100");
            C("Damage.BurnUnits", "BurnUnits", DamageCalculator.BurnUnits, 0, 50 * RulesConstants.HpUnitsPerHp, "HP/100");
            C("Damage.OceanHealUnits", "OceanHealUnits", DamageCalculator.OceanHealUnits, 0, 50 * RulesConstants.HpUnitsPerHp, "HP/100");
            C("Damage.RiverHealUnits", "RiverHealUnits", DamageCalculator.RiverHealUnits, 0, 50 * RulesConstants.HpUnitsPerHp, "HP/100");

            foreach (CardId card in Cards.All)
                list.Add(new TunableDefinition(CardCapName(card), TunableTarget.CardCap, null, (int)card, Cards.CapPercent(card), 1, 40, "%"));

            foreach (WeaponDefinition w in WeaponCatalog.All)
            {
                list.Add(new TunableDefinition(WeaponDamageName(w.Id), TunableTarget.WeaponDamage, null, w.Id, w.DamagePerProjectileUnits,
                    0, 100 * RulesConstants.HpUnitsPerHp, "HP/100 per projectile"));
                list.Add(new TunableDefinition(WeaponMassName(w.Id), TunableTarget.WeaponMass, null, w.Id, w.MassPerProjectile, 1, 10, "mass units"));
                list.Add(new TunableDefinition(WeaponUnlockName(w.Id), TunableTarget.WeaponUnlockLevel, null, w.Id, w.UnlockLevel, 1, 20, "level"));
            }
            return list;
        }
    }
}
