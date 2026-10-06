using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.World.Armies
{
    /// <summary>Tactical army roles (PROPOSED). Their effects are not implemented: see <see cref="ArmyRules"/>.</summary>
    public enum ArmyRole : byte
    {
        Scout = 1,
        Guard = 2,
        Banner = 3,
        Vanguard = 4,
        Engineer = 5,
    }

    /// <summary>One row of the role table: deployment cost and slot limit per encounter.</summary>
    public sealed class ArmyRoleSpec
    {
        public ArmyRole Role { get; }
        public int Cost { get; }
        public int MaxSlots { get; }
        /// <summary>World encounters after which the role's own appearance is unlocked (presentation only).</summary>
        public int CosmeticUnlockEncounters { get; }
        public string Intent { get; }

        public ArmyRoleSpec(ArmyRole role, int cost, int maxSlots, int cosmeticUnlockEncounters, string intent)
        {
            Role = role;
            Cost = cost;
            MaxSlots = maxSlots;
            CosmeticUnlockEncounters = cosmeticUnlockEncounters;
            Intent = intent;
        }
    }

    /// <summary>One deployed role with a count.</summary>
    public readonly struct ArmySlot
    {
        public readonly ArmyRole Role;
        public readonly int Count;

        public ArmySlot(ArmyRole role, int count)
        {
            Role = role;
            Count = count;
        }
    }

    /// <summary>
    /// The PROPOSED tactical-army budget (plan: "Ten total deployment points per encounter, with
    /// role-specific costs and slot limits. Unlocks provide choices; every eligible competitor
    /// receives the same available choices through loans.").
    /// <para>
    /// <b>Deliberately effect-free.</b> The plan says role effects "need separate simulations and
    /// rule approval" and may not add unlimited units, account-age power, private-input visibility or
    /// passive duel-stat bonuses. So in this candidate an army is a validated, snapshotted tactical
    /// choice with <b>no</b> effect on the AK-TR-1 encounter. Any future effect needs its own rules
    /// version. Earned resources cannot raise the budget: there is no API that does.
    /// </para>
    /// </summary>
    public static class ArmyRules
    {
        private static readonly ArmyRoleSpec[] Table =
        {
            new ArmyRoleSpec(ArmyRole.Scout, 2, 2, 0, "scouting (candidate: earlier terrain announcement)"),
            new ArmyRoleSpec(ArmyRole.Guard, 3, 2, 3, "guarding (candidate: defender formation choice)"),
            new ArmyRoleSpec(ArmyRole.Banner, 1, 2, 0, "cosmetic standard"),
            new ArmyRoleSpec(ArmyRole.Vanguard, 4, 1, 10, "formation choice (candidate: card offer order)"),
            new ArmyRoleSpec(ArmyRole.Engineer, 2, 2, 5, "formation choice (candidate: cut preview aid)"),
        };

        public static IReadOnlyList<ArmyRoleSpec> Roles => Table;

        public static ArmyRoleSpec Spec(ArmyRole role)
        {
            foreach (ArmyRoleSpec s in Table)
                if (s.Role == role) return s;
            return null;
        }

        /// <summary>
        /// The roles an account may deploy: always the full table. Unlocking only changes appearance;
        /// a role not yet unlocked is loaned, so every eligible competitor has the same choices.
        /// </summary>
        public static IReadOnlyList<ArmyRoleSpec> AvailableTo(int worldEncounters) => Table;

        /// <summary>Returns null when legal, otherwise a violation code.</summary>
        public static string Validate(IReadOnlyList<ArmySlot> slots)
        {
            if (slots == null) return "ARMY_NULL";
            int total = 0;
            var seen = new HashSet<ArmyRole>();
            foreach (ArmySlot s in slots)
            {
                ArmyRoleSpec spec = Spec(s.Role);
                if (spec == null) return "ARMY_UNKNOWN_ROLE";
                if (!seen.Add(s.Role)) return "ARMY_DUPLICATE_ROLE";
                if (s.Count <= 0) return "ARMY_COUNT";
                if (s.Count > spec.MaxSlots) return "ARMY_SLOT_LIMIT";
                total += spec.Cost * s.Count;
            }
            if (total > WorldRules.DeploymentPoints) return "ARMY_OVER_BUDGET";
            return null;
        }

        public static int Cost(IReadOnlyList<ArmySlot> slots)
        {
            int total = 0;
            foreach (ArmySlot s in slots) total += Spec(s.Role).Cost * s.Count;
            return total;
        }

        internal static void WriteTo(CanonicalWriter w)
        {
            w.Ascii("armies").U32((uint)Table.Length);
            foreach (ArmyRoleSpec s in Table) w.U8((int)s.Role).I32(s.Cost).I32(s.MaxSlots).I32(s.CosmeticUnlockEncounters);
        }
    }

    /// <summary>A validated army. Construction fails for anything outside the budget or slot limits.</summary>
    public sealed class TacticalArmy
    {
        public IReadOnlyList<ArmySlot> Slots { get; }
        public int Cost { get; }

        private TacticalArmy(ArmySlot[] slots, int cost)
        {
            Slots = slots;
            Cost = cost;
        }

        public static TacticalArmy Empty { get; } = new TacticalArmy(Array.Empty<ArmySlot>(), 0);

        public static TacticalArmy Create(IReadOnlyList<ArmySlot> slots)
        {
            string violation = ArmyRules.Validate(slots);
            if (violation != null) throw new ArgumentException(violation, nameof(slots));
            var copy = new ArmySlot[slots.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = slots[i];
            return new TacticalArmy(copy, ArmyRules.Cost(slots));
        }

        public void WriteTo(CanonicalWriter w)
        {
            w.U32((uint)Slots.Count);
            foreach (ArmySlot s in Slots) w.U8((int)s.Role).I32(s.Count);
        }
    }
}
