using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AstraKingdoms.Client.Arena;
using AstraKingdoms.Client.Combat;
using AstraKingdoms.Client.Match;
using AstraKingdoms.Client.Online.Protocol;
using AstraKingdoms.Client.Services;
using AstraKingdoms.Client.UI;
using AstraKingdoms.Client.UI.Screens;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using AstraKingdoms.Rules.Match;
using UnityEngine;
using UnityEngine.UI;

namespace AstraKingdoms.Client.Online.UI
{
    /// <summary>
    /// Online play in the Unity client. Owns the <see cref="OnlineClient"/>, pumps its messages on the
    /// main thread and drives screens from the <see cref="IMatchSession"/> surface: the same
    /// view-driven screens the shared-phone flow uses (HUD, announcement, selection, resolution,
    /// result) plus online-only ones (lobby, any-catalog loadout, card window, waiting notices, status
    /// strip). The client never decides an outcome; it shows the server's view of this player.
    /// </summary>
    public sealed class OnlineFlow : MonoBehaviour
    {
        private ClientContext _ctx;
        private HomeScreen _home;
        private OnlineClient _client;
        private OnlineMatchSession _session;
        private Canvas _canvas;
        private UiFactory _ui;
        private readonly List<UiScreen> _screens = new List<UiScreen>();
        private OnlineLobbyScreen _lobby;
        private OnlineLoadoutScreen _loadout;
        private OnlineCutScreen _cut;
        private OnlineWaitScreen _wait;
        private OnlineStatusBar _statusBar;
        private HudView _hud;
        private AnnouncementScreen _announce;
        private SelectionScreen _selection;
        private ResolutionScreen _resolution;
        private ResultScreen _result;
        private ArenaView _arena;
        private IReadOnlyList<string> _lastLines = Array.Empty<string>();
        private string _landMessage;

        public void Open(HomeScreen home, ClientContext ctx)
        {
            _home = home;
            if (_ctx != ctx || _canvas == null)
            {
                _ctx = ctx;
                BuildUi();
            }
            home.Hide();
            ShowLobby();
            if (_client == null || _client.Status == ConnectionStatus.Failed || _client.Status == ConnectionStatus.Disconnected) Connect();
        }

        // ------------------------------------------------------------------ UI

        private void BuildUi()
        {
            if (_canvas != null) Destroy(_canvas.gameObject);
            _screens.Clear();
            var canvasGo = new GameObject("OnlineUI", typeof(RectTransform));
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 20; // above the main flow's canvas
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            canvasGo.AddComponent<GraphicRaycaster>();

            _ui = new UiFactory(UiFactory.BuiltinFont(), _ctx.Settings.TextScale);
            Transform root = canvasGo.transform;
            _hud = Add(new HudView(_ctx, _ui, root));
            _hud.SetPauseAvailable(false); // never pause an online opponent's clock
            _announce = Add(new AnnouncementScreen(_ctx, _ui, root));
            _selection = Add(new SelectionScreen(_ctx, _ui, root));
            _resolution = Add(new ResolutionScreen(_ctx, _ui, root));
            _loadout = Add(new OnlineLoadoutScreen(_ctx, _ui, root));
            _cut = Add(new OnlineCutScreen(_ctx, _ui, root));
            _wait = Add(new OnlineWaitScreen(_ctx, _ui, root));
            _result = Add(new ResultScreen(_ctx, _ui, root));
            _lobby = Add(new OnlineLobbyScreen(_ctx, _ui, root));
            _statusBar = Add(new OnlineStatusBar(_ctx, _ui, root));

            _lobby.CreateRoom += c => _client?.CreateRoom(c);
            _lobby.JoinRoom += code => _client?.JoinRoom(code);
            _lobby.ConfirmRoom += () => _client?.ConfirmRoom();
            _lobby.LeaveRoom += () => _client?.LeaveRoom();
            _lobby.FindOpponent += c => _client?.JoinQueue(c);
            _lobby.AcceptBot += () => _client?.AcceptBotMatch();
            _lobby.KeepWaiting += () => _client?.KeepWaiting();
            _lobby.CancelQueue += () => _client?.CancelQueue();
            _lobby.Back += Close;
            _loadout.Confirmed += weapons => _session?.SubmitLoadout(_session.LocalSide, weapons, 0);
            _selection.PreviewChanged += OnPreviewChanged;
            _selection.LockRequested += (side, input) =>
            {
                _arena?.ClearPreview();
                _session?.SubmitLock(side, input);
            };
            _cut.CutRequested += plan => _session?.SubmitCut(_session.LocalSide, plan.Card, plan.Pose, plan.AnchorCellId, CutMode.Auto, null);
            _result.Rematch += ShowLobby; // a rematch is a new room or queue entry
            _result.Home += Close;
        }

