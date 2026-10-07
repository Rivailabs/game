using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Rules.Bots
{
    /// <summary>
    /// The three bounded bot difficulties (ticket 22). Documented differences:
    /// <list type="table">
    /// <item><term>Aim</term><description>All start from <see cref="AimSolver"/>'s baseline solution. Easy adds up to ±6° pitch / ±2° yaw
    /// error, Normal ±2° / ±1°, Hard ±0.25°. Normal and Hard compensate a visible Quake (+5°); Easy does not.</description></item>
    /// <item><term>Weapon</term><description>Easy: uniform from the loadout. Normal: 50% counter to the opponent's last
    /// revealed element, else uniform. Hard: expected-value pick over the opponent's revealed element history
    /// (attack multiplier minus defensive exposure), cover-aware, uses the Armoury reserve when eligible.</description></item>
    /// <item><term>Dodge</term><description>Easy: uniform over None/Left/Right/Jump. Normal and Hard: mostly side dodges;
    /// Hard does not waste a dodge while netted.</description></item>
    /// <item><term>Brahmastra</term><description>(flagged rooms only) Normal and Hard fire it when the opponent is at or below 60 HP.</description></item>
    /// <item><term>Cut</term><description>Easy: one anchor, one rotation, the largest-quota card at 60% scale (1 preview). Normal: two anchors,
    /// two rotations, the largest-quota card (4 previews). Hard: every offered card x four rotations at one anchor,
    /// then five more anchors for the best (up to 17 previews).</description></item>
    /// </list>
    /// No difficulty receives extra information or damage.
    /// </summary>
    public sealed class BotPolicy : IBotPolicy
    {
        private readonly BotRng _rng;

        public BotDifficulty Difficulty { get; }

        public BotPolicy(BotDifficulty difficulty, BotRng rng)
        {
            Difficulty = difficulty;
            _rng = rng ?? throw new ArgumentNullException(nameof(rng));
        }

        // ---------------- Loadout ----------------

        public void ChooseLoadout(BotObservation observation, out int[] weapons, out int reserve)
        {
            CatalogPreset preset = observation.View.Config.Catalog;
            var catalog = new List<int>();
            foreach (WeaponDefinition w in WeaponCatalog.ForPreset(preset)) catalog.Add(w.Id);
            reserve = 0;
            if (preset == CatalogPreset.Starter)
            {
                weapons = catalog.ToArray();
                return;
            }

            var chosen = new List<int>();
            if (Difficulty == BotDifficulty.Hard)
            {
                // One weapon of every element, then one more, then a reserve.
                for (int e = 1; e <= 5; e++)
                {
                    var ofElement = new List<int>();
                    foreach (WeaponDefinition w in WeaponCatalog.All)
                        if ((int)w.Element == e) ofElement.Add(w.Id);
                    chosen.Add(_rng.Pick(ofElement));
                }
            }
            var remaining = new List<int>();
            foreach (int id in catalog)
                if (!chosen.Contains(id)) remaining.Add(id);
            while (chosen.Count < RulesConstants.FullMaxSlots)
            {
                int i = _rng.Next(remaining.Count);
                chosen.Add(remaining[i]);
                remaining.RemoveAt(i);
            }
            chosen.Sort();
            weapons = chosen.ToArray();
            if (Difficulty != BotDifficulty.Easy) reserve = remaining[_rng.Next(remaining.Count)];
        }

        // ---------------- Volley ----------------

        public VolleyInput ChooseVolley(BotObservation observation)
        {
            PlayerView v = observation.View;
            if (v.Config.BrahmastraEnabled && Difficulty != BotDifficulty.Easy && v.Self.BrahmastraAvailable &&
                v.Foe.HpUnits <= ScheduledStrike.BrahmastraDamageUnits)
                return VolleyInput.Brahmastra();

            var candidates = new List<int>(v.OwnLoadout.Weapons);
            if (v.ReserveEligible) candidates.Add(v.OwnLoadout.Reserve);
            int weaponId = ChooseWeapon(v, candidates);
            WeaponDefinition weapon = WeaponCatalog.Get(weaponId);

            AimSolution aim = AimSolver.Solve(weaponId, v.Viewer, Fixed.FromRaw(v.Self.BaselineOffsetRightRaw),
                Fixed.FromRaw(v.Foe.BaselineOffsetRightRaw));
            int pitchNoise, yawNoise;
            switch (Difficulty)
            {
                case BotDifficulty.Easy: pitchNoise = 24; yawNoise = 8; break;
                case BotDifficulty.Normal: pitchNoise = 8; yawNoise = 4; break;
                default: pitchNoise = 1; yawNoise = 1; break;
            }
            int pitch = aim.PitchQdeg + _rng.Range(-pitchNoise, pitchNoise);
            int yaw = aim.YawQdeg + _rng.Range(-yawNoise, yawNoise);
            if (v.Self.QuakeDue && Difficulty != BotDifficulty.Easy) pitch -= 5 * RulesConstants.QuarterDegreesPerDegree;
            pitch = LaunchProfiles.CentralPitchRange(weapon).Clamp(pitch);
            yaw = LaunchProfiles.CentralYawRange(weapon).Clamp(yaw);

            return new VolleyInput(weaponId, pitch, yaw, RulesConstants.MaxPowerPercent, ChooseDodge(v));
        }

        private int ChooseWeapon(PlayerView v, List<int> candidates)
        {
            if (Difficulty == BotDifficulty.Easy) return _rng.Pick(candidates);

            if (Difficulty == BotDifficulty.Normal)
            {
                Element last = LastRevealedOpponentElement(v);
                if (last != Element.Neutral && _rng.Chance(50))
                {
                    var counters = new List<int>();
                    foreach (int id in candidates)
                        if (ElementChart.Beats(WeaponCatalog.Get(id).Element, last)) counters.Add(id);
                    if (counters.Count > 0) return _rng.Pick(counters);
                }
                return _rng.Pick(candidates);
            }

            // Hard: expected value against the opponent's revealed element frequencies (Laplace-smoothed).
            var weight = new int[6];
            for (int e = 1; e <= 5; e++) weight[e] = 1;
            foreach (RevealedVolley h in v.History)
            {
                RevealedChoice foe = h[v.Opponent];
                if (!foe.Concealed && foe.Element != Element.Neutral) weight[(int)foe.Element] += 2;
            }
            bool foeCovered = v.Foe.IronWallActive || (v.FortCoverActive && v.Defender == v.Opponent);
            long bestScore = long.MinValue;
            var best = new List<int>();
            foreach (int id in candidates)
            {
                WeaponDefinition w = v.Config.Parameters.Weapon(id); // tuned damage of the pinned snapshot (ticket 24)
                long attack = 0, exposure = 0;
                for (int e = 1; e <= 5; e++)
                {
                    bool thunder = w.Ability == WeaponAbility.ThunderAdvantage && !v.Self.ShockDue;
                    Rational a = ElementChart.Multiplier(w.Element, (Element)e, thunder);
                    Rational d = ElementChart.Multiplier((Element)e, w.Element);
                    attack += weight[e] * a.ApplyRoundHalfUp(1000);
                    exposure += weight[e] * d.ApplyRoundHalfUp(1000);
                }
                long damage = (long)w.DamagePerProjectileUnits * w.ProjectileCount;
                if (foeCovered && (w.Ability == WeaponAbility.IgnoreCover || w.Ability == WeaponAbility.RemoveCover || w.Ability == WeaponAbility.ChainBonus))
                    damage = damage * 4 / 3;
                long score = damage * attack - 1500L * exposure * 3;
                if (score > bestScore)
                {
                    bestScore = score;
                    best.Clear();
                }
                if (score == bestScore) best.Add(id);
            }
            return _rng.Pick(best);
        }

        private static Element LastRevealedOpponentElement(PlayerView v)
        {
            for (int i = v.History.Count - 1; i >= 0; i--)
            {
                RevealedChoice foe = v.History[i][v.Opponent];
                if (!foe.Concealed && foe.Element != Element.Neutral) return foe.Element;
            }
            return Element.Neutral;
        }

        private Dodge ChooseDodge(PlayerView v)
        {
            if (Difficulty == BotDifficulty.Easy) return (Dodge)_rng.Next(4);
            if (Difficulty == BotDifficulty.Hard && v.Self.NetDue) return Dodge.None;
            int roll = _rng.Next(100);
            if (roll < 40) return Dodge.Left;
            if (roll < 80) return Dodge.Right;
            if (roll < 90) return Dodge.Jump;
            return Dodge.None;
        }

        // ---------------- Cut ----------------

        public CutPlan ChooseCut(BotObservation observation)
        {
            CutSearchBudget budget;
            switch (Difficulty)
            {
                case BotDifficulty.Easy:
                    budget = new CutSearchBudget { Anchors = 1, Rotations = 1, AllCards = false, ScalePercent = 60, CenterOffsetPercent = 30 };
                    break;
                case BotDifficulty.Normal:
                    budget = new CutSearchBudget { Anchors = 2, Rotations = 2, AllCards = false, CenterOffsetPercent = 50 };
                    break;
                default:
                    budget = new CutSearchBudget { Anchors = 1, RefineAnchors = 5, Rotations = 4, AllCards = true, CenterOffsetPercent = 60 };
                    break;
            }
            return CutPlanner.Plan(observation.View, budget, _rng);
        }
    }

    /// <summary>
    /// Drives one seat with a policy: turns the current private view into the next command (or
    /// null when this seat has nothing to do). Request IDs come from the bot's deterministic RNG.
    /// </summary>
    public sealed class BotPlayer
    {
        private readonly BotRng _ids;

        public PlayerSide Side { get; }
        public IBotPolicy Policy { get; }

        public BotPlayer(PlayerSide side, IBotPolicy policy, BotRng requestIds)
        {
            Side = side;
            Policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _ids = requestIds ?? throw new ArgumentNullException(nameof(requestIds));
        }

        /// <summary>Standard bot for a seat: policy and request-ID streams both derive from the match seed.</summary>
        public static BotPlayer Create(PlayerSide side, BotDifficulty difficulty, byte[] matchSeed)
        {
            var policy = new BotPolicy(difficulty, BotRng.FromMatchSeed(matchSeed, side, 0xB07_0000UL + (ulong)difficulty));
            return new BotPlayer(side, policy, BotRng.FromMatchSeed(matchSeed, side, 0x1D5_0000UL + (ulong)difficulty));
        }

        /// <summary>The next command for this seat given its private view, or null.</summary>
        public MatchCommand Decide(PlayerView view)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (view.Viewer != Side) throw new ArgumentException("View belongs to the other seat.", nameof(view));
            var observation = new BotObservation(view);
            switch (view.Phase)
            {
                case MatchPhase.Setup:
                    if (view.OwnLoadout != null) return null;
                    Policy.ChooseLoadout(observation, out int[] weapons, out int reserve);
                    return new SubmitLoadoutCommand(view.NewHeader(_ids.NextUuid()), weapons, reserve);
                case MatchPhase.Selection:
                    if (view.OwnLock != null) return null;
                    return new LockInputCommand(view.NewHeader(_ids.NextUuid()), view.VolleyIndex, Policy.ChooseVolley(observation));
                case MatchPhase.CardAndCut:
                    if (!view.IsCutTurn) return null;
                    CutPlan plan = Policy.ChooseCut(observation);
                    if (plan == null) return null;
                    return new SubmitCutCommand(view.NewHeader(_ids.NextUuid()), view.MapRevision, plan.Card, plan.AnchorCellId,
                        plan.Pose.CenterX, plan.Pose.CenterY, plan.Pose.Rotation, plan.Pose.ScaleQuarters, CutMode.Auto);
                default:
                    return null;
            }
        }
    }
}
