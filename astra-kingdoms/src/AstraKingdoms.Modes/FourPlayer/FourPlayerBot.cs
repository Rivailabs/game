using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;

namespace AstraKingdoms.Modes.FourPlayer
{
    /// <summary>
    /// A simple labelled bot for four-player discovery runs and tests. It decides from one
    /// <see cref="FourPlayerParticipantView"/> only, so it cannot see another player's hidden lock.
    /// It is never used to replace an absent human (the candidate forbids undisclosed substitutes).
    /// Aim comes from the public <see cref="AimSolver"/> against the opponent's visible baseline,
    /// with difficulty noise as in the two-player bots; cuts are Auto Cuts with the largest-quota
    /// card at the largest legal scale, centred on a loser cell chosen by the bot's seeded RNG.
    /// </summary>
    public sealed class FourPlayerBot
    {
        private readonly BotRng _rng;
        private readonly CatalogPreset _catalog;

        public Kingdom Self { get; }
        public BotDifficulty Difficulty { get; }

        public FourPlayerBot(Kingdom self, BotDifficulty difficulty, CatalogPreset catalog, BotRng rng)
        {
            Self = self;
            Difficulty = difficulty;
            _catalog = catalog;
            _rng = rng ?? throw new ArgumentNullException(nameof(rng));
        }

        /// <summary>Standard bot for a label, seeded from the match seed.</summary>
        public static FourPlayerBot Create(Kingdom self, BotDifficulty difficulty, CatalogPreset catalog, byte[] matchSeed) =>
            new FourPlayerBot(self, difficulty, catalog,
                BotRng.FromMatchSeed(matchSeed, PlayerSide.A, 0x4B1D_0000UL + ((ulong)self << 8) + (ulong)difficulty));

        /// <summary>The next command for this kingdom, or null when it has nothing to do.</summary>
        public FourPlayerCommand Decide(FourPlayerParticipantView view)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (view.Viewer != Self) throw new ArgumentException("View belongs to another kingdom.", nameof(view));
            if (view.Eliminated) return null;
            switch (view.Phase)
            {
                case FourPlayerPhase.Setup:
                    return view.OwnLoadout == null ? ChooseLoadout() : null;
                case FourPlayerPhase.Wave:
                    if (!view.InDuel) return null;
                    if (view.Stage == PairStage.Selection && view.OwnLock == null)
                        return new Lock4P(Self, view.Wave, view.Volley, ChooseVolley(view));
                    if (view.IsCutTurn) return ChooseCut(view);
                    return null;
                default:
                    return null;
            }
        }

        private FourPlayerCommand ChooseLoadout()
        {
            var catalog = new List<int>();
            foreach (WeaponDefinition w in WeaponCatalog.ForPreset(_catalog)) catalog.Add(w.Id);
            if (_catalog == CatalogPreset.Starter) return new SubmitLoadout4P(Self, catalog.ToArray());
            var chosen = new List<int>();
            while (chosen.Count < RulesConstants.FullMaxSlots)
            {
                int i = _rng.Next(catalog.Count);
                chosen.Add(catalog[i]);
                catalog.RemoveAt(i);
            }
            chosen.Sort();
            return new SubmitLoadout4P(Self, chosen.ToArray());
        }

        private VolleyInput ChooseVolley(FourPlayerParticipantView v)
        {
            int weaponId = _rng.Pick(v.OwnLoadout.Weapons);
            WeaponDefinition weapon = WeaponCatalog.Get(weaponId);
            AimSolution aim = AimSolver.Solve(weaponId, v.DuelSide, Fixed.FromRaw(v.OwnBaselineRaw), Fixed.FromRaw(v.OpponentBaselineRaw));
            int pitchNoise = Difficulty == BotDifficulty.Easy ? 24 : Difficulty == BotDifficulty.Normal ? 8 : 1;
            int yawNoise = Difficulty == BotDifficulty.Easy ? 8 : Difficulty == BotDifficulty.Normal ? 4 : 1;
            int pitch = aim.PitchQdeg + _rng.Range(-pitchNoise, pitchNoise);
            int yaw = aim.YawQdeg + _rng.Range(-yawNoise, yawNoise);
            if (v.OwnQuakeDue && Difficulty != BotDifficulty.Easy) pitch -= 5 * RulesConstants.QuarterDegreesPerDegree;
            pitch = LaunchProfiles.CentralPitchRange(weapon).Clamp(pitch);
            yaw = LaunchProfiles.CentralYawRange(weapon).Clamp(yaw);
            Dodge dodge = v.OwnNetDue ? Dodge.None : (Dodge)_rng.Next(4);
            return new VolleyInput(weaponId, pitch, yaw, RulesConstants.MaxPowerPercent, dodge);
        }

