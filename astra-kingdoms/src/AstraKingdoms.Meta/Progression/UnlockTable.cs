using System;
using System.Collections.Generic;
using System.Linq;
using AstraKingdoms.Meta.Cosmetics;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Meta.Progression
{
    public enum UnlockKind : byte
    {
        /// <summary>Permanent access in practice and progression presentation (never a competitive advantage).</summary>
        Weapon = 0,
        Cosmetic = 1,
    }

    /// <summary>What reaching one level unlocks.</summary>
    public sealed class LevelUnlock
    {
        public int Level { get; }
        public UnlockKind Kind { get; }
        /// <summary>Weapon id for <see cref="UnlockKind.Weapon"/>, else 0.</summary>
        public int WeaponId { get; }
        /// <summary>Cosmetic id for <see cref="UnlockKind.Cosmetic"/>, else null.</summary>
        public string CosmeticId { get; }

        public LevelUnlock(int level, UnlockKind kind, int weaponId, string cosmeticId)
        {
            Level = level;
            Kind = kind;
            WeaponId = weaponId;
            CosmeticId = cosmeticId;
        }

        public override string ToString() => "L" + Level + " " + (Kind == UnlockKind.Weapon ? "weapon " + WeaponId : "cosmetic " + CosmeticId);
    }

    /// <summary>
    /// Level rewards (plan: five weapons at level 1, one more at each level 2-16 taken from
    /// <see cref="WeaponDefinition.UnlockLevel"/>, cosmetics and mastery recognition at 17-20).
    /// </summary>
    public static class UnlockTable
    {
        private static readonly LevelUnlock[] Table = Build();

        public static IReadOnlyList<LevelUnlock> All => Table;

        public static IReadOnlyList<LevelUnlock> AtLevel(int level) => Table.Where(u => u.Level == level).ToArray();

        /// <summary>Unlocks gained when moving from <paramref name="fromLevel"/> to <paramref name="toLevel"/> (exclusive, inclusive).</summary>
        public static IReadOnlyList<LevelUnlock> Between(int fromLevel, int toLevel) =>
            Table.Where(u => u.Level > fromLevel && u.Level <= toLevel).ToArray();

        /// <summary>Cosmetic level rewards owned at <paramref name="level"/>.</summary>
        public static IEnumerable<string> CosmeticsUpTo(int level) =>
            Table.Where(u => u.Kind == UnlockKind.Cosmetic && u.Level <= level).Select(u => u.CosmeticId);

        private static LevelUnlock[] Build()
        {
            var list = new List<LevelUnlock>();
            foreach (WeaponDefinition w in WeaponCatalog.All)
                if (w.UnlockLevel >= ProgressionRules.FirstWeaponUnlockLevel)
                    list.Add(new LevelUnlock(w.UnlockLevel, UnlockKind.Weapon, w.Id, null));
            foreach (CosmeticItem c in CosmeticCatalog.Default.Items)
                if (c.Source == CosmeticSource.LevelReward)
                    list.Add(new LevelUnlock(c.LevelRequired, UnlockKind.Cosmetic, 0, c.Id));
            return list.OrderBy(u => u.Level).ThenBy(u => u.WeaponId).ToArray();
        }

        /// <summary>
        /// Structural checks run by tests and at server start: each level 2-16 unlocks exactly one
        /// weapon, levels 17-20 unlock no weapon and at least one cosmetic, every weapon is reachable.
        /// </summary>
        public static IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();
            for (int level = ProgressionRules.FirstWeaponUnlockLevel; level <= ProgressionRules.LastWeaponUnlockLevel; level++)
            {
                int weapons = Table.Count(u => u.Level == level && u.Kind == UnlockKind.Weapon);
                if (weapons != 1) errors.Add("level " + level + " unlocks " + weapons + " weapons (expected 1)");
            }
            for (int level = ProgressionRules.LastWeaponUnlockLevel + 1; level <= ProgressionRules.MaxLevel; level++)
            {
                if (Table.Any(u => u.Level == level && u.Kind == UnlockKind.Weapon)) errors.Add("level " + level + " unlocks a weapon");
                if (!Table.Any(u => u.Level == level && u.Kind == UnlockKind.Cosmetic)) errors.Add("level " + level + " has no cosmetic/mastery reward");
            }
            if (Table.Any(u => u.Level < 2 || u.Level > ProgressionRules.MaxLevel)) errors.Add("unlock outside levels 2-20");
            int startWeapons = WeaponCatalog.All.Count(w => w.UnlockLevel <= 1);
            if (startWeapons != RulesConstants.StarterWeaponCount) errors.Add("level 1 has " + startWeapons + " weapons (expected 5)");
            return errors;
        }
    }

    /// <summary>Where a loadout is being chosen.</summary>
    public enum LoadoutContext : byte
    {
        /// <summary>Any match against another person, or an online/queued match: the room catalogue decides.</summary>
        CompetitiveRoom = 0,
        /// <summary>Local practice using the account's own permanently unlocked weapons.</summary>
        PracticeOwned = 1,
    }

    /// <summary>
    /// Which weapons a player may equip. Competitive catalogues are symmetric by construction: the
    /// room method takes no account data at all, so progression cannot restrict or extend them.
    /// </summary>
    public static class WeaponAccess
    {
        /// <summary>The symmetric room catalogue (Starter: five; Full: all twenty on loan to both players).</summary>
        public static IReadOnlyList<WeaponDefinition> RoomCatalog(CatalogPreset preset) => WeaponCatalog.ForPreset(preset);

        /// <summary>Permanently unlocked weapons at a level (practice and progression presentation).</summary>
        public static IReadOnlyList<WeaponDefinition> Owned(int level) =>
            WeaponCatalog.UnlockedAtLevel(Math.Max(1, Math.Min(ProgressionRules.MaxLevel, level)));

        /// <summary>
        /// Practice may also run on the Full preset: weapons not yet owned are loaned for that match,
        /// exactly as in a Full room. This returns the equip list for practice.
        /// </summary>
        public static IReadOnlyList<WeaponDefinition> Practice(int level, CatalogPreset? loanPreset = null) =>
            loanPreset.HasValue ? RoomCatalog(loanPreset.Value) : Owned(level);

        /// <summary>True when the weapon is available in this match only because of a catalogue loan.</summary>
        public static bool IsLoaned(int weaponId, int level, CatalogPreset preset) =>
            RoomCatalog(preset).Any(w => w.Id == weaponId) && WeaponCatalog.Get(weaponId).UnlockLevel > level;
    }
}
