using System;
using System.Collections.Generic;
using AstraKingdoms.Client.Localization;
using AstraKingdoms.Client.Match;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Client.Flow
{
    /// <summary>Guided starter duel steps (plan: "The tutorial introduces one choice at a time").</summary>
    public enum TutorialStep : byte
    {
        Welcome = 0,
        ChooseWeapon = 1,
        AimAndPower = 2,
        ChooseDodge = 3,
        Lock = 4,
        WatchReveal = 5,
        ReadOutcome = 6,
        /// <summary>Further volleys and duels play normally with the same controls (hints only).</summary>
        FreePlay = 7,
        DrawCut = 8,
        /// <summary>Statuses (burn, shock, net...) are introduced only after the core sequence.</summary>
        AdvancedStatuses = 9,
        Complete = 10,
    }

    /// <summary>Controls the tutorial can enable or disable.</summary>
    [Flags]
    public enum TutorialControl
    {
        None = 0,
        Continue = 1,
        Weapon = 2,
        Aim = 4,
        Power = 8,
        Dodge = 16,
        Lock = 32,
        Card = 64,
        Cut = 128,
        All = Weapon | Aim | Power | Dodge | Lock | Card | Cut | Continue,
    }

    /// <summary>Things the player or the match did that the tutorial listens for.</summary>
    public enum TutorialEvent : byte
    {
        Continue = 0,
        WeaponSelected = 1,
        AimChanged = 2,
        PowerChanged = 3,
        DodgeSelected = 4,
        Locked = 5,
        RevealFinished = 6,
        /// <summary>The outcome explanation was shown (required before moving on).</summary>
        OutcomeExplained = 7,
        CutWindowOpened = 8,
        CutConfirmed = 9,
        /// <summary>The cut window closed without a cut, or the duel was not won (no cut to draw).</summary>
        CutSkipped = 10,
        MatchEnded = 11,
    }

    /// <summary>
    /// The playable tutorial's step machine (ticket 45). It runs on a real practice match against a
    /// clearly labelled Easy bot, so every outcome is the engine's: the tutorial never scripts or
    /// awards an unexplained win, and it cannot advance past a reveal until the outcome explanation
    /// (or, after a loss, the loss explanation) has been shown. Each early step enables exactly the
    /// one new control it teaches (plus the ones already taught). The match can run untimed.
    /// Tutorial completion grants no rewards; it only records that the tutorial was finished.
    /// </summary>
    public sealed class TutorialMachine
    {
        private bool _aimed;
        private bool _powered;
        private bool _cutTaught;

        public TutorialStep Step { get; private set; } = TutorialStep.Welcome;
        public bool Untimed { get; }
        public bool OutcomeShown { get; private set; }
        public bool Finished => Step == TutorialStep.Complete;

        public TutorialMachine(bool untimed)
        {
            Untimed = untimed;
        }

        /// <summary>Localization key of the current instruction.</summary>
        public string PromptKey => "tutorial.step." + Step;

        /// <summary>Controls usable in the current step (everything else is shown but disabled).</summary>
        public TutorialControl Enabled
        {
            get
            {
                switch (Step)
                {
                    case TutorialStep.Welcome: return TutorialControl.Continue;
                    case TutorialStep.ChooseWeapon: return TutorialControl.Weapon;
                    case TutorialStep.AimAndPower: return TutorialControl.Weapon | TutorialControl.Aim | TutorialControl.Power;
                    case TutorialStep.ChooseDodge: return TutorialControl.Weapon | TutorialControl.Aim | TutorialControl.Power | TutorialControl.Dodge;
                    case TutorialStep.Lock: return TutorialControl.Weapon | TutorialControl.Aim | TutorialControl.Power | TutorialControl.Dodge | TutorialControl.Lock;
                    case TutorialStep.WatchReveal: return TutorialControl.None;
                    case TutorialStep.ReadOutcome: return TutorialControl.None;
                    case TutorialStep.FreePlay: return TutorialControl.All & ~TutorialControl.Continue;
                    case TutorialStep.DrawCut: return TutorialControl.Card | TutorialControl.Cut;
                    case TutorialStep.AdvancedStatuses: return TutorialControl.Continue;
                    default: return TutorialControl.None;
                }
            }
        }

        public bool IsEnabled(TutorialControl control) => (Enabled & control) == control;

        /// <summary>Feeds an event; returns true when the step changed.</summary>
        public bool Handle(TutorialEvent e)
        {
            TutorialStep before = Step;
            switch (Step)
            {
                case TutorialStep.Welcome:
                    if (e == TutorialEvent.Continue) Step = TutorialStep.ChooseWeapon;
                    break;
                case TutorialStep.ChooseWeapon:
                    if (e == TutorialEvent.WeaponSelected) Step = TutorialStep.AimAndPower;
                    break;
                case TutorialStep.AimAndPower:
                    if (e == TutorialEvent.AimChanged) _aimed = true;
                    if (e == TutorialEvent.PowerChanged) _powered = true;
                    if (_aimed && _powered) Step = TutorialStep.ChooseDodge;
                    break;
                case TutorialStep.ChooseDodge:
                    if (e == TutorialEvent.DodgeSelected) Step = TutorialStep.Lock;
                    break;
                case TutorialStep.Lock:
                    if (e == TutorialEvent.Locked) Step = TutorialStep.WatchReveal;
                    break;
                case TutorialStep.WatchReveal:
                    if (e == TutorialEvent.RevealFinished) Step = TutorialStep.ReadOutcome;
                    break;
                case TutorialStep.ReadOutcome:
                    if (e == TutorialEvent.OutcomeExplained) OutcomeShown = true;
                    if (e == TutorialEvent.Continue && OutcomeShown) Step = TutorialStep.FreePlay;
                    break;
                case TutorialStep.FreePlay:
                    if (e == TutorialEvent.CutWindowOpened && !_cutTaught) Step = TutorialStep.DrawCut;
                    else if (e == TutorialEvent.MatchEnded) Step = TutorialStep.AdvancedStatuses;
                    break;
                case TutorialStep.DrawCut:
                    if (e == TutorialEvent.CutConfirmed || e == TutorialEvent.CutSkipped)
                    {
                        _cutTaught = e == TutorialEvent.CutConfirmed;
                        Step = TutorialStep.FreePlay;
                    }
                    else if (e == TutorialEvent.MatchEnded) Step = TutorialStep.AdvancedStatuses;
                    break;
                case TutorialStep.AdvancedStatuses:
                    if (e == TutorialEvent.Continue) Step = TutorialStep.Complete;
                    break;
            }
            return Step != before;
        }

        /// <summary>Practice-host timings for the tutorial: untimed uses a deadline nobody reaches.</summary>
        public HostTimings Timings()
        {
            var t = new HostTimings();
            if (!Untimed) return t;
            const double never = 24 * 3600.0;
            t.ChoiceSeconds = never;
            t.CutSeconds = never;
            return t; // announcements and replays keep their short display times: they ask nothing of the player
        }
    }

    /// <summary>
    /// Why a volley went against a player, as localized reasons built from the engine's volley
    /// report (ticket 45 "loss explanation"). Every reason is a fact from the record.
    /// </summary>
    public static class LossExplanation
    {
        public static List<TextRef> Build(VolleyExplanation e, PlayerSide player, Func<PlayerSide, string> names, Localizer loc)
        {
            if (e == null) throw new ArgumentNullException(nameof(e));
            if (loc == null) throw new ArgumentNullException(nameof(loc));
            var reasons = new List<TextRef>();
            PlayerSide foe = CombatGeometry.Opponent(player);
            PlayerVolleyReport mine = e[player], theirs = e[foe];

            if (mine.IsPass) reasons.Add(new TextRef("tutorial.why.pass"));
            bool myHit = false, cancelled = false;
            foreach (ContactReport c in theirs.Incoming) myHit |= c.Attacker == player && c.IsHit;
            foreach (ProjectileTrack t in mine.Projectiles) cancelled |= t.Termination == TerminationReason.ClashDestroyed;
            if (!mine.IsPass && !myHit)
            {
                if (cancelled) reasons.Add(new TextRef("tutorial.why.clashed"));
                else if (theirs.EffectiveDodge != Dodge.None) reasons.Add(new TextRef("tutorial.why.theyDodged", names(foe), loc.Get(DodgeKey(theirs.EffectiveDodge))));
                else if (theirs.AshShieldConsumed) reasons.Add(new TextRef("tutorial.why.blocked", names(foe)));
                else reasons.Add(new TextRef("tutorial.why.missed"));
            }
            foreach (ContactReport c in mine.Incoming)
            {
                if (!c.IsHit) continue;
                if (c.ElementFactor.Numerator > c.ElementFactor.Denominator)
                {
                    reasons.Add(new TextRef("tutorial.why.elementBeatYou", loc.Get(ElementKey(theirs.DefensiveElement)), loc.Get(ElementKey(mine.DefensiveElement))));
                    break;
                }
            }
            if (mine.EffectiveDodge == Dodge.None && theirs.LandedHit) reasons.Add(new TextRef("tutorial.why.noDodge"));
            int dealt = theirs.HpBeforeUnits - theirs.HpAfterUnits, taken = mine.HpBeforeUnits - mine.HpAfterUnits;
            reasons.Add(new TextRef("tutorial.why.hpSummary", Hp.Format(Math.Max(0, taken)), Hp.Format(Math.Max(0, dealt))));
            return reasons;
        }

        /// <summary>Localization key of a dodge name.</summary>
        public static string DodgeKey(Dodge d) => "dodge." + d.ToString().ToLowerInvariant();

        public static string ElementKey(Element e) => "element." + e.ToString().ToLowerInvariant();
    }
}
