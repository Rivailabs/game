using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using UnityEngine;
using UnityEngine.UI;

namespace AstraKingdoms.Client.UI.Screens
{
    /// <summary>
    /// One player's private volley choice: weapon (icon shape + name + element), aim by touch drag
    /// (pitch/yaw in the rules' 0.25 deg steps, clamped to the weapon's legal ranges), power 70-100%,
    /// dodge, and Lock. Every Open starts from a clean default, so nothing of a previous entry remains.
    /// The trajectory preview is drawn by the arena from <see cref="Combat.TrajectoryPreview"/>.
    /// </summary>
    public sealed class SelectionScreen : UiScreen
    {
        private const float QdegPerPixel = 0.5f;

        private readonly Text _title;
        private readonly Text _timer;
        private readonly RectTransform _weaponColumn;
        private readonly Text _pitch;
        private readonly Text _yaw;
        private readonly Text _power;
        private readonly Slider _powerSlider;
        private readonly Button[] _dodges = new Button[4];
        private readonly Button _lock;
        private readonly Text _status;
        private readonly Text _lastVolley;
        private readonly Button _lastToggle;
        private readonly List<Button> _weaponButtons = new List<Button>();
        private readonly List<int> _weaponIds = new List<int>();

        private PlayerSide _side;
        private int _weapon;
        private int _pitchQ;
        private int _yawQ;
        private int _powerPct = RulesConstants.MaxPowerPercent;
        private Dodge _dodge = Dodge.None;
        private float _accPitch;
        private float _accYaw;
        private bool _locked;
        private IReadOnlyList<string> _lastLines = Array.Empty<string>();

        /// <summary>Own provisional aim changed: side, weapon, pitch, yaw, power.</summary>
        public event Action<PlayerSide, int, int, int, int> PreviewChanged;
        public event Action<PlayerSide, VolleyInput> LockRequested;

