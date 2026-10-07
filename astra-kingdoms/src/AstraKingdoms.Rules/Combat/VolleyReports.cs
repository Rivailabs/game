using System.Collections.Generic;
using System.Text;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Combat
{
    /// <summary>How one qualifying incoming contact was resolved (steps 7-8).</summary>
    public sealed class ContactReport
    {
        public ProjectileId Projectile { get; internal set; }
        public PlayerSide Attacker => Projectile.Owner;
        public PlayerSide Target => CombatGeometry.Opponent(Projectile.Owner);
        public ContactKind Kind { get; internal set; }
        public int Tick { get; internal set; }
        public int SubTick { get; internal set; }
        /// <summary>Consumed the target's Ash Shield; no damage and no on-hit effects.</summary>
        public bool Blocked { get; internal set; }
        public Rational ElementFactor { get; internal set; } = Rational.One;
        public Rational DodgeFactor { get; internal set; } = Rational.One;
        public Rational CoverFactor { get; internal set; } = Rational.One;
        /// <summary>The target had cover at the time of this contact (after any Flood removal).</summary>
        public bool TargetCovered { get; internal set; }
        public bool CoverIgnored { get; internal set; }
        public bool CoverRemovedByFlood { get; internal set; }
        public bool ForestApplied { get; internal set; }
        /// <summary>Rounded direct damage of this contact (HP units).</summary>
        public int DamageUnits { get; internal set; }
        public int ChainBonusUnits { get; internal set; }
        /// <summary>Core/graze/burst contact with positive resolved direct damage.</summary>
        public bool IsHit => !Blocked && DamageUnits > 0;

        public override string ToString()
        {
            if (Blocked) return Projectile + " " + Kind + " blocked by Ash Shield";
            return Projectile + " " + Kind + " " + Hp.Format(DamageUnits) + " HP (element " + ElementFactor + ", dodge " + DodgeFactor +
                   ", cover " + CoverFactor + ")" + (ChainBonusUnits > 0 ? " + " + Hp.Format(ChainBonusUnits) + " chain" : string.Empty);
        }
    }

    /// <summary>
    /// Human-readable, UI-ready summary of one player's side of a volley: what they chose, what
    /// changed it, every incoming contact and the health arithmetic.
    /// </summary>
    public sealed class PlayerVolleyReport
    {
        private readonly List<ContactReport> _incoming = new List<ContactReport>();
        private readonly List<StatusKind> _queued = new List<StatusKind>();
        private readonly List<ProjectileTrack> _projectiles = new List<ProjectileTrack>();

        public PlayerSide Side { get; internal set; }
        public int WeaponId { get; internal set; }
        public string WeaponName { get; internal set; }
        public Element DefensiveElement { get; internal set; }
        public bool IsPass { get; internal set; }
        public bool IsBrahmastra { get; internal set; }

        public int SubmittedPitchQdeg { get; internal set; }
        public int EffectivePitchQdeg { get; internal set; }
        public bool QuakeApplied { get; internal set; }

        public Dodge SubmittedDodge { get; internal set; }
        public Dodge EffectiveDodge { get; internal set; }
        public bool DodgeForcedByNet { get; internal set; }
        public bool DodgeReversedByCyclone { get; internal set; }
        public bool ForestActive { get; internal set; }

        public bool CleansedBurn { get; internal set; }
        public bool CleansedShock { get; internal set; }
        /// <summary>A due Shock remained after cleanse this volley.</summary>
        public bool Shocked { get; internal set; }
        /// <summary>The weapon's optional ability was disabled by Shock.</summary>
        public bool AbilitySuppressed { get; internal set; }
        public bool AshShieldRaised { get; internal set; }
        public bool AshShieldConsumed { get; internal set; }
        public bool IronWallRaised { get; internal set; }
        /// <summary>Mist Veil: this volley's weapon label and exact controls are concealed from the opponent.</summary>
        public bool ConcealedFromOpponent { get; internal set; }

        /// <summary>This player's projectiles (playback tracks and terminations).</summary>
        public IReadOnlyList<ProjectileTrack> Projectiles => _projectiles;
        /// <summary>Qualifying contacts this player received, in resolution order.</summary>
        public IReadOnlyList<ContactReport> Incoming => _incoming;
        /// <summary>Statuses queued onto this player for the next volley.</summary>
        public IReadOnlyList<StatusKind> StatusesQueued => _queued;
        /// <summary>This player's attack produced at least one hit.</summary>
        public bool LandedHit { get; internal set; }

        public int HpBeforeUnits { get; internal set; }
        public int DirectDamageUnits { get; internal set; }
        public int BurnDamageUnits { get; internal set; }
        public int OceanHealUnits { get; internal set; }
        public int RiverHealUnits { get; internal set; }
        public int HpAfterUnits { get; internal set; }

        internal void AddIncoming(ContactReport c) => _incoming.Add(c);
        internal void AddQueued(StatusKind s) => _queued.Add(s);
        internal void AddProjectile(ProjectileTrack t) => _projectiles.Add(t);

        /// <summary>Quarter degrees as exact decimal text, e.g. 61 -> "15.25".</summary>
        public static string FormatDegrees(int qdeg)
        {
            int abs = qdeg < 0 ? -qdeg : qdeg;
            string text = (abs / 4).ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + (abs % 4 * 25).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
            return qdeg < 0 ? "-" + text : text;
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append(Side).Append(": ").Append(WeaponName);
            if (IsPass) sb.Append(" (timeout)");
            if (ConcealedFromOpponent) sb.Append(" [veiled]");
            sb.Append(", dodge ").Append(EffectiveDodge);
            if (DodgeForcedByNet) sb.Append(" (forced by Storm Net)");
            if (DodgeReversedByCyclone) sb.Append(" (reversed by Cyclone)");
            if (ForestActive) sb.Append(" (Forest 50%)");
            if (QuakeApplied) sb.Append(", pitch shifted by Quake to ").Append(FormatDegrees(EffectivePitchQdeg)).Append(" deg");
            if (CleansedBurn || CleansedShock) sb.Append(", Tide cleansed");
            if (AbilitySuppressed) sb.Append(", ability suppressed by Shock");
            sb.Append(". ");
            if (_incoming.Count == 0) sb.Append("No hits taken. ");
            foreach (var c in _incoming) sb.Append(c).Append(". ");
            if (BurnDamageUnits > 0) sb.Append("Burn ").Append(Hp.Format(BurnDamageUnits)).Append(". ");
            if (OceanHealUnits > 0) sb.Append("Ocean +").Append(Hp.Format(OceanHealUnits)).Append(". ");
            if (RiverHealUnits > 0) sb.Append("River +").Append(Hp.Format(RiverHealUnits)).Append(". ");
            sb.Append("HP ").Append(Hp.Format(HpBeforeUnits)).Append(" -> ").Append(Hp.Format(HpAfterUnits)).Append('.');
            return sb.ToString();
        }
    }

    /// <summary>Both players' reports plus the duel result after the volley.</summary>
    public sealed class VolleyExplanation
    {
        public int Round { get; internal set; }
        public int Volley { get; internal set; }
        public PlayerVolleyReport A { get; internal set; }
        public PlayerVolleyReport B { get; internal set; }
        public DuelResult ResultAfter { get; internal set; }

        public PlayerVolleyReport this[PlayerSide side] => side == PlayerSide.A ? A : B;

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.Append("Round ").Append(Round).Append(", volley ").Append(Volley).Append('\n');
            sb.Append(A.Describe()).Append('\n');
            sb.Append(B.Describe()).Append('\n');
            sb.Append("Result: ").Append(ResultAfter).Append('\n');
            return sb.ToString();
        }
    }

    /// <summary>Everything one volley produced.</summary>
    public sealed class VolleyResult
    {
        public DuelState NewState { get; internal set; }
        /// <summary>Complete authoritative event log of the volley (pre-flight, flight, resolution, settle).</summary>
        public CombatEventLog Log { get; internal set; }
        /// <summary>Flight simulation (playback tracks). Null when contacts were scripted.</summary>
        public SimulationResult Simulation { get; internal set; }
        public VolleyExplanation Explanation { get; internal set; }
    }
}
