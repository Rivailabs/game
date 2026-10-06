using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Client.Flow
{
    /// <summary>How a weapon is available in this room (presentation only; never a combat difference).</summary>
    public enum WeaponAccess : byte
    {
        /// <summary>Permanently unlocked by the account.</summary>
        Owned = 0,
        /// <summary>Loaned by the room's symmetric catalogue (both players get the same loan).</summary>
        Loaned = 1,
    }

    /// <summary>One row of the loadout screen.</summary>
    public sealed class LoadoutRow
    {
        public WeaponDefinition Weapon { get; internal set; }
        public WeaponAccess Access { get; internal set; }
        public bool Equipped { get; internal set; }
        public bool IsReserve { get; internal set; }
        public int MasteryStars { get; internal set; }
    }

    /// <summary>
    /// Loadout screen model (ticket 44). The catalogue is the room's symmetric preset
    /// (<see cref="WeaponCatalog.ForPreset"/>): account level only decides the Owned/Loaned label,
    /// never which weapons can be equipped, so progression can never restrict either player's
    /// equivalent catalogue. Slot limits follow the rules: Starter 1-5; Full 1-6 plus one reserve
    /// once six are equipped (usable only while defending an Armoury duel).
    /// </summary>
    public sealed class LoadoutModel
    {
        private readonly List<LoadoutRow> _rows = new List<LoadoutRow>();
        private int _reserve;

        public CatalogPreset Preset { get; }
        public int AccountLevel { get; }
        public IReadOnlyList<LoadoutRow> Rows => _rows;
        public int MaxSlots => Preset == CatalogPreset.Starter ? RulesConstants.StarterMaxSlots : RulesConstants.FullMaxSlots;
        public int Reserve => _reserve;

        public LoadoutModel(CatalogPreset preset, int accountLevel, IReadOnlyDictionary<int, int> masteryStars = null)
        {
            Preset = preset;
            AccountLevel = Math.Max(1, accountLevel);
            foreach (WeaponDefinition w in WeaponCatalog.ForPreset(preset))
            {
                int stars = 0;
                if (masteryStars != null) masteryStars.TryGetValue(w.Id, out stars);
                _rows.Add(new LoadoutRow
                {
                    Weapon = w,
                    Access = w.UnlockLevel <= AccountLevel ? WeaponAccess.Owned : WeaponAccess.Loaned,
                    MasteryStars = stars,
                });
            }
            // Default: equip as many as fit, owned (familiar) weapons first, then by ID.
            var order = new List<LoadoutRow>(_rows);
            order.Sort((a, b) => a.Access != b.Access ? a.Access.CompareTo(b.Access) : a.Weapon.Id.CompareTo(b.Weapon.Id));
            for (int i = 0; i < order.Count && i < MaxSlots; i++) order[i].Equipped = true;
        }

        public int EquippedCount
        {
            get
            {
                int n = 0;
                foreach (LoadoutRow r in _rows) if (r.Equipped) n++;
                return n;
            }
        }

        public bool AnyLoaned
        {
            get
            {
                foreach (LoadoutRow r in _rows) if (r.Access == WeaponAccess.Loaned) return true;
                return false;
            }
        }

        /// <summary>Toggles equipment; returns a localization key explaining a refusal, or null.</summary>
        public string Toggle(int weaponId)
        {
            LoadoutRow row = Find(weaponId);
            if (row == null) return "loadout.notInRoom";
            if (row.Equipped)
            {
                if (EquippedCount <= RulesConstants.MinSlots) return "loadout.needOne";
                row.Equipped = false;
                ClearReserve(); // a reserve needs six equipped
                return null;
            }
            if (EquippedCount >= MaxSlots) return "loadout.full";
            if (row.IsReserve) ClearReserve();
            row.Equipped = true;
            return null;
        }

        /// <summary>Sets the Full-room reserve (0 clears it); returns a refusal key or null.</summary>
        public string SetReserve(int weaponId)
        {
            if (weaponId == 0)
            {
                ClearReserve();
                return null;
            }
            if (Preset != CatalogPreset.Full) return "loadout.reserveFullOnly";
            if (EquippedCount != RulesConstants.FullMaxSlots) return "loadout.reserveNeedsSix";
            LoadoutRow row = Find(weaponId);
            if (row == null || row.Equipped) return "loadout.reserveDistinct";
            ClearReserve();
            row.IsReserve = true;
            _reserve = weaponId;
            return null;
        }

        public int[] EquippedIds()
        {
            var ids = new List<int>();
            foreach (LoadoutRow r in _rows) if (r.Equipped) ids.Add(r.Weapon.Id);
            ids.Sort();
            return ids.ToArray();
        }

        /// <summary>Weapon IDs the opponent may equip in this room (always the same set).</summary>
        public static IReadOnlyList<int> RoomCatalogue(CatalogPreset preset)
        {
            var ids = new List<int>();
            foreach (WeaponDefinition w in WeaponCatalog.ForPreset(preset)) ids.Add(w.Id);
            return ids;
        }

        private LoadoutRow Find(int weaponId)
        {
            foreach (LoadoutRow r in _rows) if (r.Weapon.Id == weaponId) return r;
            return null;
        }

        private void ClearReserve()
        {
            foreach (LoadoutRow r in _rows) r.IsReserve = false;
            _reserve = 0;
        }
    }

    /// <summary>
    /// Mastery and practice presentation (ticket 44): per-weapon familiarity counted from local
    /// practice and match use. Stars are recognition only; they never change combat values, slots or
    /// loans. Practice drills for a weapon open at its unlock level, but any weapon can be practised
    /// on a loan in a Full practice room.
    /// </summary>
    public static class Mastery
    {
        public static readonly int[] StarThresholds = { 5, 20, 50 };

        public static int Stars(int volleysFired)
        {
            int s = 0;
            foreach (int t in StarThresholds) if (volleysFired >= t) s++;
            return s;
        }

        /// <summary>Weapons with a dedicated drill at this level (unlocked ones), ascending by ID.</summary>
        public static IReadOnlyList<int> DrillsAvailable(int accountLevel)
        {
            var ids = new List<int>();
            foreach (WeaponDefinition w in WeaponCatalog.UnlockedAtLevel(Math.Max(1, accountLevel))) ids.Add(w.Id);
            return ids;
        }
    }
}