        private FourPlayerCommand ChooseCut(FourPlayerParticipantView v)
        {
            int best = 0;
            for (int i = 1; i < v.OfferedQuotas.Count; i++)
                if (v.OfferedQuotas[i] > v.OfferedQuotas[best]) best = i;
            CardId card = v.OfferedCards[best];
            int quota = v.OfferedQuotas[best];
            FourOwnerTerritory board = v.CloneBoard();
            List<int> loserCells = board.CellsOwnedBy(v.Opponent.Value);
            if (loserCells.Count == 0 || quota <= 0) return null;
            int anchor = loserCells[_rng.Next(loserCells.Count)];
            int rotation = _rng.Next(RulesConstants.RotationSteps);
            int scale = FourPlayerCutRules.LargestFittingScale(card, rotation, quota);
            if (scale == 0) return null;
            var pose = new CardPose(Board.X(anchor), Board.Y(anchor), scale, rotation);
            return new SubmitCut4P(Self, v.Wave, card, pose, CellPoint.FromCellId(anchor));
        }
    }

    /// <summary>Runs a four-player match to completion with four bots (discovery and tests).</summary>
    public static class FourPlayerBotRunner
    {
        public static FourPlayerMatch Run(FourPlayerConfig config, IReadOnlyList<string> entrants, byte[] seed, string matchId,
            BotDifficulty difficulty = BotDifficulty.Normal, int maxSteps = 2000)
        {
            FourPlayerMatch match = FourPlayerMatch.Create(config, entrants, seed, matchId);
            var bots = new FourPlayerBot[FourPlayerRules.Seats];
            for (int i = 0; i < bots.Length; i++) bots[i] = FourPlayerBot.Create((Kingdom)i, difficulty, config.Catalog, seed);
            Play(match, bots, maxSteps);
            return match;
        }

        /// <summary>
        /// Lets every bot act; when none can, issues the server deadline for the first pair still
        /// selecting (missing locks become Pass) or cutting (zero transfer).
        /// </summary>
        public static void Play(FourPlayerMatch match, IReadOnlyList<FourPlayerBot> bots, int maxSteps = 2000)
        {
            for (int step = 0; step < maxSteps && !match.IsFinished; step++)
            {
                bool acted = false;
                foreach (FourPlayerBot bot in bots)
                {
                    if (bot == null || match.IsFinished) continue;
                    FourPlayerCommand cmd = bot.Decide(match.GetView(bot.Self));
                    if (cmd == null) continue;
                    ModeReceipt r = match.Submit(cmd);
                    if (!r.Accepted && !(cmd is SubmitCut4P))
                        throw new InvalidOperationException("Bot command rejected: " + r);
                    if (!r.Accepted) match.Submit(new ExpireCut4P(match.Wave, SlotOf(match, cmd.Sender.Value)));
                    acted = true;
                }
                if (acted || match.IsFinished) continue;
                ServerTick(match);
            }
            if (!match.IsFinished) throw new InvalidOperationException("Four-player match did not finish within " + maxSteps + " steps.");
        }

        /// <summary>Applies the deadline of the first unfinished pair (or the setup deadline).</summary>
        public static void ServerTick(FourPlayerMatch match)
        {
            if (match.Phase == FourPlayerPhase.Setup)
            {
                match.Submit(new ExpireSetup4P());
                return;
            }
            WavePlan plan = match.CurrentPlan;
            for (int slot = 0; slot < plan.Pairs.Count; slot++)
            {
                PairStage stage = match.StageOf(slot);
                if (stage == PairStage.Selection) { match.Submit(new ExpireSelection4P(match.Wave, slot)); return; }
                if (stage == PairStage.Cut) { match.Submit(new ExpireCut4P(match.Wave, slot)); return; }
            }
        }

        private static int SlotOf(FourPlayerMatch match, Kingdom k)
        {
            WavePlan plan = match.CurrentPlan;
            for (int i = 0; i < plan.Pairs.Count; i++)
                if (plan.Pairs[i].Contains(k)) return i;
            return 0;
        }
    }
}