        public SelectionScreen(Services.ClientContext ctx, UiFactory ui, Transform parent)
            : base(ctx, ui, parent, "SelectionScreen", new Color(0, 0, 0, 0), false)
        {
            // Left: title, timer, weapons.
            RectTransform left = ui.Panel(Root, "Left", UiTheme.Panel);
            UiFactory.Region(left, 0f, 0f, 0.28f, 0.88f);
            RectTransform leftCol = ui.Column(left, "Column", 10, 16);
            UiFactory.Stretch(leftCol);
            _title = ui.Label(leftCol, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleCenter, UiTheme.Text);
            _timer = ui.Label(leftCol, string.Empty, UiFactory.SizeLarge, TextAnchor.MiddleCenter, UiTheme.Warning);
            ui.Label(leftCol, T("select.weapon"), UiFactory.SizeSmall, TextAnchor.MiddleLeft, UiTheme.TextMuted);
            _weaponColumn = ui.Column(leftCol, "Weapons", 8, 0);

            // Right: aim pad, readouts, fine steps, power.
            RectTransform right = ui.Panel(Root, "Right", UiTheme.Panel);
            UiFactory.Region(right, 0.68f, 0f, 1f, 0.88f);
            RectTransform rightCol = ui.Column(right, "Column", 8, 16);
            UiFactory.Stretch(rightCol);
            RectTransform pad = ui.Panel(rightCol, "AimPad", UiTheme.Button);
            UiFactory.Prefer(pad.gameObject, -1, 300);
            ui.Label(pad, T("select.aimPad"), UiFactory.SizeBody, TextAnchor.MiddleCenter, UiTheme.TextMuted);
            var aim = pad.gameObject.AddComponent<AimPad>();
            aim.Dragged += OnAimDrag;
            _pitch = ui.Label(rightCol, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleLeft, UiTheme.Text);
            RectTransform pitchRow = ui.Row(rightCol, "PitchSteps", 8);
            UiFactory.Prefer(pitchRow.gameObject, -1, ui.Scaled(UiFactory.SizeBody) + 34);
            ui.Button(pitchRow, "▼ 1", () => Nudge(-4, 0), UiTheme.Button);
            ui.Button(pitchRow, "▲ 1", () => Nudge(4, 0), UiTheme.Button);
            _yaw = ui.Label(rightCol, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleLeft, UiTheme.Text);
            RectTransform yawRow = ui.Row(rightCol, "YawSteps", 8);
            UiFactory.Prefer(yawRow.gameObject, -1, ui.Scaled(UiFactory.SizeBody) + 34);
            ui.Button(yawRow, "◀ 1", () => Nudge(0, -4), UiTheme.Button);
            ui.Button(yawRow, "▶ 1", () => Nudge(0, 4), UiTheme.Button);
            _power = ui.Label(rightCol, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleLeft, UiTheme.Text);
            _powerSlider = ui.Slider(rightCol, RulesConstants.MinPowerPercent, RulesConstants.MaxPowerPercent, true, RulesConstants.MaxPowerPercent,
                v =>
                {
                    _powerPct = Mathf.RoundToInt(v);
                    RefreshAim();
                });

            // Bottom centre: dodge, lock, status, last volley.
            RectTransform bottom = ui.Panel(Root, "Bottom", UiTheme.Panel);
            UiFactory.Region(bottom, 0.28f, 0f, 0.68f, 0.34f);
            RectTransform bottomCol = ui.Column(bottom, "Column", 8, 12);
            UiFactory.Stretch(bottomCol);
            ui.Label(bottomCol, T("select.dodge"), UiFactory.SizeSmall, TextAnchor.MiddleLeft, UiTheme.TextMuted);
            RectTransform dodgeRow = ui.Row(bottomCol, "Dodges", 8);
            UiFactory.Prefer(dodgeRow.gameObject, -1, ui.Scaled(UiFactory.SizeBody) + 40);
            string[] dodgeIcons = { "■ ", "← ", "→ ", "↑ " };
            for (int i = 0; i < 4; i++)
            {
                var d = (Dodge)i;
                _dodges[i] = ui.Button(dodgeRow, dodgeIcons[i] + T("dodge." + d.ToString().ToLowerInvariant()), () => SetDodge(d), UiTheme.Button);
            }
            _lock = ui.Button(bottomCol, T("select.lock"), Lock, UiTheme.ButtonPrimary, UiFactory.SizeLarge);
            _status = ui.Label(bottomCol, string.Empty, UiFactory.SizeSmall, TextAnchor.MiddleCenter, UiTheme.Warning);
            _lastToggle = ui.Button(bottomCol, T("select.lastVolley"), ToggleLastVolley, UiTheme.Panel, UiFactory.SizeSmall);

            RectTransform lastBox = ui.Panel(Root, "LastVolley", UiTheme.Background);
            UiFactory.Region(lastBox, 0.29f, 0.36f, 0.67f, 0.86f);
            _lastVolley = ui.Label(lastBox, string.Empty, UiFactory.SizeSmall, TextAnchor.UpperLeft, UiTheme.Text);
            UiFactory.Stretch(_lastVolley.rectTransform, 14, 14, 14, 14);
            lastBox.gameObject.SetActive(false);
        }

        /// <summary>Opens a clean entry form for <paramref name="view"/>'s player.</summary>
        public void Open(PlayerView view, IReadOnlyList<string> lastVolleyLines)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            _side = view.Viewer;
            _locked = false;
            _status.text = string.Empty;
            _lastLines = lastVolleyLines ?? Array.Empty<string>();
            _lastVolley.transform.parent.gameObject.SetActive(false);
            UiFactory.ButtonLabel(_lastToggle).text = T("select.lastVolley");
            _title.text = TF("select.title", Ctx.PlayerName(_side));

