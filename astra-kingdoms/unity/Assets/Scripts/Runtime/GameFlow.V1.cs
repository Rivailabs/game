using System;
using System.Collections.Generic;
using System.IO;
using AstraKingdoms.Client.Flow;
using AstraKingdoms.Client.Land;
using AstraKingdoms.Client.Match;
using AstraKingdoms.Client.Settings;
using AstraKingdoms.Client.UI.Screens;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using UnityEngine;

namespace AstraKingdoms.Client
{
    /// <summary>
    /// V1 screens and usability (tickets 40 and 43-47), kept in its own partial file so the core
    /// match flow stays readable: launch routing (first-run settings, then the tutorial offer), the
    /// playable tutorial on a real practice match, the weapons/mastery screen, land-transfer capture,
    /// local data deletion, and the pause/background/connection overlay driven by
    /// <see cref="SessionLifecycle"/> and Unity's <c>OnApplicationPause</c>.
    /// </summary>
    public sealed partial class GameFlow
    {
        private readonly SessionLifecycle _life = new SessionLifecycle();
        private TutorialOverlay _tutorialOverlay;
        private TutorialOfferScreen _tutorialOffer;
        private SessionOverlayScreen _sessionOverlay;
        private WeaponsScreen _weapons;
        private TutorialMachine _tutorial;
        private List<TextRef> _tutorialDetails = new List<TextRef>();
        private byte[] _ownersBefore;
        private LandTransferPlan _pendingTransfer;
        private SessionOverlay _overlayShown;

        public SessionLifecycle Lifecycle => _life;

        private void BuildV1Ui(Transform root)
        {
            _tutorialOffer = Add(new TutorialOfferScreen(_ctx, _ui, root));
            _weapons = Add(new WeaponsScreen(_ctx, _ui, root));
            _tutorialOverlay = Add(new TutorialOverlay(_ctx, _ui, root));
            _sessionOverlay = Add(new SessionOverlayScreen(_ctx, _ui, root)); // last: above everything

            _tutorialOffer.Chosen += start =>
            {
                _ctx.Settings.TutorialOffered = true;
                _ctx.SaveSettings();
                if (start) StartTutorial();
                else ShowHome();
            };
            _home.StartTutorial += StartTutorial;
            _home.OpenWeapons += () =>
            {
                HideAll();
                _weapons.Open(_ctx.AccountLevel(), null);
            };
            _weapons.Closed += ShowHome;
            _weapons.PracticeWeapon += id => StartPractice(Difficulty);
            _tutorialOverlay.ContinueTapped += () => TutorialEvent(Flow.TutorialEvent.Continue);
            _selection.ChoiceMade += e => TutorialEvent(e);
            _land.Confirmed += () => TutorialEvent(Flow.TutorialEvent.CutConfirmed);
            _settings.DeleteLocalData = DeleteLocalData;
            _sessionOverlay.Resume += () =>
            {
                if (_life.Overlay == SessionOverlay.PauseMenu) TogglePause();
                _life.ResumeTapped();
                RefreshOverlay();
            };
            _sessionOverlay.Leave += GoHome;
            _sessionOverlay.OpenSettings += ShowSettings;
            _controller.DeltaFilter = dt => _life.HostDelta(dt);
        }

        private void ApplySettingsV1()
        {
            _arena.SetReducedMotion(_ctx.Settings.ReducedMotion);
            _arena.SetReducedShake(_ctx.Settings.ShakeDisabled);
            _arena.SetTextScale(_ctx.Settings.TextScale);
        }

        // ------------------------------------------------------------------ launch and modes (43)

        private void ShowLaunchScreen()
        {
            switch (LaunchFlow.First(_ctx.Settings))
            {
                case LaunchScreen.FirstRunSettings:
                    ShowSettings();
                    break;
                case LaunchScreen.TutorialOffer:
                    _controller.EndMatch();
                    HideAll();
                    _tutorialOffer.Show();
                    break;
                default:
                    ShowHome();
                    break;
            }
        }

        /// <summary>Practice room from the home screen's choices: Starter (plain) or the loaned Full catalogue.</summary>
        private MatchConfig PracticeConfig()
        {
            _arena.SetVariant(_home.ArenaVariantId);
            return _home.PracticeCatalog == CatalogPreset.Full ? MatchConfig.V1Full(MatchMode.Practice) : MatchConfig.Pilot(MatchMode.Practice);
        }

        // ------------------------------------------------------------------ tutorial (45)

        /// <summary>The guided starter duel: a real practice match against a labelled Easy bot.</summary>
        public void StartTutorial()
        {
            Mode = PlayMode.Tutorial;
            Difficulty = BotDifficulty.Easy;
            _tutorial = new TutorialMachine(_ctx.Settings.TutorialUntimed);
            _tutorialDetails = new List<TextRef>();
            _ctx.PlayerName = side => side == PlayerSide.A ? _ctx.T("player.you") : _ctx.TF("player.bot", _ctx.DifficultyName(BotDifficulty.Easy));
            _controller.ClockSpeed = 1f;
            _arena.Speed = 1f;
            _controller.StartMatch(MatchConfig.Pilot(MatchMode.Practice), SeatKind.Human, SeatKind.Bot, BotDifficulty.Easy, _tutorial.Timings());
        }

        private void TutorialEvent(Flow.TutorialEvent e)
        {
            if (Mode != PlayMode.Tutorial || _tutorial == null) return;
            if (_tutorial.Handle(e)) _tutorialDetails = new List<TextRef>();
            if (_tutorial.Finished)
            {
                _ctx.Settings.TutorialCompleted = true;
                _ctx.SaveSettings();
                _tutorialOverlay.Hide();
                return;
            }
            RefreshTutorial();
        }

