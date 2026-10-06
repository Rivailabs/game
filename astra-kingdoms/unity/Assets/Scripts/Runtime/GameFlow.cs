using System;
using System.Collections.Generic;
using System.IO;
using AstraKingdoms.Client.Arena;
using AstraKingdoms.Client.Combat;
using AstraKingdoms.Client.Match;
using AstraKingdoms.Client.MatchFlow;
using AstraKingdoms.Client.Services;
using AstraKingdoms.Client.UI;
using AstraKingdoms.Client.UI.Screens;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AstraKingdoms.Client
{
    /// <summary>How the current match was started (for rematch and seat names).</summary>
    public enum PlayMode
    {
        SharedPhone = 0,
        Practice = 1,
        Automation = 2,
    }

    /// <summary>
    /// Coordinates screens with the match host: privacy handovers, private entry screens, the HUD,
    /// resolution playback, the cut screen and results. Screens only ever receive the entitled
    /// player's view or the public snapshot.
    /// </summary>
    public sealed class GameFlow : MonoBehaviour
    {
        private ClientContext _ctx;
        private ArenaView _arena;
        private MatchController _controller;
        private bool _devTools;

        private Canvas _canvas;
        private UiFactory _ui;
        private HomeScreen _home;
        private SettingsScreen _settings;
        private LoadoutScreen _loadout;
        private PrivacyScreen _privacy;
        private AnnouncementScreen _announce;
        private SelectionScreen _selection;
        private ResolutionScreen _resolution;
        private LandScreen _land;
        private ResultScreen _result;
        private HudView _hud;
        private ReplayScreen _replay;
        private readonly List<UiScreen> _screens = new List<UiScreen>();

        private ExplanationBuilder _explain;
        private PublicSnapshot _snapshot;
        private IReadOnlyList<string> _lastLines = Array.Empty<string>();
        private string _landMessage;
        private bool _cutAppliedThisRound;
        private HostStage _previousStage;

        public PlayMode Mode { get; private set; }
        public BotDifficulty Difficulty { get; private set; } = BotDifficulty.Normal;
        public LocalMatchHost Host => _controller.Host;
        public HostTimings AutomationTimings { get; set; }
        public Canvas Canvas => _canvas;
        public ClientContext Context => _ctx;

        /// <summary>Raised when a match ends (automation listens).</summary>
        public event Action<LocalMatchHost, MatchResult> MatchFinished;

        public void Init(ClientContext ctx, ArenaView arena, MatchController controller, bool developmentTools)
        {
            _ctx = ctx;
            _arena = arena;
            _controller = controller;
            _devTools = developmentTools;
            _controller.MatchStarted += OnMatchStarted;
            _arena.FlightCompleted += OnFlightCompleted;
            EnsureEventSystem();
            BuildUi();
        }

        // ------------------------------------------------------------------ UI construction

        private void BuildUi()
        {
            if (_canvas != null) Destroy(_canvas.gameObject);
            _screens.Clear();
            var canvasGo = new GameObject("UI", typeof(RectTransform));
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 10;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            canvasGo.AddComponent<GraphicRaycaster>();

            _ui = new UiFactory(UiFactory.BuiltinFont(), _ctx.Settings.TextScale);
            Transform root = canvasGo.transform;
            _hud = Add(new HudView(_ctx, _ui, root));
            _announce = Add(new AnnouncementScreen(_ctx, _ui, root));
            _selection = Add(new SelectionScreen(_ctx, _ui, root));
            _resolution = Add(new ResolutionScreen(_ctx, _ui, root));
            _land = Add(new LandScreen(_ctx, _ui, root));
            _loadout = Add(new LoadoutScreen(_ctx, _ui, root));
            _result = Add(new ResultScreen(_ctx, _ui, root));
            _home = Add(new HomeScreen(_ctx, _ui, root, _devTools));
            _settings = Add(new SettingsScreen(_ctx, _ui, root));
            _replay = Add(new ReplayScreen(_ctx, _ui, root, _arena));
            _privacy = Add(new PrivacyScreen(_ctx, _ui, root)); // last: always drawn on top

            _home.PlaySharedPhone += StartSharedPhone;
            _home.PlayPractice += StartPractice;
            _home.OpenSettings += ShowSettings;
            _home.OpenReplay += ShowReplay;
            _settings.Done += rebuild =>
            {
                ApplySettings();
                if (rebuild) BuildUi();
                ShowHome();
            };
            _loadout.Confirmed += (side, weapons) => Host?.SubmitLoadout(side, weapons);
            _privacy.ReadyTapped += () => Host?.ConfirmReady();
            _selection.PreviewChanged += OnPreviewChanged;
            _selection.LockRequested += (side, input) =>
            {
                _arena.ClearPreview();
                Host?.SubmitLock(side, input);
            };
            _land.CutRequested += (card, pose, anchor, mode, vertices) =>
            {
                LocalMatchHost h = Host;
                if (h != null && h.StageSide.HasValue) h.SubmitCut(h.StageSide.Value, card, pose, anchor, mode, vertices);
            };
            _result.Rematch += Rematch;
            _result.Home += GoHome;
            _hud.PauseToggled += TogglePause;
            _replay.Closed += ShowHome;
        }

        private T Add<T>(T screen) where T : UiScreen
        {
            _screens.Add(screen);
            return screen;
        }

        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
            go.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#else
            go.AddComponent<StandaloneInputModule>();
#endif
        }

        public void ApplySettings()
        {
            _ctx.Audio?.SetVolumes(_ctx.Settings.MusicVolume, _ctx.Settings.EffectsVolume);
            Haptics.Enabled = _ctx.Settings.Haptics;
            _arena.SetReducedShake(_ctx.Settings.ReducedCameraShake);
            _ctx.Loc.Language = _ctx.Settings.Language;
        }

        private void HideAll()
        {
            foreach (UiScreen s in _screens) s.Hide();
        }

        // ------------------------------------------------------------------ navigation

        public void ShowStart()
        {
            if (!_ctx.Settings.FirstRunComplete) ShowSettings();
            else ShowHome();
        }

        public void ShowHome()
        {
            _controller.EndMatch();
            _arena.Stop();
            _arena.ClearPreview();
            HideAll();
            _home.Show();
        }

        private void ShowSettings()
        {
            HideAll();
            _settings.Show();
        }

        private void ShowReplay()
        {
            HideAll();
            _replay.Open(null);
        }

        public void OpenReplay(string path)
        {
            HideAll();
            _replay.Open(path);
        }

        private void GoHome() => ShowHome();

        public void StartSharedPhone()
        {
            Mode = PlayMode.SharedPhone;
            _ctx.PlayerName = side => _ctx.T(side == PlayerSide.A ? "player.one" : "player.two");
            _controller.ClockSpeed = 1f;
            _arena.Speed = 1f;
            _controller.StartMatch(MatchConfig.Pilot(MatchMode.SharedPhone), SeatKind.Human, SeatKind.Human, Difficulty);
        }

        public void StartPractice(BotDifficulty difficulty)
        {
            Mode = PlayMode.Practice;
            Difficulty = difficulty;
            _ctx.PlayerName = side => side == PlayerSide.A ? _ctx.T("player.you") : _ctx.TF("player.bot", _ctx.DifficultyName(Difficulty));
            _controller.ClockSpeed = 1f;
            _arena.Speed = 1f;
            _controller.StartMatch(MatchConfig.Pilot(MatchMode.Practice), SeatKind.Human, SeatKind.Bot, difficulty);
        }

        /// <summary>Both seats are bots; used by the development automation interface.</summary>
        public LocalMatchHost StartAutomationMatch(byte[] seed, string matchId, Func<string> requestIds, float clockSpeed, BotDifficulty difficulty)
        {
            Mode = PlayMode.Automation;
            Difficulty = difficulty;
            _ctx.PlayerName = side => _ctx.TF("player.botSeat", side.ToString());
            _controller.ClockSpeed = clockSpeed;
            _arena.Speed = clockSpeed;
            HostTimings timings = AutomationTimings ?? new HostTimings { BotCutDelaySeconds = 0.5 };
            return _controller.StartMatch(MatchConfig.Pilot(MatchMode.SharedPhone), SeatKind.Bot, SeatKind.Bot, difficulty, timings, seed, matchId, requestIds);
        }

        /// <summary>Rematch: a brand-new match (new seed and engine); every screen reopens from clean defaults.</summary>
        public void Rematch()
        {
            _lastLines = Array.Empty<string>();
            _landMessage = null;
            _arena.Stop();
            _arena.ClearPreview();
            if (Mode == PlayMode.Practice) StartPractice(Difficulty);
            else StartSharedPhone();
        }

        private void TogglePause()
        {
            LocalMatchHost h = Host;
            if (h == null) return;
            if (h.SetPaused(!h.Paused))
            {
                _arena.Speed = h.Paused ? 0f : _controller.ClockSpeed;
                RefreshSnapshot();
            }
        }

        // ------------------------------------------------------------------ host events

        private void OnMatchStarted(LocalMatchHost host)
        {
            HideAll();
            _lastLines = Array.Empty<string>();
            _landMessage = null;
            _explain = new ExplanationBuilder(_ctx.Loc, _ctx.PlayerName);
            _arena.ResetPoses(0, 0);
            _hud.SetPauseAvailable(host.PauseAllowed);
            host.StageChanged += OnStageChanged;
            host.VolleyResolved += OnVolleyResolved;
            host.CutApplied += OnCutApplied;
            host.MatchEnded += result => OnMatchEnded(host, result);
            host.CommandRejected += OnRejected;
        }

        private void OnStageChanged(HostStage stage)
        {
            LocalMatchHost h = Host;
            if (h == null) return;
            if (_previousStage == HostStage.CardAndCut && stage != HostStage.CardAndCut && !_cutAppliedThisRound && _landMessage == null)
                _landMessage = _ctx.T("land.timeout");
            if (_previousStage == HostStage.Resolution && stage != HostStage.Resolution)
            {
                _arena.Stop();
                _hud.ReleaseHp();
            }
            _previousStage = stage;
            RefreshSnapshot();
            HideAll();
            PlayerSide side = h.StageSide ?? PlayerSide.A;
            switch (stage)
            {
                case HostStage.LoadoutReady:
                case HostStage.EntryReady:
                case HostStage.Handover:
                    _arena.ClearPreview();
                    _privacy.Open(stage, side);
                    break;
                case HostStage.LoadoutEntry:
                    _loadout.Open(side);
                    break;
                case HostStage.TerrainAnnounce:
                    _hud.Show();
                    _announce.Open(_snapshot, _landMessage);
                    _landMessage = null;
                    _cutAppliedThisRound = false;
                    break;
                case HostStage.Entry:
                    if (h.Seat(side) == SeatKind.Human)
                    {
                        _hud.Show();
                        _selection.Open(h.ViewFor(side), _lastLines);
                    }
                    break;
                case HostStage.Resolution:
                    _hud.Show();
                    _resolution.Open();
                    break;
                case HostStage.CardAndCut:
                    _hud.Show();
                    bool interactive = h.Seat(side) == SeatKind.Human && h.CanView(side);
                    _land.Open(h, side, interactive, _snapshot, _snapshot.FrontierCellId);
                    break;
                case HostStage.MatchOver:
                    _result.Open(h.Engine.Result, h.CloneTerritory());
                    break;
            }
        }

        private void OnVolleyResolved(ResolvedVolley v)
        {
            VolleyExplanation e = v.Result.Explanation;
            _lastLines = _explain.Build(e, v.ConcealA, v.ConcealB);
            _hud.HoldHp(e.A.HpBeforeUnits, e.B.HpBeforeUnits);
            _arena.Play(v, (float)(Host?.Timings.ResolutionSeconds ?? 2.5));
        }

        private void OnFlightCompleted()
        {
            _hud.ReleaseHp();
            if (_resolution.Visible) _resolution.ShowExplanation(_lastLines);
        }

        private void OnCutApplied(PlayerSide side, int cells)
        {
            _cutAppliedThisRound = true;
            _landMessage = _ctx.TF("land.applied", _ctx.PlayerName(side), cells);
            _ctx.Audio?.Play(Sfx.Land);
        }

        private void OnMatchEnded(LocalMatchHost host, MatchResult result)
        {
            _ctx.Audio?.Play(Sfx.Victory);
            if (_devTools) SaveRecord(host);
            MatchFinished?.Invoke(host, result);
        }

        private void OnRejected(CommandReceipt r)
        {
            if (_selection.Visible) _selection.ShowRejection(r.RejectCode);
            else if (_loadout.Visible) _loadout.ShowRejection(r.RejectCode);
            else if (_land.Visible) _land.ShowRejection(r.RejectCode);
            Debug.LogWarning("[Match] Command rejected: " + r);
        }

        private void OnPreviewChanged(PlayerSide side, int weapon, int pitch, int yaw, int power)
        {
            LocalMatchHost h = Host;
            if (h == null || !h.CanView(side)) return;
            PlayerView view = h.ViewFor(side);
            var arcs = TrajectoryPreview.Compute(side, weapon, pitch, yaw, power, view.Self.BaselineOffsetRightRaw, view.Foe.BaselineOffsetRightRaw);
            _arena.ShowPreview(arcs, side == PlayerSide.A ? UiTheme.PlayerA : UiTheme.PlayerB);
        }

        private void RefreshSnapshot()
        {
            LocalMatchHost h = Host;
            if (h == null) return;
            _snapshot = h.Snapshot();
            _hud.Refresh(_snapshot);
        }

        /// <summary>Development builds keep the finished match's replay record (it contains the now-disclosed seed).</summary>
        public static string SaveRecord(LocalMatchHost host, string directory = null)
        {
            try
            {
                string dir = directory ?? MatchFlowPaths.RecordsDirectory;
                Directory.CreateDirectory(dir);
                string json = host.ToRecord().ToJson();
                string path = Path.Combine(dir, host.Engine.MatchId + ".json");
                File.WriteAllText(path, json);
                File.WriteAllText(Path.Combine(dir, "latest-record.json"), json);
                return path;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Match] Could not save the match record: " + ex.Message);
                return null;
            }
        }

        // ------------------------------------------------------------------ frame

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            foreach (UiScreen s in _screens)
                if (s.Visible) s.Tick(dt);
            LocalMatchHost h = Host;
            if (h == null) return;
            double remaining = h.StageSecondsRemaining;
            switch (h.Stage)
            {
                case HostStage.EntryReady:
                case HostStage.Handover:
                    _privacy.SetRemaining(remaining);
                    break;
                case HostStage.Entry:
                    _selection.SetRemaining(remaining);
                    break;
                case HostStage.CardAndCut:
                    _land.SetRemaining(remaining);
                    break;
            }
            _hud.SetRemaining(remaining, h.Stage != HostStage.LoadoutEntry && h.Stage != HostStage.LoadoutReady && h.Stage != HostStage.MatchOver);
        }
    }
}
