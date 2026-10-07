using System;
using System.Collections.Generic;

namespace AstraKingdoms.Rules.Core
{
    /// <summary>Immutable combat record for one regular weapon (IDs 1-20).</summary>
    public sealed class WeaponDefinition
    {
        public int Id { get; }
        public string Name { get; }
        public Element Element { get; }
        /// <summary>Base damage per projectile in HP units (30 HP = 3000).</summary>
        public int DamagePerProjectileUnits { get; private set; }
        public int ProjectileCount { get; }
        public SpeedProfile Speed { get; }
        public TrajectoryProfile Trajectory { get; }
        public int MassPerProjectile { get; private set; }
        /// <summary>Projectile radius in millimetres (0.04 m = 40).</summary>
        public int RadiusMm { get; }
        public WeaponAbility Ability { get; }
        /// <summary>Account level at which the weapon is permanently unlocked (presentation/practice only).</summary>
        public int UnlockLevel { get; private set; }

        public WeaponDefinition(int id, string name, Element element, int damagePerProjectileHp, int projectileCount,
            SpeedProfile speed, TrajectoryProfile trajectory, int massPerProjectile, int radiusMm,
            WeaponAbility ability, int unlockLevel)
        {
            Id = id;
            Name = name;
            Element = element;
            DamagePerProjectileUnits = damagePerProjectileHp * RulesConstants.HpUnitsPerHp;
            ProjectileCount = projectileCount;
            Speed = speed;
            Trajectory = trajectory;
            MassPerProjectile = massPerProjectile;
            RadiusMm = radiusMm;
            Ability = ability;
            UnlockLevel = unlockLevel;
        }

        /// <summary>
        /// A copy of this weapon with the balance-tunable fields replaced (ticket 24). Everything
        /// else (element, projectile count, launch profile, radius, ability) is structural and kept.
        /// Returns this instance when nothing changes, so the default parameters share the catalog's
        /// objects.
        /// </summary>
        internal WeaponDefinition WithTunables(int damagePerProjectileUnits, int massPerProjectile, int unlockLevel)
        {
            if (damagePerProjectileUnits == DamagePerProjectileUnits && massPerProjectile == MassPerProjectile && unlockLevel == UnlockLevel)
                return this;
            var copy = (WeaponDefinition)MemberwiseClone();
            copy.DamagePerProjectileUnits = damagePerProjectileUnits;
            copy.MassPerProjectile = massPerProjectile;
            copy.UnlockLevel = unlockLevel;
            return copy;
        }

        /// <summary>
        /// Intrinsic abilities are properties of the weapon and are not removed by Shock.
        /// </summary>
        public bool AbilityIsIntrinsic =>
            Ability == WeaponAbility.FanSpread ||
            Ability == WeaponAbility.TwinCurve ||
            Ability == WeaponAbility.GroundBurst ||
            Ability == WeaponAbility.StraightLance;

        public bool IsStarter => Id >= 1 && Id <= RulesConstants.StarterWeaponCount;

        public override string ToString() => Id + ":" + Name;
    }