        private void RefreshTutorial()
        {
            if (Mode != PlayMode.Tutorial || _tutorial == null || _tutorial.Finished) return;
            _selection.ApplyGate(_tutorial.Enabled);
            _tutorialOverlay.ShowStep(_tutorial, _tutorialDetails);
        }

        private void OnVolleyResolvedV1(ResolvedVolley v)
        {
            if (Mode != PlayMode.Tutorial || _tutorial == null) return;
            // The outcome and, when the volley went against the player, the reasons from the record.
            var e = v.Result.Explanation;
            _tutorialDetails = new List<TextRef>();
            if (e.A.HpAfterUnits - e.A.HpBeforeUnits < e.B.HpAfterUnits - e.B.HpBeforeUnits || e.A.IsPass)
                _tutorialDetails.AddRange(LossExplanation.Build(e, PlayerSide.A, _ctx.PlayerName, _ctx.Loc));
        }

        private void OnFlightCompletedV1()
        {
            if (Mode != PlayMode.Tutorial || _tutorial == null) return;
            TutorialEvent(Flow.TutorialEvent.RevealFinished);
            TutorialEvent(Flow.TutorialEvent.OutcomeExplained);
            RefreshTutorial();
        }

        // ------------------------------------------------------------------ stage hooks

        private void OnMatchStartedV1(LocalMatchHost host)
        {
            _arena.ResetArchers();
            SessionMatchKind kind = Mode == PlayMode.Practice ? SessionMatchKind.Practice
                : Mode == PlayMode.Tutorial ? SessionMatchKind.Tutorial : SessionMatchKind.SharedPhone;
            _life.MatchStarted(kind);
            _ownersBefore = null;
            _pendingTransfer = null;
            _hud.SetPauseAvailable(_life.PauseAllowed);
            host.MatchEnded += result => _life.MatchEnded();
        }

        private void OnStageChangedV1(HostStage stage, PlayerSide side)
        {
            // Ownership before the cut window, for the transfer animation (captured before any bot cut runs).
            if (stage == HostStage.CardAndCut && Host != null) _ownersBefore = OwnershipContours.Snapshot(Host.CloneTerritory());
            if (_life.Overlay != SessionOverlay.None) RefreshOverlay(); // screens were just re-shown underneath
            if (Mode != PlayMode.Tutorial || _tutorial == null) return;
            if (stage == HostStage.CardAndCut)
            {
                bool mine = side == PlayerSide.A && Host != null && Host.Seat(side) == SeatKind.Human;
                if (mine) TutorialEvent(Flow.TutorialEvent.CutWindowOpened);
            }
            else if (stage == HostStage.TerrainAnnounce && _tutorial.Step == TutorialStep.DrawCut)
            {
                TutorialEvent(Flow.TutorialEvent.CutSkipped);
            }
            else if (stage == HostStage.MatchOver)
            {
                TutorialEvent(Flow.TutorialEvent.MatchEnded);
            }
            if (stage == HostStage.Entry || stage == HostStage.Resolution || stage == HostStage.CardAndCut || stage == HostStage.MatchOver ||
                stage == HostStage.TerrainAnnounce)
                RefreshTutorial();
        }

        // ------------------------------------------------------------------ land transfer (40)

        /// <summary>Called on every accepted cut: the before/after snapshots become the transfer animation.</summary>
        private void CaptureTransfer(PlayerSide cutter)
        {
            LocalMatchHost h = Host;
            if (h == null || _snapshot == null) return;
            byte[] after = OwnershipContours.Snapshot(h.CloneTerritory());
            byte[] before = _ownersBefore ?? after;
            int anchor = -1;
            for (int i = 0; i < after.Length && anchor < 0; i++)
                if (before[i] != after[i]) anchor = i;
            if (h.Engine.CommandLog.Count > 0 && h.Engine.CommandLog[h.Engine.CommandLog.Count - 1].Command is SubmitCutCommand cut) anchor = cut.AnchorCellId;
            _pendingTransfer = new LandTransferPlan(before, after, cutter, anchor);
            _ctx.Audio?.Play(Audio.AudioCue.LandTransfer);
        }

        private LandTransferPlan TakeTransferPlan()
        {
            LandTransferPlan p = _pendingTransfer;
            _pendingTransfer = null;
            return p;
        }

        // ------------------------------------------------------------------ data controls (46)

        private List<string> DeleteLocalData()
        {
            var controls = new LocalDataControls();
            if (_ctx.Store is IKeyValueEraser eraser) controls.Register(LocalDataControls.SettingsCategory(_ctx.Store, eraser, _ctx.Settings));
            controls.Register(new LocalDataCategory("records", "data.records", () => DeleteDirectory(MatchFlowPaths.RecordsDirectory)));
            controls.Register(new LocalDataCategory("automation", "data.records", () => DeleteDirectory(MatchFlowPaths.AutomationDirectory)));
            List<string> failed = controls.DeleteAll();
            ApplySettings();
            return failed;
        }

        private static bool DeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Data] Could not delete " + path + ": " + ex.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------ pause, background, connection (47)

        /// <summary>Unity message: the app went to (true) or came back from (false) the background.</summary>
        private void OnApplicationPause(bool paused)
        {
            _life.ApplicationPaused(paused);
            RefreshOverlay();
        }

        private void UpdateV1()
        {
            if (_life.Overlay != _overlayShown) RefreshOverlay();
        }

        private void RefreshOverlay()
        {
            _overlayShown = _life.Overlay;
            if (_sessionOverlay == null) return;
            _sessionOverlay.Present(_life);
            if (_life.Overlay != SessionOverlay.None) _sessionOverlay.Root.SetAsLastSibling();
        }
    }
}
