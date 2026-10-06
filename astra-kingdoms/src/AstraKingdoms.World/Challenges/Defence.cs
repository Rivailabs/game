using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.World.Armies;
using AstraKingdoms.World.Season;

namespace AstraKingdoms.World.Challenges
{
    /// <summary>How the defender meets an encounter. Chosen before the snapshot; never switched mid-duel.</summary>
    public enum DefenceMode : byte
    {
        /// <summary>The published bot policy plays the defender's seat from legal observations only.</summary>
        Automatic = 0,
        /// <summary>The defender plays in person.</summary>
        Live = 1,
    }

    /// <summary>
    /// A defender's published defence (plan: "Defenders publish a legal defensive loadout and bot
    /// policy; defaults are available"). The loadout is validated against the Full catalog that is
    /// loaned to every competitor, so automatic defence has equal catalog/budget access.
    /// </summary>
    public sealed class DefencePublication
    {
        public IReadOnlyList<int> Weapons { get; }
        public int Reserve { get; }
        public BotDifficulty Policy { get; }
        public DefenceMode Mode { get; }
        public TacticalArmy Army { get; }

        private DefencePublication(int[] weapons, int reserve, BotDifficulty policy, DefenceMode mode, TacticalArmy army)
        {
            Weapons = weapons;
            Reserve = reserve;
            Policy = policy;
            Mode = mode;
            Army = army;
        }

        /// <summary>Validates and creates a publication. Throws <see cref="RulesViolationException"/> or <see cref="ArgumentException"/>.</summary>
        public static DefencePublication Create(IReadOnlyList<int> weapons, int reserve, BotDifficulty policy, DefenceMode mode, TacticalArmy army)
        {
            Loadout.Create(CatalogPreset.Full, weapons, reserve); // throws on an illegal loadout
            if (!Enum.IsDefined(typeof(BotDifficulty), policy)) throw new ArgumentOutOfRangeException(nameof(policy));
            var copy = new int[weapons.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = weapons[i];
            return new DefencePublication(copy, reserve, policy, mode, army ?? TacticalArmy.Empty);
        }

        /// <summary>The default every account starts with: one weapon per element plus one more, Normal policy, automatic.</summary>
        public static DefencePublication Default { get; } = Create(new[] { 1, 2, 3, 4, 5, 19 }, 0, BotDifficulty.Normal, DefenceMode.Automatic, TacticalArmy.Empty);

        public void WriteTo(CanonicalWriter w)
        {
            w.U32((uint)Weapons.Count);
            foreach (int id in Weapons) w.I32(id);
            w.I32(Reserve).U8((int)Policy).U8((int)Mode);
            Army.WriteTo(w);
        }
    }

    /// <summary>
    /// The defender seat's bot policy: the published loadout, then the existing AK-TR-1
    /// <see cref="BotPolicy"/> for volleys and cuts. Like every <see cref="IBotPolicy"/> it receives a
    /// <see cref="BotObservation"/> built from one private <see cref="PlayerView"/> only, so it cannot
    /// inspect the attacker's hidden selection.
    /// </summary>
    public sealed class PublishedDefencePolicy : IBotPolicy
    {
        private readonly DefencePublication _publication;
        private readonly BotPolicy _inner;

        public PublishedDefencePolicy(DefencePublication publication, BotRng rng)
        {
            _publication = publication ?? throw new ArgumentNullException(nameof(publication));
            _inner = new BotPolicy(publication.Policy, rng);
        }

        public BotDifficulty Difficulty => _publication.Policy;

        public void ChooseLoadout(BotObservation observation, out int[] weapons, out int reserve)
        {
            weapons = new int[_publication.Weapons.Count];
            for (int i = 0; i < weapons.Length; i++) weapons[i] = _publication.Weapons[i];
            reserve = _publication.Reserve;
        }

        public VolleyInput ChooseVolley(BotObservation observation) => _inner.ChooseVolley(observation);

        public CutPlan ChooseCut(BotObservation observation) => _inner.ChooseCut(observation);
    }

    /// <summary>
    /// Fair-opponent rule (PROPOSED): ratings within ±200 and experience bands at most one apart, so
    /// a newcomer never meets a veteran merely because both hold twelve tiles.
    /// </summary>
    public static class OpponentPolicy
    {
        public static bool IsEligible(AccountSnapshot attacker, AccountSnapshot defender)
        {
            if (attacker == null || defender == null) return false;
            if (Math.Abs(attacker.Rating - defender.Rating) > WorldRules.MaxRatingDifference) return false;
            return Math.Abs((int)attacker.Band - (int)defender.Band) <= 1;
        }
    }
}