    /// <summary>The 20-weapon AK-TR-1 registry (ticket 11).</summary>
    public static class WeaponCatalog
    {
        private static readonly WeaponDefinition[] Weapons =
        {
            new WeaponDefinition(1, "Ember Arrow", Element.Agni, 30, 1, SpeedProfile.Normal, TrajectoryProfile.Normal, 2, 40, WeaponAbility.Burn, 1),
            new WeaponDefinition(2, "Gale Arrow", Element.Vayu, 25, 1, SpeedProfile.Fast, TrajectoryProfile.Flat, 1, 40, WeaponAbility.Push, 1),
            new WeaponDefinition(3, "Stone Arrow", Element.Prithvi, 35, 1, SpeedProfile.Slow, TrajectoryProfile.High, 3, 60, WeaponAbility.JumpPierce, 1),
            new WeaponDefinition(4, "Spark Arrow", Element.Vidyut, 28, 1, SpeedProfile.Fast, TrajectoryProfile.Normal, 1, 40, WeaponAbility.Shock, 1),
            new WeaponDefinition(5, "Tide Arrow", Element.Varuna, 30, 1, SpeedProfile.Normal, TrajectoryProfile.Normal, 2, 40, WeaponAbility.Cleanse, 1),
            new WeaponDefinition(6, "Fire Fan", Element.Agni, 12, 3, SpeedProfile.Normal, TrajectoryProfile.Normal, 1, 30, WeaponAbility.FanSpread, 2),
            new WeaponDefinition(7, "Twin Gust", Element.Vayu, 15, 2, SpeedProfile.Fast, TrajectoryProfile.Flat, 1, 30, WeaponAbility.TwinCurve, 3),
            new WeaponDefinition(8, "Boulder Shot", Element.Prithvi, 45, 1, SpeedProfile.VerySlow, TrajectoryProfile.VeryHigh, 4, 120, WeaponAbility.GroundBurst, 4),
            new WeaponDefinition(9, "Chain Bolt", Element.Vidyut, 26, 1, SpeedProfile.Fast, TrajectoryProfile.Normal, 1, 40, WeaponAbility.ChainBonus, 5),
            new WeaponDefinition(10, "Mist Veil", Element.Varuna, 20, 1, SpeedProfile.Normal, TrajectoryProfile.High, 2, 40, WeaponAbility.Veil, 6),
            new WeaponDefinition(11, "Ash Shield", Element.Agni, 15, 1, SpeedProfile.Normal, TrajectoryProfile.Normal, 2, 40, WeaponAbility.AshShield, 7),
            new WeaponDefinition(12, "Cyclone", Element.Vayu, 22, 1, SpeedProfile.Normal, TrajectoryProfile.High, 2, 80, WeaponAbility.ReverseDodge, 8),
            new WeaponDefinition(13, "Iron Wall", Element.Prithvi, 10, 1, SpeedProfile.Slow, TrajectoryProfile.Flat, 3, 60, WeaponAbility.IronWall, 9),
            new WeaponDefinition(14, "Storm Net", Element.Vidyut, 24, 1, SpeedProfile.Normal, TrajectoryProfile.Normal, 1, 120, WeaponAbility.Net, 10),
            new WeaponDefinition(15, "Flood Arrow", Element.Varuna, 32, 1, SpeedProfile.Slow, TrajectoryProfile.High, 2, 60, WeaponAbility.RemoveCover, 11),
            new WeaponDefinition(16, "Sun Lance", Element.Agni, 40, 1, SpeedProfile.Direct, TrajectoryProfile.Direct, 1, 30, WeaponAbility.StraightLance, 12),
            new WeaponDefinition(17, "Sky Dive", Element.Vayu, 35, 1, SpeedProfile.Normal, TrajectoryProfile.VeryHigh, 2, 50, WeaponAbility.IgnoreCover, 13),
            new WeaponDefinition(18, "Quake Arrow", Element.Prithvi, 30, 1, SpeedProfile.Slow, TrajectoryProfile.Normal, 3, 50, WeaponAbility.Quake, 14),
            new WeaponDefinition(19, "Thunder Crown", Element.Vidyut, 38, 1, SpeedProfile.Normal, TrajectoryProfile.Normal, 2, 50, WeaponAbility.ThunderAdvantage, 15),
            new WeaponDefinition(20, "Ocean Call", Element.Varuna, 36, 1, SpeedProfile.Slow, TrajectoryProfile.High, 2, 60, WeaponAbility.OceanHeal, 16),
        };

        public static IReadOnlyList<WeaponDefinition> All => Weapons;

        public static bool IsRegularId(int id) => id >= 1 && id <= RulesConstants.RegularWeaponCount;

        public static WeaponDefinition Get(int id)
        {
            if (!IsRegularId(id)) throw new ArgumentOutOfRangeException(nameof(id), "Unknown regular weapon id " + id);
            return Weapons[id - 1];
        }

        /// <summary>Weapons available in a symmetric room catalog, ascending by ID.</summary>
        public static IReadOnlyList<WeaponDefinition> ForPreset(CatalogPreset preset)
        {
            if (preset == CatalogPreset.Full) return Weapons;
            var list = new List<WeaponDefinition>(RulesConstants.StarterWeaponCount);
            for (int i = 0; i < RulesConstants.StarterWeaponCount; i++) list.Add(Weapons[i]);
            return list;
        }

        /// <summary>Weapons permanently unlocked at a given account level (presentation/practice only).</summary>
        public static IReadOnlyList<WeaponDefinition> UnlockedAtLevel(int level)
        {
            var list = new List<WeaponDefinition>();
            foreach (var w in Weapons)
                if (w.UnlockLevel <= level) list.Add(w);
            return list;
        }
    }
}
