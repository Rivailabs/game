using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Sim;

/// <summary>
/// Account-level cohorts for the unlock-cohort strata (plan: levels 2-16 introduce the 15
/// non-starter weapons; 17-20 are cosmetic). Full rooms loan all 20 weapons to everyone, so a
/// cohort never changes what a player is <i>allowed</i> to equip; it models familiarity.
/// </summary>
public static class UnlockCohorts
{
    public static readonly int[] SimulatedLevels = { 1, 4, 8, 12, 16, 20 };

    public static string Band(int? level) => level switch
    {
        null => "unknown level",
        <= 1 => "L1 (starters)",
        <= 8 => "L2-8",
        <= 16 => "L9-16",
        _ => "L17-20",
    };

    public static readonly string[] Bands = { "L1 (starters)", "L2-8", "L9-16", "L17-20", "unknown level" };

    /// <summary>Weapon IDs permanently unlocked at a level.</summary>
    public static HashSet<int> Unlocked(int level) => WeaponCatalog.UnlockedAtLevel(level).Select(w => w.Id).ToHashSet();
}

/// <summary>
/// Simulation-only familiarity model (stated assumption, not a rule): a bot standing in for a
/// player of a given level equips only weapons that level has permanently unlocked, even though
/// the room loans all twenty. Volley and cut decisions are delegated unchanged to the wrapped
/// policy, which still sees only its own <see cref="BotObservation"/>.
/// </summary>
public sealed class FamiliarWeaponsPolicy : IBotPolicy
{
    private readonly IBotPolicy _inner;
    private readonly HashSet<int> _familiar;
    private readonly BotRng _rng;

    public int Level { get; }
    public BotDifficulty Difficulty => _inner.Difficulty;

    public FamiliarWeaponsPolicy(IBotPolicy inner, int level, BotRng rng)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _rng = rng ?? throw new ArgumentNullException(nameof(rng));
        Level = level;
        _familiar = UnlockCohorts.Unlocked(level);
    }

    public void ChooseLoadout(BotObservation observation, out int[] weapons, out int reserve)
    {
        _inner.ChooseLoadout(observation, out int[] proposed, out int proposedReserve);
        CatalogPreset preset = observation.View.Config.Catalog;
        int slots = preset == CatalogPreset.Starter ? RulesConstants.StarterMaxSlots : RulesConstants.FullMaxSlots;
        // Keep the inner policy's familiar picks, then fill from the remaining familiar weapons.
        var chosen = proposed.Where(_familiar.Contains).Distinct().ToList();
        var pool = WeaponCatalog.ForPreset(preset).Select(w => w.Id).Where(id => _familiar.Contains(id) && !chosen.Contains(id)).ToList();
        while (chosen.Count < slots && pool.Count > 0)
        {
            int i = _rng.Next(pool.Count);
            chosen.Add(pool[i]);
            pool.RemoveAt(i);
        }
        chosen.Sort();
        weapons = chosen.ToArray();
        // A reserve is only legal with six Full slots equipped; keep it familiar too.
        reserve = 0;
        if (preset == CatalogPreset.Full && chosen.Count == RulesConstants.FullMaxSlots && proposedReserve != 0 && pool.Count > 0)
            reserve = _familiar.Contains(proposedReserve) && !chosen.Contains(proposedReserve) ? proposedReserve : pool[_rng.Next(pool.Count)];
    }

    public VolleyInput ChooseVolley(BotObservation observation) => _inner.ChooseVolley(observation);

    public CutPlan ChooseCut(BotObservation observation) => _inner.ChooseCut(observation);
}