            foreach (Button b in _weaponButtons)
            {
                b.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(b.gameObject);
            }
            _weaponButtons.Clear();
            _weaponIds.Clear();
            var ids = new List<int>(view.OwnLoadout.Weapons);
            if (view.ReserveEligible && view.OwnLoadout.HasReserve) ids.Add(view.OwnLoadout.Reserve);
            foreach (int id in ids)
            {
                int captured = id;
                Button b = WeaponButton.Create(Ui, _weaponColumn, Ctx, WeaponCatalog.Get(id), () => SelectWeapon(captured));
                _weaponButtons.Add(b);
                _weaponIds.Add(id);
            }

            // Clean defaults: first weapon, a moderate arc, full power, no dodge.
            _powerPct = RulesConstants.MaxPowerPercent;
            _powerSlider.value = _powerPct;
            _dodge = Dodge.None;
            _pitchQ = 0;
            _yawQ = 0;
            SelectWeapon(_weaponIds[0]);
            _pitchQ = LaunchProfiles.CentralPitchRange(WeaponCatalog.Get(_weapon)).Clamp(20 * RulesConstants.QuarterDegreesPerDegree);
            SetDodge(Dodge.None);
            _lock.interactable = true;
            RefreshAim();
            Show();
        }

        public void SetRemaining(double seconds) => _timer.text = TF("hud.time", UiFactory.Seconds(seconds));

        public void ShowRejection(string code)
        {
            _locked = false;
            _lock.interactable = true;
            _status.text = TF("select.rejected", code);
        }

        private void SelectWeapon(int id)
        {
            if (_locked) return;
            _weapon = id;
            for (int i = 0; i < _weaponIds.Count; i++) WeaponButton.SetSelected(_weaponButtons[i], _weaponIds[i] == id);
            Ctx.Audio?.Play(Services.Sfx.Click);
            RefreshAim();
        }

        private void SetDodge(Dodge d)
        {
            if (_locked) return;
            _dodge = d;
            for (int i = 0; i < 4; i++) UiFactory.SetButtonColor(_dodges[i], i == (int)d ? UiTheme.ButtonSelected : UiTheme.Button);
        }

        private void OnAimDrag(Vector2 delta)
        {
            if (_locked) return;
            _accPitch += delta.y * QdegPerPixel;
            _accYaw += delta.x * QdegPerPixel;
            int dp = (int)_accPitch;
            int dy = (int)_accYaw;
            _accPitch -= dp;
            _accYaw -= dy;
            if (dp != 0 || dy != 0) Nudge(dp, dy);
        }

        private void Nudge(int pitchQ, int yawQ)
        {
            if (_locked) return;
            _pitchQ += pitchQ;
            _yawQ += yawQ;
            RefreshAim();
        }

        private void RefreshAim()
        {
            if (_weapon == 0) return;
            WeaponDefinition w = WeaponCatalog.Get(_weapon);
            _pitchQ = LaunchProfiles.CentralPitchRange(w).Clamp(_pitchQ);
            _yawQ = LaunchProfiles.CentralYawRange(w).Clamp(_yawQ);
            _pitch.text = TF("select.pitch", PlayerVolleyReport.FormatDegrees(_pitchQ));
            _yaw.text = TF("select.yaw", PlayerVolleyReport.FormatDegrees(_yawQ));
            _power.text = TF("select.power", _powerPct);
            PreviewChanged?.Invoke(_side, _weapon, _pitchQ, _yawQ, _powerPct);
        }

        private void Lock()
        {
            if (_locked || _weapon == 0) return;
            _locked = true;
            _lock.interactable = false;
            Ctx.Audio?.Play(Services.Sfx.Lock);
            LockRequested?.Invoke(_side, new VolleyInput(_weapon, _pitchQ, _yawQ, _powerPct, _dodge));
        }

        private void ToggleLastVolley()
        {
            GameObject box = _lastVolley.transform.parent.gameObject;
            bool show = !box.activeSelf;
            box.SetActive(show);
            _lastVolley.text = _lastLines.Count == 0 ? T("select.noLastVolley") : string.Join("\n", _lastLines);
            UiFactory.ButtonLabel(_lastToggle).text = T(show ? "select.hideLastVolley" : "select.lastVolley");
        }
    }
}
