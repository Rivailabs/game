using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Combat
{
    /// <summary>
    /// LockInput validation. Invalid or out-of-range submissions are rejected with a
    /// <see cref="RulesViolationException"/> code; they are never silently converted into another shot.
    /// </summary>
    public static class InputValidator
    {
        public const string DuelOver = "DUEL_OVER";
        public const string VolleyIndexInvalid = "VOLLEY_INDEX";
        public const string PassServerOnly = "PASS_SERVER_ONLY";
        public const string WeaponUnknown = "WEAPON_UNKNOWN";
        public const string WeaponNotEquipped = "WEAPON_NOT_EQUIPPED";
        public const string ReserveNotEligible = "RESERVE_NOT_ELIGIBLE";
        public const string PitchRange = "PITCH_RANGE";
        public const string YawRange = "YAW_RANGE";
        public const string PowerRange = "POWER_RANGE";
        public const string DodgeInvalid = "DODGE_INVALID";
        public const string BrahmastraDisabled = "BRAHMASTRA_DISABLED";
        public const string BrahmastraSpent = "BRAHMASTRA_SPENT";
        public const string BrahmastraPlaceholder = "BRAHMASTRA_PLACEHOLDER";

        /// <summary>Validates a client LockInput against the current duel state.</summary>
        public static void ValidateLock(DuelState state, PlayerSide side, LockInput input)
        {
            if (input == null) throw new System.ArgumentNullException(nameof(input));
            if (state.IsOver) throw new RulesViolationException(DuelOver, "The duel has already ended.");
            int maxVolleys = (state.Parameters ?? RulesParameters.Default).MaxVolleys;
            if (input.VolleyIndex < 1 || input.VolleyIndex > maxVolleys)
                throw new RulesViolationException(VolleyIndexInvalid, "Volley index must be 1-" + maxVolleys + ".");
            if (input.VolleyIndex != state.VolleyIndex)
                throw new RulesViolationException(VolleyIndexInvalid, "Lock targets volley " + input.VolleyIndex + " but volley " + state.VolleyIndex + " is open.");
            if (input.Choice.IsPass)
                throw new RulesViolationException(PassServerOnly, "Pass (weapon 0) is created only by the server at the deadline.");
            ValidateChoice(state, side, input.Choice);
        }

        /// <summary>
        /// Validates a choice's eligibility and ranges (Pass is accepted here because the resolver
        /// receives server-created Passes). Pitch is checked as submitted, before any Quake shift.
        /// </summary>
        public static void ValidateChoice(DuelState state, PlayerSide side, VolleyInput choice)
        {
            if (choice == null) throw new System.ArgumentNullException(nameof(choice));
            if (choice.IsPass) return;

            if (choice.Dodge != Dodge.None && choice.Dodge != Dodge.Left && choice.Dodge != Dodge.Right && choice.Dodge != Dodge.Jump)
                throw new RulesViolationException(DodgeInvalid, "Dodge must be None, Left, Right or Jump.");

            if (choice.IsBrahmastra)
            {
                if (!state.BrahmastraEnabled)
                    throw new RulesViolationException(BrahmastraDisabled, "Brahmastra is not enabled in this room.");
                if (!state[side].BrahmastraAvailable)
                    throw new RulesViolationException(BrahmastraSpent, "Brahmastra has already been used this match.");
                if (choice.PitchQdeg != 0 || choice.YawQdeg != 0 || choice.PowerPercent != RulesConstants.MaxPowerPercent || choice.Dodge != Dodge.None)
                    throw new RulesViolationException(BrahmastraPlaceholder, "Brahmastra requires pitch 0, yaw 0, power 100 and dodge None.");
                return;
            }

            if (!WeaponCatalog.IsRegularId(choice.WeaponId))
                throw new RulesViolationException(WeaponUnknown, "Unknown weapon id " + choice.WeaponId + ".");
            Loadout loadout = state[side].Loadout;
            if (!loadout.Contains(choice.WeaponId))
            {
                if (loadout.Reserve != choice.WeaponId)
                    throw new RulesViolationException(WeaponNotEquipped, "Weapon " + choice.WeaponId + " is not in the loadout.");
                if (!state.ReserveEligible(side))
                    throw new RulesViolationException(ReserveNotEligible, "The reserve is usable only while defending an Armoury duel in Full.");
            }

            if (choice.PowerPercent < RulesConstants.MinPowerPercent || choice.PowerPercent > RulesConstants.MaxPowerPercent)
                throw new RulesViolationException(PowerRange, "Power must be 70-100%.");

            WeaponDefinition weapon = WeaponCatalog.Get(choice.WeaponId);
            QdegRange pitch = LaunchProfiles.CentralPitchRange(weapon);
            if (!pitch.Contains(choice.PitchQdeg))
                throw new RulesViolationException(PitchRange, "Pitch " + choice.PitchQdeg + " qdeg outside " + pitch + " for " + weapon.Name + ".");
            QdegRange yaw = LaunchProfiles.CentralYawRange(weapon);
            if (!yaw.Contains(choice.YawQdeg))
                throw new RulesViolationException(YawRange, "Yaw " + choice.YawQdeg + " qdeg outside " + yaw + " for " + weapon.Name + ".");
        }
    }
}
