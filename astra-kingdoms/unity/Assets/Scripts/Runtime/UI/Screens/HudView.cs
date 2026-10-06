using System;
using AstraKingdoms.Client.Match;
using AstraKingdoms.Client.Presentation;
using AstraKingdoms.Rules.Core;
using UnityEngine;
using UnityEngine.UI;

namespace AstraKingdoms.Client.UI.Screens
{
    /// <summary>
    /// Duel HUD (ticket 35): round and volley, both HP values as number and bar, visible statuses with
    /// glyphs, land, time remaining with a "!" urgency badge, and each player's ready flag (never their
    /// choice). Content comes from <see cref="HudModel"/>, which switches the status line to a compact
    /// glyph form at large text scales. During a replay the HP shown is held at the pre-volley values
    /// until the flight animation ends, so the display agrees with the arrows.
    /// </summary>
    public sealed class HudView : UiScreen
    {
        private readonly Text _centre;
        private readonly Text _timer;
        private readonly Text[] _name = new Text[2];
        private readonly Text[] _hp = new Text[2];
        private readonly RectTransform[] _hpFill = new RectTransform[2];
        private readonly Text[] _status = new Text[2];
        private readonly Button _pause;
        private readonly Text _pausedBanner;
        private int[] _heldHp;
        private PublicSnapshot _last;
        private float _pulse;

        public event Action PauseToggled;

        public HudView(Services.ClientContext ctx, UiFactory ui, Transform parent)
            : base(ctx, ui, parent, "Hud", new Color(0, 0, 0, 0), false)
        {
            RectTransform bar = ui.Panel(Root, "TopBar", UiTheme.Panel, false);
            UiFactory.Region(bar, 0f, 0.86f, 1f, 1f);
            for (int i = 0; i < 2; i++)
            {
                RectTransform box = UiFactory.Rect(i == 0 ? "PlayerA" : "PlayerB", bar);
                UiFactory.Region(box, i == 0 ? 0f : 0.68f, 0f, i == 0 ? 0.32f : 1f, 1f);
                TextAnchor align = i == 0 ? TextAnchor.UpperLeft : TextAnchor.UpperRight;
                _name[i] = ui.Label(box, string.Empty, UiFactory.SizeBody, align, i == 0 ? UiTheme.PlayerA : UiTheme.PlayerB);
                UiFactory.Region(_name[i].rectTransform, 0.03f, 0.62f, 0.97f, 1f);
                // HP bar: an outlined track and a fill, so HP reads as length as well as a number.
                RectTransform track = ui.Panel(box, "HpTrack", UiTheme.Opaque, false);
                UiFactory.Region(track, 0.03f, 0.44f, 0.97f, 0.58f);
                _hpFill[i] = ui.Panel(track, "HpFill", i == 0 ? UiTheme.PlayerA : UiTheme.PlayerB, false);
                _hpFill[i].pivot = new Vector2(i == 0 ? 0f : 1f, 0.5f);
                _hp[i] = ui.Label(box, string.Empty, UiFactory.SizeSmall, align, UiTheme.Text);
                UiFactory.Region(_hp[i].rectTransform, 0.03f, 0.2f, 0.97f, 0.44f);
                _status[i] = ui.Label(box, string.Empty, UiFactory.SizeSmall, align, UiTheme.TextMuted);
                UiFactory.Region(_status[i].rectTransform, 0.03f, -0.3f, 0.97f, 0.2f);
            }
            _centre = ui.Label(bar, string.Empty, UiFactory.SizeBody, TextAnchor.UpperCenter, UiTheme.Text);
            UiFactory.Region(_centre.rectTransform, 0.32f, 0.45f, 0.6f, 1f);
            _timer = ui.Label(bar, string.Empty, UiFactory.SizeLarge, TextAnchor.LowerCenter, UiTheme.Warning);
            UiFactory.Region(_timer.rectTransform, 0.32f, 0f, 0.6f, 0.55f);
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
        public void SetRemaining(double seconds, bool timed)
        {
            if (!timed)
            {
                _timer.text = string.Empty;
                return;
            }
            bool urgent = seconds <= HudModel.UrgentSeconds && seconds > 0;
            string time = TF("hud.time", UiFactory.Seconds(seconds));
            _timer.text = urgent ? TF("hud.timeUrgent", time) : time;
            if (urgent && !Ctx.Settings.ReducedMotion)
            {
                _pulse += Time.unscaledDeltaTime * 6f;
                float s = 1f + 0.08f * Mathf.Sin(_pulse);
                _timer.rectTransform.localScale = new Vector3(s, s, 1f);
            }
            else _timer.rectTransform.localScale = Vector3.one;
        }

        public void Refresh(PublicSnapshot s)
        {
            if (s == null) return;
            _last = s;
            PublicSnapshot shown = s;
            if (_heldHp != null)
            {
                shown = Clone(s);
                shown.HpA = _heldHp[0];
                shown.HpB = _heldHp[1];
            }
            bool timed = s.Stage != HostStage.LoadoutEntry && s.Stage != HostStage.LoadoutReady && s.Stage != HostStage.MatchOver;
            HudState hud = HudModel.Build(shown, Ctx.Loc, Ctx.PlayerName, Ctx.Settings.TextScale, timed);
            _centre.text = hud.RoundText;
            SetRemaining(s.StageSecondsRemaining, timed);
            for (int i = 0; i < 2; i++)
            {
                HudPlayerLine line = hud[(PlayerSide)i];
                _name[i].text = string.IsNullOrEmpty(line.ReadyTag) ? line.Name : TF("hud.nameReady", line.Name, line.ReadyTag);
                _hp[i].text = TF("hud.hpLand", line.HpText, line.LandText);
                _status[i].text = line.StatusLine;
                RectTransform fill = _hpFill[i];
                fill.anchorMin = new Vector2(i == 0 ? 0f : 1f - (float)line.HpFraction, 0f);
                fill.anchorMax = new Vector2(i == 0 ? (float)line.HpFraction : 1f, 1f);
                fill.offsetMin = Vector2.zero;
                fill.offsetMax = Vector2.zero;
            }
            _pausedBanner.gameObject.SetActive(s.Paused);
            UiFactory.ButtonLabel(_pause).text = T(s.Paused ? "hud.resume" : "hud.pause");
        }

        private static PublicSnapshot Clone(PublicSnapshot s) => new PublicSnapshot
        {
            Phase = s.Phase, Stage = s.Stage, StageSide = s.StageSide, StageSecondsRemaining = s.StageSecondsRemaining, Paused = s.Paused,
            Round = s.Round, Volley = s.Volley, Attacker = s.Attacker, Terrain = s.Terrain, FrontierCellId = s.FrontierCellId,
            HpA = s.HpA, HpB = s.HpB, StatusA = s.StatusA, StatusB = s.StatusB, CellsA = s.CellsA, CellsB = s.CellsB,
            LockedA = s.LockedA, LockedB = s.LockedB, DuelWinner = s.DuelWinner, HpDifferenceUnits = s.HpDifferenceUnits,
            OfferedCards = s.OfferedCards, OfferedQuotas = s.OfferedQuotas, Result = s.Result,
        };
    }
}
