using System;
using System.Collections.Generic;
using AstraKingdoms.Client.Match;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using UnityEngine;
using UnityEngine.UI;

namespace AstraKingdoms.Client.UI.Screens
{
    /// <summary>
    /// Duel HUD: round and volley, both HP values, visible statuses, land, time remaining and each
    /// player's ready flag (never their choice). During a replay the HP shown is held at the
    /// pre-volley values until the flight animation ends, so the display agrees with the arrows.
    /// </summary>
    public sealed class HudView : UiScreen
    {
        private readonly Text _centre;
        private readonly Text[] _name = new Text[2];
        private readonly Text[] _hp = new Text[2];
        private readonly Text[] _status = new Text[2];
        private readonly Button _pause;
        private readonly Text _pausedBanner;
        private int[] _heldHp;
        private string _roundText = string.Empty;
        private PublicSnapshot _last;

        public event Action PauseToggled;

        public HudView(Services.ClientContext ctx, UiFactory ui, Transform parent)
            : base(ctx, ui, parent, "Hud", new Color(0, 0, 0, 0), false)
        {
            RectTransform bar = ui.Panel(Root, "TopBar", UiTheme.Panel, false);
            UiFactory.Region(bar, 0f, 0.88f, 1f, 1f);
            for (int i = 0; i < 2; i++)
            {
                RectTransform box = UiFactory.Rect(i == 0 ? "PlayerA" : "PlayerB", bar);
                UiFactory.Region(box, i == 0 ? 0f : 0.68f, 0f, i == 0 ? 0.32f : 1f, 1f);
                TextAnchor align = i == 0 ? TextAnchor.UpperLeft : TextAnchor.UpperRight;
                _name[i] = ui.Label(box, string.Empty, UiFactory.SizeBody, align, i == 0 ? UiTheme.PlayerA : UiTheme.PlayerB);
                UiFactory.Region(_name[i].rectTransform, 0.03f, 0.55f, 0.97f, 1f);
                _hp[i] = ui.Label(box, string.Empty, UiFactory.SizeBody, align, UiTheme.Text);
                UiFactory.Region(_hp[i].rectTransform, 0.03f, 0.2f, 0.97f, 0.62f);
                _status[i] = ui.Label(box, string.Empty, UiFactory.SizeSmall, align, UiTheme.TextMuted);
                UiFactory.Region(_status[i].rectTransform, 0.03f, -0.25f, 0.97f, 0.25f);
            }
            _centre = ui.Label(bar, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleCenter, UiTheme.Text);
            UiFactory.Region(_centre.rectTransform, 0.32f, 0f, 0.6f, 1f);
            _pause = ui.Button(bar, T("hud.pause"), () => PauseToggled?.Invoke(), UiTheme.Button, UiFactory.SizeSmall);
            UiFactory.Region(_pause.GetComponent<RectTransform>(), 0.6f, 0.15f, 0.67f, 0.85f);
            _pausedBanner = ui.Label(Root, T("hud.paused"), UiFactory.SizeTitle, TextAnchor.MiddleCenter, UiTheme.Warning);
            UiFactory.Region(_pausedBanner.rectTransform, 0.3f, 0.45f, 0.7f, 0.6f);
            _pausedBanner.gameObject.SetActive(false);
        }

        public void SetPauseAvailable(bool available) => _pause.gameObject.SetActive(available);

        /// <summary>Hold HP at these values (pre-volley) until <see cref="ReleaseHp"/>.</summary>
        public void HoldHp(int hpA, int hpB) => _heldHp = new[] { hpA, hpB };

        public void ReleaseHp()
        {
            _heldHp = null;
            if (_last != null) Refresh(_last);
        }

        /// <summary>Per-frame timer update (cheap; the full refresh happens on match events).</summary>
        public void SetRemaining(double seconds, bool timed) =>
            _centre.text = timed ? _roundText + "\n" + TF("hud.time", UiFactory.Seconds(seconds)) : _roundText;

        public void Refresh(PublicSnapshot s)
        {
            if (s == null) return;
            _last = s;
            _roundText = TF("hud.round", Math.Max(1, s.Round), Math.Max(1, s.Volley));
            SetRemaining(s.StageSecondsRemaining, s.Stage != HostStage.LoadoutEntry && s.Stage != HostStage.MatchOver);
            for (int i = 0; i < 2; i++)
            {
                var side = (PlayerSide)i;
                int hp = _heldHp != null ? _heldHp[i] : s.Hp(side);
                bool locked = side == PlayerSide.A ? s.LockedA : s.LockedB;
                string ready = s.Phase == MatchPhase.Selection ? "  [" + T(locked ? "hud.ready" : "hud.waiting") + "]" : string.Empty;
                _name[i].text = Ctx.PlayerName(side) + ready;
                _hp[i].text = TF("hud.hp", Hp.Format(hp)) + "   " + TF("hud.cells", s.Cells(side));
                _status[i].text = Statuses(s.Status(side));
            }
            _pausedBanner.gameObject.SetActive(s.Paused);
            UiFactory.ButtonLabel(_pause).text = T(s.Paused ? "hud.resume" : "hud.pause");
        }

        private string Statuses(PlayerStatus st)
        {
            if (st == null) return string.Empty;
            var parts = new List<string>();
            if (st.BurnDue) parts.Add(T("status.burn"));
            if (st.ShockDue) parts.Add(T("status.shock"));
            if (st.NetDue) parts.Add(T("status.net"));
            if (st.QuakeDue) parts.Add(T("status.quake"));
            if (st.IronWallActive) parts.Add(T("status.ironWall"));
            return parts.Count == 0 ? T("hud.statusNone") : string.Join(" · ", parts);
        }
    }
}