        private T Add<T>(T screen) where T : UiScreen
        {
            _screens.Add(screen);
            return screen;
        }

        private void HideAll()
        {
            foreach (UiScreen s in _screens)
                if (s != _statusBar) s.Hide();
        }

        private void ShowLobby()
        {
            HideAll();
            _lobby.SetRoom(_client?.Room);
            _lobby.SetQueue(_client?.Queue);
            _lobby.Show();
            _statusBar.Show();
            RefreshStatus();
        }

        private void Close()
        {
            if (_client?.Room != null) _client.LeaveRoom();
            if (_client?.Queue != null) _client.CancelQueue();
            HideAll();
            _statusBar.Hide();
            _home?.Show();
        }

        // ------------------------------------------------------------------ connection

        private async void Connect()
        {
            Func<CancellationToken, Task<string>> tokens = OnlineEntryPoint.TokenSource(_ctx);
            if (tokens == null)
            {
                _lobby.ShowMessage(_ctx.T("online.signInUnavailable"));
                return;
            }
            _client?.Dispose();
            _client = new OnlineClient(new OnlineClientOptions
            {
                ServerUri = OnlineEntryPoint.ServerUri(_ctx),
                ClientVersion = Application.version,
                TokenProvider = tokens,
            });
            // Connection status is polled in Update: StatusChanged fires on the network thread.
            _client.RoomChanged += r => _lobby.SetRoom(r);
            _client.RoomClosed += c => _lobby.RoomClosed(c.Reason);
            _client.QueueChanged += q => _lobby.SetQueue(q);
            _client.MatchStarted += OnMatchStarted;
            _client.MatchEnded += OnMatchEnded;
            _client.ErrorReceived += e => _lobby.ShowMessage(_ctx.TF("online.error", e.Message ?? e.Code));
            try
            {
                await _client.ConnectAsync();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Online] Connect failed: " + e.Message);
            }
        }

        private void RefreshStatus()
        {
            string connection;
            switch (_client?.Status ?? ConnectionStatus.Disconnected)
            {
                case ConnectionStatus.Connecting: connection = _ctx.T("online.status.connecting"); break;
                case ConnectionStatus.Connected: connection = _ctx.T("online.status.connected"); break;
                case ConnectionStatus.Reconnecting: connection = _ctx.T("online.status.reconnecting"); break;
                case ConnectionStatus.Failed: connection = _ctx.TF("online.status.failed", _client.Connection.LastFailure ?? string.Empty); break;
                default: connection = _ctx.T("online.status.disconnected"); break;
            }
            string opponent = null;
            if (_session != null && !_session.IsOver)
                opponent = _session.OpponentIsBot ? _ctx.TF("online.opponent.bot", _session.OpponentLabel) : _ctx.TF("online.opponent", _session.OpponentLabel);
            _statusBar.Set(connection, opponent, _session == null || _session.OpponentConnected);
            _lobby.SetConnection(_client?.Status ?? ConnectionStatus.Disconnected, _client?.Connection.LastFailure);
        }

        // ------------------------------------------------------------------ match

        private void OnMatchStarted(OnlineMatchSession session)
        {
            _session = session;
            _lastLines = Array.Empty<string>();
            _landMessage = null;
            if (_arena == null) _arena = FindFirstObjectByType<ArenaView>();
            string opponent = session.OpponentLabel ?? _ctx.T("online.opponentDefault");
            _ctx.PlayerName = side => side == session.LocalSide ? _ctx.T("player.you") : opponent;
            session.StageChanged += OnStageChanged;
            session.ViewChanged += OnViewChanged;
            session.VolleyRevealed += OnVolleyRevealed;
            session.CutApplied += (side, cells) => _landMessage = _ctx.TF("land.applied", _ctx.PlayerName(side), cells);
            session.CommandRejected += OnRejected;
            HideAll();
            _wait.Open(OnlineLobbyScreen.RulesSummary(_ctx, session.Rules));
        }

