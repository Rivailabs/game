using System;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Combat
{
    /// <summary>Duel outcome after the most recent health update.</summary>
    public enum DuelResult : byte
    {
        InProgress = 0,
        AWins = 1,
        BWins = 2,
        Draw = 3,
    }

    /// <summary>
    /// Per-player duel state. Statuses are stored as the volley in which their single due instance
    /// activates (0 = none); expiry removes only an instance due in the volley just resolved, never a
    /// newly queued replacement. Nothing here survives into the next duel.
    /// </summary>
    public sealed class PlayerDuelState
    {
        public PlayerSide Side { get; }
        public Loadout Loadout { get; set; }
        public int HpUnits { get; set; } = RulesConstants.StartHpUnits;

        /// <summary>Match-level Brahmastra charge (meaningful only when the room flag is enabled).</summary>
        public bool BrahmastraAvailable { get; set; }

        /// <summary>Gale Push baseline displacement along this player's local Right, within ±0.50 m.</summary>
        public Fixed BaselineOffsetRight { get; set; }

        /// <summary>Volley in which a Burn (5.00 HP) is due; 0 when none.</summary>
        public int BurnDueVolley { get; set; }
        /// <summary>Volley in which Shock suppresses this player's optional ability; 0 when none.</summary>
        public int ShockDueVolley { get; set; }
        /// <summary>Volley in which Storm Net forces this player's dodge to None; 0 when none.</summary>
        public int NetDueVolley { get; set; }
        /// <summary>Volley in which Quake adds +5° to this player's submitted pitch; 0 when none.</summary>
        public int QuakeDueVolley { get; set; }
        /// <summary>Volley whose weapon label and controls are concealed from the opponent (Mist Veil); 0 when none.</summary>
        public int VeilVolley { get; set; }
        /// <summary>First volley of this player's Iron Wall cover; 0 when none.</summary>
        public int IronWallFromVolley { get; set; }
        /// <summary>Last volley of this player's Iron Wall cover.</summary>
        public int IronWallUntilVolley { get; set; }

        public PlayerDuelState(PlayerSide side, Loadout loadout)
        {
            Side = side;
            Loadout = loadout;
        }

        public bool IronWallActiveIn(int volley) =>
            IronWallFromVolley != 0 && IronWallFromVolley <= volley && volley <= IronWallUntilVolley;

        public void ClearStatuses()
        {
            BurnDueVolley = ShockDueVolley = NetDueVolley = QuakeDueVolley = VeilVolley = 0;
            IronWallFromVolley = IronWallUntilVolley = 0;
        }

        public PlayerDuelState Clone() => (PlayerDuelState)MemberwiseClone();
    }

    /// <summary>
    /// Complete authoritative state of one duel between volleys: health, statuses, baselines, cover,
    /// Forest charge, the duel's terrain and defender, loadouts and the Brahmastra room flag.
    /// The resolver never mutates an input state; it returns a new one.
    /// </summary>
    public sealed class DuelState
    {
        /// <summary>One-based match round (1..8); part of projectile identity.</summary>
        public int RoundIndex { get; set; }
        /// <summary>The volley that resolves next (1..3); stays at the last resolved volley once the duel ends.</summary>
        public int VolleyIndex { get; set; } = 1;
        public TerrainType Terrain { get; set; }
        /// <summary>Terrain benefits only the defender.</summary>
        public PlayerSide Defender { get; set; }
        public CatalogPreset Catalog { get; set; }
        /// <summary>Private-room experimental Brahmastra flag (applies to both players).</summary>
        public bool BrahmastraEnabled { get; set; }
        /// <summary>Flood removed the defender's Fort cover for the rest of this duel.</summary>
        public bool FortCoverRemoved { get; set; }
        /// <summary>The defender's single Forest charge for this duel.</summary>
        public bool ForestChargeAvailable { get; set; }
        public DuelResult Result { get; set; }

        /// <summary>
        /// Balance values pinned to the match (ticket 24); <see cref="RulesParameters.Default"/>
        /// (AK-TR-1) unless the duel was started with a tuned snapshot. Shared by clones.
        /// </summary>
        public RulesParameters Parameters { get; set; } = RulesParameters.Default;

        public PlayerDuelState A { get; set; }
        public PlayerDuelState B { get; set; }

        public PlayerDuelState this[PlayerSide side] => side == PlayerSide.A ? A : B;

        public bool IsOver => Result != DuelResult.InProgress;

        /// <summary>
        /// Fresh state at the start of a duel: 100.00 HP each, no statuses, zero baselines, Fort cover
        /// intact and one Forest charge. Brahmastra charges are match-level and passed in.
        /// </summary>
        public static DuelState Start(int roundIndex, TerrainType terrain, PlayerSide defender, Loadout loadoutA, Loadout loadoutB,
            bool brahmastraEnabled = false, bool brahmastraAvailableA = true, bool brahmastraAvailableB = true) =>
            Start(roundIndex, terrain, defender, loadoutA, loadoutB, RulesParameters.Default, brahmastraEnabled, brahmastraAvailableA, brahmastraAvailableB);

        /// <summary>
        /// Fresh state under a pinned balance snapshot (ticket 24): starting HP and the round range
        /// come from <paramref name="parameters"/> (null means <see cref="RulesParameters.Default"/>).
        /// </summary>
        public static DuelState Start(int roundIndex, TerrainType terrain, PlayerSide defender, Loadout loadoutA, Loadout loadoutB,
            RulesParameters parameters, bool brahmastraEnabled = false, bool brahmastraAvailableA = true, bool brahmastraAvailableB = true)
        {
            RulesParameters p = parameters ?? RulesParameters.Default;
            if (loadoutA == null) throw new ArgumentNullException(nameof(loadoutA));
            if (loadoutB == null) throw new ArgumentNullException(nameof(loadoutB));
            if (loadoutA.Preset != loadoutB.Preset)
                throw new RulesViolationException("CATALOG_MISMATCH", "Both loadouts must use the room's symmetric catalog.");
            if (roundIndex < 1 || roundIndex > p.MaxRounds)
                throw new RulesViolationException("ROUND_RANGE", "Round index must be 1-" + p.MaxRounds + ".");
            if (brahmastraEnabled && loadoutA.Preset != CatalogPreset.Full)
                throw new RulesViolationException("BRAHMASTRA_NOT_FULL", "Brahmastra may only be enabled in a Full private room.");
            return new DuelState
            {
                RoundIndex = roundIndex,
                Terrain = terrain,
                Defender = defender,
                Catalog = loadoutA.Preset,
                BrahmastraEnabled = brahmastraEnabled,
                ForestChargeAvailable = terrain == TerrainType.Forest,
                Parameters = p,
                A = new PlayerDuelState(PlayerSide.A, loadoutA) { BrahmastraAvailable = brahmastraEnabled && brahmastraAvailableA, HpUnits = p.StartHpUnits },
                B = new PlayerDuelState(PlayerSide.B, loadoutB) { BrahmastraAvailable = brahmastraEnabled && brahmastraAvailableB, HpUnits = p.StartHpUnits },
            };
        }

        /// <summary>Whether the defender currently has Fort cover.</summary>
        public bool FortCoverActive => Terrain == TerrainType.Fort && !FortCoverRemoved;

        /// <summary>Whether <paramref name="side"/> may use its Armoury reserve in this duel.</summary>
        public bool ReserveEligible(PlayerSide side) =>
            Catalog == CatalogPreset.Full && Terrain == TerrainType.Armoury && Defender == side && this[side].Loadout.HasReserve;

        public DuelState Clone()
        {
            var copy = (DuelState)MemberwiseClone();
            copy.A = A.Clone();
            copy.B = B.Clone();
            return copy;
        }
    }

    /// <summary>
    /// One player's committed choice for a volley. Weapon 1-20, 0 = server-created Pass,
    /// 1000 = experimental Brahmastra. Pitch and yaw are in quarter degrees.
    /// </summary>
    public sealed class VolleyInput
    {
        public int WeaponId { get; }
        public int PitchQdeg { get; }
        public int YawQdeg { get; }
        public int PowerPercent { get; }
        public Dodge Dodge { get; }

        public VolleyInput(int weaponId, int pitchQdeg, int yawQdeg, int powerPercent, Dodge dodge)
        {
            WeaponId = weaponId;
            PitchQdeg = pitchQdeg;
            YawQdeg = yawQdeg;
            PowerPercent = powerPercent;
            Dodge = dodge;
        }

        public bool IsPass => WeaponId == RulesConstants.PassWeaponId;
        public bool IsBrahmastra => WeaponId == RulesConstants.BrahmastraWeaponId;
        public bool IsRegular => WeaponCatalog.IsRegularId(WeaponId);

        /// <summary>
        /// Server-created timeout default: no projectile, no dodge, Neutral defensive element.
        /// Due damage-over-time and terrain healing still resolve.
        /// </summary>
        public static VolleyInput Pass() => new VolleyInput(RulesConstants.PassWeaponId, 0, 0, RulesConstants.MaxPowerPercent, Dodge.None);

        /// <summary>Brahmastra with its canonical placeholders (pitch 0, yaw 0, power 100, dodge None).</summary>
        public static VolleyInput Brahmastra() => new VolleyInput(RulesConstants.BrahmastraWeaponId, 0, 0, RulesConstants.MaxPowerPercent, Dodge.None);

        /// <summary>Defensive element for this volley: the weapon's element, or Neutral for Pass/Brahmastra.</summary>
        public Element DefensiveElement => IsRegular ? WeaponCatalog.Get(WeaponId).Element : Element.Neutral;

        public override string ToString() =>
            "weapon=" + WeaponId + " pitch=" + PitchQdeg + "q yaw=" + YawQdeg + "q power=" + PowerPercent + "% dodge=" + Dodge;
    }

    /// <summary>LockInput command payload: the volley it targets plus the choice.</summary>
    public sealed class LockInput
    {
        public int VolleyIndex { get; }
        public VolleyInput Choice { get; }

        public LockInput(int volleyIndex, VolleyInput choice)
        {
            VolleyIndex = volleyIndex;
            Choice = choice ?? throw new ArgumentNullException(nameof(choice));
        }
    }
}
