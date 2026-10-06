using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Sim;

/// <summary>Who played a seat. Bots are labelled by policy; people by a pseudonymous reference only.</summary>
public sealed class SeatInfo
{
    public const string BotKind = "bot";
    public const string HumanKind = "human";

    public string Kind { get; init; } = BotKind;
    /// <summary>Stratum label: the bot difficulty ("Hard") or "human".</summary>
    public string Label { get; init; } = "";
    /// <summary>Pseudonymous tester reference for people (never a name or contact detail).</summary>
    public string? PlayerRef { get; init; }
    /// <summary>Account level at match time (unlock cohort); null when unknown.</summary>
    public int? AccountLevel { get; init; }

    public bool IsHuman => Kind == HumanKind;

    public static SeatInfo Bot(string policy, int? level = null) => new() { Kind = BotKind, Label = policy, AccountLevel = level };
    public static SeatInfo Human(string playerRef, int? level) => new() { Kind = HumanKind, Label = "human", PlayerRef = playerRef, AccountLevel = level };
}

/// <summary>Per-volley facts used for usage-conditioned statistics.</summary>
public readonly record struct VolleyFact(int Round, int Volley, int WeaponA, int WeaponB, int NetA, int NetB);

/// <summary>Per-duel facts.</summary>
public readonly record struct DuelFact(int Round, TerrainType Terrain, PlayerSide Defender, DuelResult Result, int[] WeaponsA, int[] WeaponsB);

/// <summary>Cell counts after a completed round.</summary>
public readonly record struct RoundCells(int Round, int CellsA, int CellsB);

/// <summary>
/// One finished match in a source-independent form: the same report runs over simulated bot
/// matches and verified human playtest records (ticket 25). Built only from an authoritative
/// engine (a live simulation, or a record re-executed by <c>Replayer.Verify</c>).
/// </summary>
public sealed class MatchObservation
{
    public const string BotSimulation = "bot-simulation";
    public const string Playtest = "playtest";

    public string Source { get; init; } = BotSimulation;
    public string MatchId { get; init; } = "";
    public string RulesHashHex { get; init; } = "";
    public MatchConfig Config { get; init; } = null!;
    public SeatInfo A { get; init; } = null!;
    public SeatInfo B { get; init; } = null!;
    public PlayerSide FirstAttacker { get; init; }
    public MatchResult Result { get; init; } = null!;
    public int[] LoadoutA { get; init; } = Array.Empty<int>();
    public int[] LoadoutB { get; init; } = Array.Empty<int>();
    public int ReserveA { get; init; }
    public int ReserveB { get; init; }
    public List<VolleyFact> Volleys { get; } = new();
    public List<DuelFact> Duels { get; } = new();
    public List<RoundCells> Cells { get; } = new();
    /// <summary>Simulation pairing index (bots), or -1.</summary>
    public int Pairing { get; init; } = -1;
    /// <summary>True for synthetic example records (never mixed into real data silently).</summary>
    public bool Synthetic { get; init; }

    public SeatInfo Seat(PlayerSide side) => side == PlayerSide.A ? A : B;
    public int[] Loadout(PlayerSide side) => side == PlayerSide.A ? LoadoutA : LoadoutB;
    public int Reserve(PlayerSide side) => side == PlayerSide.A ? ReserveA : ReserveB;

    /// <summary>Like-for-like stratum: both seats share a label (Hard v Hard, human v human).</summary>
    public bool Mirror => A.Label == B.Label;

    /// <summary>Unordered pairing label, e.g. "Hard v Normal" or "human v Hard".</summary>
    public string PairingLabel
    {
        get
        {
            string x = A.Label, y = B.Label;
            return string.CompareOrdinal(x, y) <= 0 ? x + " v " + y : y + " v " + x;
        }
    }

    /// <summary>+1 win, -1 loss, 0 draw/void for a side.</summary>
    public int Outcome(PlayerSide side) => Result.Winner == null ? 0 : Result.Winner == side ? 1 : -1;

    /// <summary>Cells after round <paramref name="round"/> when the match continued past it.</summary>
    public RoundCells? CheckpointAfter(int round)
    {
        if (Cells.Count <= round) return null; // the match must have started the next round
        foreach (RoundCells c in Cells)
            if (c.Round == round) return c;
        return null;
    }

    /// <summary>Builds the observation from an engine that has finished its match.</summary>
    public static MatchObservation FromEngine(MatchEngine engine, SeatInfo a, SeatInfo b, string source, int pairing = -1, bool synthetic = false)
    {
        if (engine.Result == null) throw new ArgumentException("The match has not finished.", nameof(engine));
        int[] la = Array.Empty<int>(), lb = Array.Empty<int>();
        int ra = 0, rb = 0;
        foreach (MatchEngine.LoggedCommand c in engine.CommandLog)
        {
            if (c.Command is not SubmitLoadoutCommand l || c.Sender == null) continue;
            if (c.Sender == PlayerSide.A) { la = l.Weapons.ToArray(); ra = l.Reserve; }
            else { lb = l.Weapons.ToArray(); rb = l.Reserve; }
        }
        var o = new MatchObservation
        {
            Source = source, MatchId = engine.MatchId, RulesHashHex = Hex.Encode(engine.RulesHash), Config = engine.Config,
            A = a, B = b, FirstAttacker = engine.FirstAttacker, Result = engine.Result,
            LoadoutA = la, LoadoutB = lb, ReserveA = ra, ReserveB = rb, Pairing = pairing, Synthetic = synthetic,
        };
        foreach (RoundRecord r in engine.Rounds)
        {
            int hpA = engine.Parameters.StartHpUnits, hpB = engine.Parameters.StartHpUnits; // the match's pinned starting HP
            var usedA = new SortedSet<int>();
            var usedB = new SortedSet<int>();
            foreach (VolleyRecord v in r.Volleys)
            {
                o.Volleys.Add(new VolleyFact(r.Round, v.Volley, v.WeaponA, v.WeaponB, v.HpA - hpA, v.HpB - hpB));
                hpA = v.HpA;
                hpB = v.HpB;
                if (WeaponCatalog.IsRegularId(v.WeaponA)) usedA.Add(v.WeaponA);
                if (WeaponCatalog.IsRegularId(v.WeaponB)) usedB.Add(v.WeaponB);
            }
            if (r.DuelResult != DuelResult.InProgress)
                o.Duels.Add(new DuelFact(r.Round, r.Terrain, Rules.Land.Board.Opponent(r.Attacker), r.DuelResult, usedA.ToArray(), usedB.ToArray()));
            o.Cells.Add(new RoundCells(r.Round, r.CellsA, r.CellsB));
        }
        return o;
    }
}
