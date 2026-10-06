using System;
using AstraKingdoms.Client.Online.Protocol;
using AstraKingdoms.Client.Services;
using AstraKingdoms.Client.UI;
using AstraKingdoms.Client.UI.Screens;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using UnityEngine;
using UnityEngine.UI;

namespace AstraKingdoms.Client.Online.UI
{
    /// <summary>
    /// Online lobby (tickets 52, 53): connection status, catalog choice, friend room (create, join by
    /// code, the full rules both players see before confirming, confirm, leave) and the public queue
    /// with the clearly labelled, consent-only bot offer.
    /// </summary>
    public sealed class OnlineLobbyScreen : UiScreen
    {
        private readonly Text _status;
        private readonly Button _catalogButton;
        private readonly InputField _code;
        private readonly RectTransform _menu;
        private readonly RectTransform _roomPanel;
        private readonly RectTransform _queuePanel;
        private readonly Text _roomTitle;
        private readonly Text _roomInfo;
        private readonly Text _roomRules;
        private readonly Button _confirm;
        private readonly Text _queueInfo;
        private readonly Button _playBot;
        private readonly Button _keepWaiting;
        private readonly Text _message;
        private CatalogPreset _catalog = CatalogPreset.Starter;
        private RoomStateMessage _room;
        private QueueStateMessage _queue;
        private double _roomExpiresIn;
        private double _queueWaited;
        private string _roomState = string.Empty;

        public event Action<CatalogPreset> CreateRoom;
        public event Action<string> JoinRoom;
        public event Action ConfirmRoom;
        public event Action LeaveRoom;
        public event Action<CatalogPreset> FindOpponent;
        public event Action AcceptBot;
        public event Action KeepWaiting;
        public event Action CancelQueue;
        public event Action Back;

        public OnlineLobbyScreen(ClientContext ctx, UiFactory ui, Transform parent) : base(ctx, ui, parent, "OnlineLobby", UiTheme.Background)
        {
            RectTransform col = ui.Column(Root, "Column", 14, 30);
            UiFactory.Region(col, 0.2f, 0.03f, 0.8f, 0.97f);
            ui.Label(col, T("online.title"), UiFactory.SizeTitle, TextAnchor.MiddleCenter, UiTheme.Text);
            _status = ui.Label(col, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleCenter, UiTheme.TextMuted);

            _menu = ui.Column(col, "Menu", 12, 0);
            _catalogButton = ui.Button(_menu, string.Empty, CycleCatalog, UiTheme.Panel);
            ui.Button(_menu, T("online.createRoom"), () => CreateRoom?.Invoke(_catalog), UiTheme.ButtonPrimary, UiFactory.SizeLarge);
            RectTransform joinRow = ui.Row(_menu, "JoinRow", 12);
            UiFactory.Prefer(joinRow.gameObject, -1, ui.Scaled(UiFactory.SizeLarge) + 40);
            _code = CodeField(ui, joinRow);
            ui.Button(joinRow, T("online.joinRoom"), () => JoinRoom?.Invoke(_code.text), UiTheme.Button);
            ui.Button(_menu, T("online.findOpponent"), () => FindOpponent?.Invoke(_catalog), UiTheme.Button, UiFactory.SizeLarge);

            _roomPanel = ui.Column(col, "Room", 10, 0);
            _roomTitle = ui.Label(_roomPanel, string.Empty, UiFactory.SizeLarge, TextAnchor.MiddleCenter, UiTheme.ButtonSelected);
            _roomInfo = ui.Label(_roomPanel, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleCenter, UiTheme.Text);
            _roomRules = ui.Label(_roomPanel, string.Empty, UiFactory.SizeSmall, TextAnchor.MiddleLeft, UiTheme.TextMuted);
            _confirm = ui.Button(_roomPanel, T("online.room.confirm"), () => ConfirmRoom?.Invoke(), UiTheme.ButtonPrimary, UiFactory.SizeLarge);
            ui.Button(_roomPanel, T("online.room.leave"), () => LeaveRoom?.Invoke(), UiTheme.ButtonDanger);

            _queuePanel = ui.Column(col, "Queue", 10, 0);
            _queueInfo = ui.Label(_queuePanel, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleCenter, UiTheme.Text);
            _playBot = ui.Button(_queuePanel, T("online.queue.playBot"), () => AcceptBot?.Invoke(), UiTheme.ButtonPrimary, UiFactory.SizeLarge);
            _keepWaiting = ui.Button(_queuePanel, T("online.queue.keepWaiting"), () => KeepWaiting?.Invoke(), UiTheme.Button);
            ui.Button(_queuePanel, T("online.queue.cancel"), () => CancelQueue?.Invoke(), UiTheme.ButtonDanger);

            _message = ui.Label(col, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleCenter, UiTheme.Warning);
            UiFactory.Flexible(col);
            ui.Button(col, T("online.back"), () => Back?.Invoke(), UiTheme.Button);
            RefreshCatalog();
            Layout();
        }

