using System;
using System.Collections.Generic;

namespace AstraKingdoms.Rules.Core
{
    /// <summary>Thrown when data or commands violate the AK-TR-1 contract.</summary>
    public sealed class RulesViolationException : Exception
    {
        public string Code { get; }

        public RulesViolationException(string code, string message) : base(code + ": " + message)
        {
            Code = code;
        }
    }

    /// <summary>
    /// A match loadout (ticket 15): one to five (Starter) or one to six (Full) distinct weapons,
    /// plus an optional seventh distinct reserve that is legal only in Full with six normal slots.
    /// Account level never restricts room catalogs; Full loans all 20 weapons to both players.
    /// </summary>
    public sealed class Loadout
    {
        private readonly int[] _weapons;

        public IReadOnlyList<int> Weapons => _weapons;
        /// <summary>Reserve weapon ID, or 0 when none.</summary>
        public int Reserve { get; }
        public CatalogPreset Preset { get; }

        private Loadout(CatalogPreset preset, int[] weapons, int reserve)
        {
            Preset = preset;
            _weapons = weapons;
            Reserve = reserve;
        }

        public bool Contains(int weaponId)
        {
            foreach (var w in _weapons)
                if (w == weaponId) return true;
            return false;
        }

        public bool HasReserve => Reserve != 0;

        /// <summary>Validates and creates a loadout. Throws <see cref="RulesViolationException"/> on illegal input.</summary>
        public static Loadout Create(CatalogPreset preset, IReadOnlyList<int> weapons, int reserve = 0)
        {
            if (weapons == null || weapons.Count < RulesConstants.MinSlots)
                throw new RulesViolationException("LOADOUT_EMPTY", "At least one equipped weapon is required.");
            int max = preset == CatalogPreset.Starter ? RulesConstants.StarterMaxSlots : RulesConstants.FullMaxSlots;
            if (weapons.Count > max)
                throw new RulesViolationException("LOADOUT_TOO_MANY", "At most " + max + " weapons in " + preset + ".");

            var seen = new HashSet<int>();
            var catalog = new HashSet<int>();
            foreach (var w in WeaponCatalog.ForPreset(preset)) catalog.Add(w.Id);
            foreach (var id in weapons)
            {
                if (!catalog.Contains(id))
                    throw new RulesViolationException("LOADOUT_NOT_IN_CATALOG", "Weapon " + id + " is not in the " + preset + " catalog.");
                if (!seen.Add(id))
                    throw new RulesViolationException("LOADOUT_DUPLICATE", "Weapon " + id + " is equipped twice.");
            }

            if (reserve != 0)
            {
                if (preset != CatalogPreset.Full)
                    throw new RulesViolationException("RESERVE_NOT_FULL", "A reserve is only legal in the Full catalog.");
                if (weapons.Count != RulesConstants.FullMaxSlots)
                    throw new RulesViolationException("RESERVE_NEEDS_SIX", "A reserve requires all six normal slots.");
                if (!catalog.Contains(reserve))
                    throw new RulesViolationException("RESERVE_NOT_IN_CATALOG", "Reserve " + reserve + " is not in the catalog.");
                if (seen.Contains(reserve))
                    throw new RulesViolationException("RESERVE_DUPLICATE", "Reserve must be distinct from equipped weapons.");
            }

            var copy = new int[weapons.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = weapons[i];
            return new Loadout(preset, copy, reserve);
        }
    }
}
