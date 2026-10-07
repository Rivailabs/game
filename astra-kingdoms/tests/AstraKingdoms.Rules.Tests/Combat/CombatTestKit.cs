using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Tests.Combat;

/// <summary>Shared helpers for combat tests.</summary>
internal static class Kit
{
    public static readonly int[] AllSix = { 1, 2, 3, 4, 5, 6 };

    public static Loadout Full(params int[] weapons) => Loadout.Create(CatalogPreset.Full, weapons);

    public static Loadout FullWithReserve(int[] six, int reserve) => Loadout.Create(CatalogPreset.Full, six, reserve);

    /// <summary>Full-catalog duel where both players equip the given weapons (max six).</summary>
    public static DuelState State(int[] weaponsA, int[] weaponsB, TerrainType terrain = TerrainType.Plain,
        PlayerSide defender = PlayerSide.B, bool brahmastra = false, int round = 1)
        => DuelState.Start(round, terrain, defender, Full(weaponsA), Full(weaponsB), brahmastra);

    /// <summary>A neutral, legal choice for any regular weapon (central range minimum pitch, yaw 0).</summary>
    public static VolleyInput Shot(int weaponId, Dodge dodge = Dodge.None, int? pitchQdeg = null, int yawQdeg = 0, int power = 100)
    {
        var w = WeaponCatalog.Get(weaponId);
        int pitch = pitchQdeg ?? LaunchProfiles.CentralPitchRange(w).Min;
        return new VolleyInput(weaponId, pitch, yawQdeg, power, dodge);
    }

    public static ProjectileId Id(PlayerSide owner, int index = 0, int round = 1, int volley = 1) =>
        new ProjectileId(round, volley, owner, index);

    public static GeometricContact Contact(PlayerSide attacker, ContactKind kind, int index = 0, int tick = 60, int sub = 0,
        int round = 1, int volley = 1) => new GeometricContact(Id(attacker, index, round, volley), kind, tick, sub);

    /// <summary>
    /// Finds the first pitch (ascending) at which a lone projectile of <paramref name="weaponId"/> from
    /// <paramref name="shooter"/> makes a contact of <paramref name="wanted"/> against the given pose.
    /// </summary>
    public static int? FindPitch(PlayerSide shooter, int weaponId, TargetPose pose, ContactKind wanted, int yawQdeg = 0,
        int power = 100, bool jumpPierce = false, Fixed baseline = default)
    {
        var w = WeaponCatalog.Get(weaponId);
        var range = LaunchProfiles.CentralPitchRange(w);
        for (int p = range.Min; p <= range.Max; p++)
        {
            var specs = LaunchProfiles.BuildPattern(1, 1, shooter, w, p, yawQdeg, power, baseline, jumpPierce);
            var poseA = shooter == PlayerSide.B ? pose : new TargetPose(PlayerSide.A, Fixed.Zero, Dodge.None);
            var poseB = shooter == PlayerSide.A ? pose : new TargetPose(PlayerSide.B, Fixed.Zero, Dodge.None);
            var sim = FlightSimulator.Simulate(specs, poseA, poseB);
            foreach (var c in sim.Contacts)
                if (c.Kind == wanted) return p;
        }
        return null;
    }

    public static Fixed M(int hundredths) => Fixed.FromRatio(hundredths, 100);
}