        private InputField CodeField(UiFactory ui, Transform parent)
        {
            RectTransform box = ui.Panel(parent, "Code", UiTheme.Panel);
            Text text = ui.Label(box, string.Empty, UiFactory.SizeLarge, TextAnchor.MiddleCenter, UiTheme.Text, "Text");
            text.raycastTarget = true;
            UiFactory.Stretch(text.rectTransform, 12, 4, 12, 4);
            Text hint = ui.Label(box, T("online.codeHint"), UiFactory.SizeBody, TextAnchor.MiddleCenter, UiTheme.TextMuted, "Placeholder");
            UiFactory.Stretch(hint.rectTransform, 12, 4, 12, 4);
            InputField field = box.gameObject.AddComponent<InputField>();
            field.textComponent = text;
            field.placeholder = hint;
            field.characterLimit = 9; // "ABC-234" with separators
            field.contentType = InputField.ContentType.Alphanumeric;
            return field;
        }

        public void SetConnection(ConnectionStatus status, string failure)
        {
            switch (status)
            {
                case ConnectionStatus.Connecting: _status.text = T("online.status.connecting"); break;
                case ConnectionStatus.Connected: _status.text = T("online.status.connected"); break;
                case ConnectionStatus.Reconnecting: _status.text = T("online.status.reconnecting"); break;
                case ConnectionStatus.Failed: _status.text = TF("online.status.failed", failure ?? string.Empty); break;
                default: _status.text = T("online.status.disconnected"); break;
            }
        }

        public void ShowMessage(string text) => _message.text = text ?? string.Empty;

        public void SetRoom(RoomStateMessage room)
        {
            _room = room;
            if (room != null)
            {
                _roomExpiresIn = room.ExpiresInMs / 1000.0;
                _roomTitle.text = TF("online.room.code", room.Code);
                string state = room.Status == RoomStatus.Open ? T("online.room.waiting")
                    : room.ConfirmedSelf ? T("online.room.confirmed") : T("online.room.full");
                if (room.ConfirmedOpponent) state += "\n" + T("online.room.opponentConfirmed");
                _roomState = state;
                _roomInfo.text = state;
                _roomRules.text = RulesSummary(Ctx, room.Rules);
                _confirm.interactable = room.Status == RoomStatus.Full && !room.ConfirmedSelf;
            }
            Layout();
        }

        public void SetQueue(QueueStateMessage queue)
        {
            _queue = queue;
            if (queue != null) _queueWaited = queue.WaitedMs / 1000.0;
            Layout();
        }

        public void RoomClosed(string reason)
        {
            _room = null;
            _message.text = TF("online.room.closed", CloseReason(reason));
            Layout();
        }

        public override void Tick(float deltaSeconds)
        {
            if (_room != null)
            {
                _roomExpiresIn = Math.Max(0, _roomExpiresIn - deltaSeconds);
                _roomInfo.text = _roomState + "\n" + TF("online.room.expires", UiFactory.Seconds(_roomExpiresIn));
            }
            if (_queue != null)
            {
                _queueWaited += deltaSeconds;
                _queueInfo.text = _queue.Status == QueueStatus.BotOffer
                    ? TF("online.queue.botOffer", _queue.BotLabel ?? string.Empty)
                    : TF("online.queue.waiting", UiFactory.Seconds(_queueWaited));
            }
        }

        private void Layout()
        {
            bool inRoom = _room != null;
            bool queued = _queue != null && (_queue.Status == QueueStatus.Waiting || _queue.Status == QueueStatus.BotOffer);
            _menu.gameObject.SetActive(!inRoom && !queued);
            _roomPanel.gameObject.SetActive(inRoom);
            _queuePanel.gameObject.SetActive(queued);
            bool offer = queued && _queue.Status == QueueStatus.BotOffer;
            _playBot.gameObject.SetActive(offer);
            _keepWaiting.gameObject.SetActive(offer);
        }

        private void CycleCatalog()
        {
            _catalog = _catalog == CatalogPreset.Starter ? CatalogPreset.Full : CatalogPreset.Starter;
            RefreshCatalog();
        }

        private void RefreshCatalog() =>
            UiFactory.ButtonLabel(_catalogButton).text = TF("online.catalog", _catalog == CatalogPreset.Starter ? T("online.catalog.starter") : T("online.catalog.full"));

        /// <summary>Everything both players agree to: catalog, terrain, mode, timers and rules version.</summary>
        public static string RulesSummary(ClientContext ctx, MatchRules rules)
        {
            string catalog = rules.Config.Catalog == CatalogPreset.Starter ? ctx.T("online.catalog.starter") : ctx.T("online.catalog.full");
            string terrain = rules.Config.TerrainTemplateId == TerrainTemplates.FullId ? ctx.T("online.terrain.full") : ctx.T("online.terrain.plain");
            return ctx.TF("online.room.rules", catalog, terrain, UiFactory.Seconds(rules.Timings.ChoiceMs / 1000.0),
                UiFactory.Seconds(rules.Timings.CutMs / 1000.0), rules.Config.RulesVersion);
        }

        private string CloseReason(string reason)
        {
            switch (reason)
            {
                case RoomCloseReasons.Expired: return T("online.closed.expired");
                case RoomCloseReasons.HostLeft: return T("online.closed.hostLeft");
                case RoomCloseReasons.HostDisconnected: return T("online.closed.hostDisconnected");
                case RoomCloseReasons.Left: return T("online.closed.left");
                case RoomCloseReasons.ServerShutdown: return T("online.closed.serverShutdown");
                default: return reason;
            }
        }
    }
}
