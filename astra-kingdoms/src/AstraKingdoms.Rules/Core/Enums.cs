namespace AstraKingdoms.Rules.Core
{
    /// <summary>The two seats in an AK-TR-1 match. A starts on the x &lt; 128 half.</summary>
    public enum PlayerSide : byte
    {
        A = 0,
        B = 1,
    }

    /// <summary>Elements. Neutral is used by Pass and enabled Brahmastra and has no advantage either way.</summary>
    public enum Element : byte
    {
        Neutral = 0,
        Agni = 1,
        Vayu = 2,
        Prithvi = 3,
        Vidyut = 4,
        Varuna = 5,
    }

    /// <summary>Dodge choice. Wire values are fixed by the command contract.</summary>
    public enum Dodge : byte
    {
        None = 0,
        Left = 1,
        Right = 2,
        Jump = 3,
    }

    /// <summary>Speed label of a weapon's launch profile.</summary>
    public enum SpeedProfile : byte
    {
        Normal = 0,   // 12 m/s
        Fast = 1,     // 15 m/s
        Slow = 2,     // 11 m/s
        VerySlow = 3, // 10.5 m/s
        Direct = 4,   // 16 m/s
    }

    /// <summary>Trajectory label of a weapon's launch profile; selects pitch range and gravity.</summary>
    public enum TrajectoryProfile : byte
    {
        Normal = 0,   // 0..65 deg
        Flat = 1,     // -5..20 deg
        High = 2,     // 25..75 deg
        VeryHigh = 3, // 45..80 deg
        Direct = 4,   // -10..10 deg, zero gravity
    }

    /// <summary>Formation cards. Wire values are fixed by the SubmitCut contract.</summary>
    public enum CardId : byte
    {
        Chakra = 1,
        Garuda = 2,
        Suchi = 3,
        Makara = 4,
        Padma = 5,
        Vajra = 6,
    }

    /// <summary>Terrain categories stored per board cell.</summary>
    public enum TerrainType : byte
    {
        Plain = 0,
        Fort = 1,
        River = 2,
        Forest = 3,
        Armoury = 4,
    }

    /// <summary>Symmetric room catalog presets.</summary>
    public enum CatalogPreset : byte
    {
        Starter = 0,
        Full = 1,
    }

    /// <summary>Weapon abilities. Intrinsic properties survive Shock; optional abilities do not.</summary>
    public enum WeaponAbility : byte
    {
        Burn = 1,             // Ember Arrow
        Push = 2,             // Gale Arrow
        JumpPierce = 3,       // Stone Arrow
        Shock = 4,            // Spark Arrow
        Cleanse = 5,          // Tide Arrow
        FanSpread = 6,        // Fire Fan (intrinsic)
        TwinCurve = 7,        // Twin Gust (intrinsic)
        GroundBurst = 8,      // Boulder Shot (intrinsic, cannot clash)
        ChainBonus = 9,       // Chain Bolt
        Veil = 10,            // Mist Veil
        AshShield = 11,       // Ash Shield
        ReverseDodge = 12,    // Cyclone
        IronWall = 13,        // Iron Wall
        Net = 14,             // Storm Net
        RemoveCover = 15,     // Flood Arrow
        StraightLance = 16,   // Sun Lance (intrinsic)
        IgnoreCover = 17,     // Sky Dive
        Quake = 18,           // Quake Arrow
        ThunderAdvantage = 19,// Thunder Crown
        OceanHeal = 20,       // Ocean Call
    }

    /// <summary>Card cut submission mode.</summary>
    public enum CutMode : byte
    {
        Manual = 0,
        Auto = 1,
    }
}