        private void OnStageChanged(HostStage stage)
        {
            OnlineMatchSession s = _session;
            if (s == null) return;
            PublicSnapshot snap = s.Snapshot();
            _hud.Refresh(snap);
            HideAll();
            PlayerView view = s.View;
            switch (stage)
            {
                case HostStage.LoadoutEntry:
                    _loadout.Open(s.Config.Catalog);
                    break;
                case HostStage.None:
                    _wait.Open(_ctx.T("online.wait.loadout"));
                    break;
                case HostStage.TerrainAnnounce:
                    _hud.Show();
                    _announce.Open(snap, _landMessage);
                    _landMessage = null;
                    break;
                case HostStage.Entry:
                    _hud.Show();
                    if (view != null && view.OwnLock == null) _selection.Open(view, _lastLines);
                    else _wait.Open(_ctx.T("online.wait.locked"));
                    break;
                case HostStage.Resolution:
                    _hud.Show();
                    _resolution.Open();
                    _resolution.ShowExplanation(_lastLines);
                    break;
                case HostStage.CardAndCut:
                    _hud.Show();
                    if (s.StageSide == s.LocalSide && view != null) _cut.Open(view);
                    else _wait.Open(_ctx.T("online.wait.cut"));
                    break;
                case HostStage.MatchOver:
                    ShowEnd();
                    break;
            }
        }

        private void OnViewChanged(PlayerView view)
        {
            _hud.Refresh(_session.Snapshot());
            // The local lock was accepted: leave the entry form for the waiting notice.
            if (_session.Stage == HostStage.Entry && view.OwnLock != null && _selection.Visible)
            {
                _selection.Hide();
                _wait.Open(_ctx.T("online.wait.locked"));
            }
        }

        private void OnVolleyRevealed(RevealedVolley v)
        {
            PlayerSide me = _session.LocalSide;
            RevealedChoice mine = v[me];
            RevealedChoice theirs = v[Board.Opponent(me)];
            var lines = new List<string>
            {
                _ctx.TF("online.volley.line", v.Volley, Hp(mine.HpBeforeUnits), Hp(mine.HpAfterUnits), Hp(theirs.HpBeforeUnits), Hp(theirs.HpAfterUnits)),
            };
            if (theirs.Concealed) lines.Add(_ctx.T("online.volley.veiled"));
            if (mine.TimedOut) lines.Add(_ctx.TF("online.volley.timeout", _ctx.PlayerName(me)));
            if (theirs.TimedOut) lines.Add(_ctx.TF("online.volley.timeout", _ctx.PlayerName(Board.Opponent(me))));
            _lastLines = lines;
        }

        private static string Hp(int units) => (units / 100.0).ToString("0.00", CultureInfo.InvariantCulture);

        private void OnRejected(string code)
        {
            if (_selection.Visible) _selection.ShowRejection(code);
            else if (_loadout.Visible) _loadout.ShowRejection(code);
            else if (_cut.Visible) _cut.ShowRejection(code);
            Debug.LogWarning("[Online] Command rejected: " + code);
        }

        private void OnMatchEnded(MatchEndMessage end)
        {
            if (_session == null || end.MatchId != _session.MatchId) return;
            ShowEnd();
        }

        private void ShowEnd()
        {
            OnlineMatchSession s = _session;
            HideAll();
            if (s.Outcome == MatchOutcomes.TechnicalVoid)
            {
                _wait.Open(_ctx.T("online.end.technicalVoid"), _ctx.T("online.back"), ShowLobby);
                return;
            }
            if (s.Result == null)
            {
                _wait.Open(_ctx.T("online.wait.result"));
                return;
            }
            _result.Open(s.Result, s.CloneTerritory());
            if (s.RewardXp > 0 || s.RewardCoins > 0) _lobby.ShowMessage(_ctx.TF("online.end.reward", s.RewardXp, s.RewardCoins));
        }

        private void OnPreviewChanged(PlayerSide side, int weapon, int pitch, int yaw, int power)
        {
            if (_arena == null || _session == null || !_session.CanView(side)) return;
            PlayerView view = _session.ViewFor(side);
            var arcs = TrajectoryPreview.Compute(side, weapon, pitch, yaw, power, view.Self.BaselineOffsetRightRaw, view.Foe.BaselineOffsetRightRaw);
            _arena.ShowPreview(arcs, side == PlayerSide.A ? UiTheme.PlayerA : UiTheme.PlayerB);
        }

        // ------------------------------------------------------------------ frame

        private void Update()
        {
            if (_client == null) return;
            _client.Pump(); // all client events run here, on the main thread
            float dt = Time.unscaledDeltaTime;
            foreach (UiScreen screen in _screens)
                if (screen.Visible) screen.Tick(dt);
            RefreshStatus();
            OnlineMatchSession s = _session;
            if (s == null || s.IsOver) return;
            s.Tick(dt);
            double remaining = s.StageSecondsRemaining;
            if (_selection.Visible) _selection.SetRemaining(remaining);
            if (_cut.Visible) _cut.SetRemaining(remaining);
            _hud.SetRemaining(remaining, s.Stage != HostStage.LoadoutEntry && s.Stage != HostStage.None && s.Stage != HostStage.MatchOver);
        }

        private void OnDestroy()
        {
            _client?.Dispose();
        }
    }
}
