using System;
using AstraKingdoms.Client.Match;
using AstraKingdoms.Rules.Core;
using UnityEngine;
using UnityEngine.UI;

namespace AstraKingdoms.Client.UI.Screens
{
    /// <summary>
    /// The opaque shared-phone handover screen. It covers the whole display (3D arena included) and
    /// shows nothing from the previous player's controls, logs or previews: only whose turn is next,
    /// the running clock and a "tap when ready" button.
    /// </summary>
    public sealed class PrivacyScreen : UiScreen
    {
        private readonly Text _title;
        private readonly Text _timer;
        private readonly Button _ready;
        private HostStage _stage;

        public event Action ReadyTapped;

        public PrivacyScreen(Services.ClientContext ctx, UiFactory ui, Transform parent) : base(ctx, ui, parent, "PrivacyScreen", UiTheme.Opaque)
        {
            RectTransform col = ui.Column(Root, "Column", 30, 40);
            UiFactory.Region(col, 0.15f, 0.1f, 0.85f, 0.9f);
            UiFactory.Flexible(col);
            _title = ui.Label(col, string.Empty, UiFactory.SizeTitle, TextAnchor.MiddleCenter, UiTheme.Text);
            ui.Label(col, T("privacy.hint"), UiFactory.SizeBody, TextAnchor.MiddleCenter, UiTheme.TextMuted);
            _timer = ui.Label(col, string.Empty, UiFactory.SizeLarge, TextAnchor.MiddleCenter, UiTheme.Warning);
            _ready = ui.Button(col, string.Empty, () => ReadyTapped?.Invoke(), UiTheme.ButtonPrimary, UiFactory.SizeLarge);
            UiFactory.Prefer(_ready.gameObject, -1, ui.Scaled(UiFactory.SizeLarge) + 90);
            UiFactory.Flexible(col);
        }

        public void Open(HostStage stage, PlayerSide side)
        {
            _stage = stage;
            string name = Ctx.PlayerName(side);
            _title.text = TF("privacy.passTo", name);
            UiFactory.ButtonLabel(_ready).text = TF("privacy.ready", name);
            _timer.text = string.Empty;
            Show();
        }

        public void SetRemaining(double seconds)
        {
            if (_stage == HostStage.LoadoutReady)
            {
                _timer.text = string.Empty; // pre-match setup is untimed
                return;
            }
            string s = UiFactory.Seconds(seconds);
            _timer.text = _stage == HostStage.Handover ? TF("privacy.handoverTimer", s) : TF("privacy.timer", s);
        }
    }
}
