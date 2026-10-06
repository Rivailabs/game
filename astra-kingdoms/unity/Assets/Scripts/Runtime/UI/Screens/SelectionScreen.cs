using System;
using System.Collections.Generic;
using AstraKingdoms.Client.Combat;
using AstraKingdoms.Client.Flow;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using UnityEngine;
using UnityEngine.UI;

namespace AstraKingdoms.Client.UI.Screens
{
    /// <summary>
    /// One player's private volley choice (tickets 29-30): weapon (icon shape + name + element), aim
    /// by touch drag (pitch/yaw in the rules' 0.25° steps, clamped to the weapon's legal ranges, with a
    /// tick per whole degree and a visible "limit" marker), power 70-100%, dodge, and Lock. The
    /// <see cref="AimController"/> holds the choice, so the preview and the locked input are the same
    /// values. Every Open starts from a clean default, so nothing of a previous entry remains. After
    /// Lock the controls freeze and show "Locked"; only a ready flag is ever shown to anyone else.
    /// </summary>
    public sealed class SelectionScreen : UiScreen
    {
        private readonly Text _title;
        private readonly Text _timer;
        private readonly RectTransform _weaponColumn;
        private readonly Text _pitch;
        private readonly Text _yaw;
        private readonly Text _power;
        private readonly Slider _powerSlider;
        private readonly Button[] _dodges = new Button[4];
        private readonly Button[] _steps = new Button[4];
        private readonly Button _lock;
        private readonly Text _status;
        private readonly Text _lastVolley;
        private readonly Button _lastToggle;
        private readonly AimPad _aimPad;
        private readonly List<Button> _weaponButtons = new List<Button>();
        private readonly List<int> _weaponIds = new List<int>();
        private readonly AimController _aim = new AimController();

        private PlayerSide _side;
        private bool _settingSlider;
        private TutorialControl _gate = TutorialControl.All;
        private IReadOnlyList<string> _lastLines = Array.Empty<string>();

        /// <summary>Own provisional aim changed: side, weapon, pitch, yaw, power.</summary>
        public event Action<PlayerSide, int, int, int, int> PreviewChanged;
        public event Action<PlayerSide, VolleyInput> LockRequested;
        /// <summary>Tutorial hooks: which kind of choice was just made.</summary>
        public event Action<TutorialEvent> ChoiceMade;

        /// <summary>The current aim sits on a legal limit (the preview shows a limit marker).</summary>
        public bool AimAtLimit { get; private set; }

        public SelectionScreen(Services.ClientContext ctx, UiFactory ui, Transform parent)
            : base(ctx, ui, parent, "SelectionScreen", new Color(0, 0, 0, 0), false)
        {
            // Left: title, timer, weapons.
            RectTransform left = ui.Panel(Root, "Left", UiTheme.Panel);
            UiFactory.Region(left, 0f, 0f, 0.28f, 0.86f);
            RectTransform leftCol = ui.Column(left, "Column", 10, 16);
            UiFactory.Stretch(leftCol);
            _title = ui.Label(leftCol, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleCenter, UiTheme.Text);
            _timer = ui.Label(leftCol, string.Empty, UiFactory.SizeLarge, TextAnchor.MiddleCenter, UiTheme.Warning);
            ui.Label(leftCol, T("select.weapon"), UiFactory.SizeSmall, TextAnchor.MiddleLeft, UiTheme.TextMuted);
            _weaponColumn = ui.Column(leftCol, "Weapons", 8, 0);

            // Right: aim pad, readouts, fine steps, power.
            RectTransform right = ui.Panel(Root, "Right", UiTheme.Panel);
            UiFactory.Region(right, 0.68f, 0f, 1f, 0.86f);
            RectTransform rightCol = ui.Column(right, "Column", 8, 16);
            UiFactory.Stretch(rightCol);
            RectTransform pad = ui.Panel(rightCol, "AimPad", UiTheme.Button);
            UiFactory.Prefer(pad.gameObject, -1, 300);
            ui.Label(pad, T("select.aimPad"), UiFactory.SizeBody, TextAnchor.MiddleCenter, UiTheme.TextMuted);
            _aimPad = pad.gameObject.AddComponent<AimPad>();
            _aimPad.Dragged += OnAimDrag;
            _pitch = ui.Label(rightCol, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleLeft, UiTheme.Text);
            RectTransform pitchRow = ui.Row(rightCol, "PitchSteps", 8);
            UiFactory.Prefer(pitchRow.gameObject, -1, ui.Scaled(UiFactory.SizeBody) + 34);
            _steps[0] = ui.Button(pitchRow, "▼ 1", () => Nudge(-4, 0), UiTheme.Button);
            _steps[1] = ui.Button(pitchRow, "▲ 1", () => Nudge(4, 0), UiTheme.Button);
            _yaw = ui.Label(rightCol, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleLeft, UiTheme.Text);
            RectTransform yawRow = ui.Row(rightCol, "YawSteps", 8);
            UiFactory.Prefer(yawRow.gameObject, -1, ui.Scaled(UiFactory.SizeBody) + 34);
            _steps[2] = ui.Button(yawRow, "◀ 1", () => Nudge(0, -4), UiTheme.Button);
            _steps[3] = ui.Button(yawRow, "▶ 1", () => Nudge(0, 4), UiTheme.Button);
            _power = ui.Label(rightCol, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleLeft, UiTheme.Text);
            _powerSlider = ui.Slider(rightCol, RulesConstants.MinPowerPercent, RulesConstants.MaxPowerPercent, true, RulesConstants.MaxPowerPercent,
                v =>
                {
                    if (_settingSlider || !Allowed(TutorialControl.Power)) return;
                    AimFeedback f = _aim.SetPower(Mathf.RoundToInt(v));
                    if (f.Changed) ChoiceMade?.Invoke(TutorialEvent.PowerChanged);
                    Feedback(f);
                });

            // Bottom centre: dodge, lock, status, last volley.
            RectTransform bottom = ui.Panel(Root, "Bottom", UiTheme.Panel);
            UiFactory.Region(bottom, 0.28f, 0f, 0.68f, 0.34f);
            RectTransform bottomCol = ui.Column(bottom, "Column", 8, 12);
            UiFactory.Stretch(bottomCol);
            ui.Label(bottomCol, T("select.dodge"), UiFactory.SizeSmall, TextAnchor.MiddleLeft, UiTheme.TextMuted);
            RectTransform dodgeRow = ui.Row(bottomCol, "Dodges", 8);
            UiFactory.Prefer(dodgeRow.gameObject, -1, ui.Scaled(UiFactory.SizeBody) + 40);
            for (int i = 0; i < 4; i++)
            {
                var d = (Dodge)i;
                _dodges[i] = ui.Button(dodgeRow, TF("select.dodgeOption", VolleyCueBuilder.GlyphFor(CueKind.Dodge, d), T("dodge." + d.ToString().ToLowerInvariant())),
                    () => SetDodge(d), UiTheme.Button);
            }
            _lock = ui.Button(bottomCol, T("select.lock"), Lock, UiTheme.ButtonPrimary, UiFactory.SizeLarge);
            _status = ui.Label(bottomCol, string.Empty, UiFactory.SizeSmall, TextAnchor.MiddleCenter, UiTheme.Warning);
            _lastToggle = ui.Button(bottomCol, T("select.lastVolley"), ToggleLastVolley, UiTheme.Panel, UiFactory.SizeSmall);

            RectTransform lastBox = ui.Panel(Root, "LastVolley", UiTheme.Background);
            UiFactory.Region(lastBox, 0.29f, 0.36f, 0.67f, 0.84f);
            _lastVolley = ui.Label(lastBox, string.Empty, UiFactory.SizeSmall, TextAnchor.UpperLeft, UiTheme.Text);
            UiFactory.Stretch(_lastVolley.rectTransform, 14, 14, 14, 14);
            lastBox.gameObject.SetActive(false);
        }

        /// <summary>Opens a clean entry form for <paramref name="view"/>'s player.</summary>
        public void Open(PlayerView view, IReadOnlyList<string> lastVolleyLines)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            _side = view.Viewer;
            _status.text = string.Empty;
            _lastLines = lastVolleyLines ?? Array.Empty<string>();
            _lastVolley.transform.parent.gameObject.SetActive(false);
            UiFactory.ButtonLabel(_lastToggle).text = T("select.lastVolley");
            UiFactory.ButtonLabel(_lock).text = T("select.lock");
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
            _aim.Reset(_weaponIds[0]);
            _settingSlider = true;
            _powerSlider.value = _aim.PowerPercent;
            _settingSlider = false;
            for (int i = 0; i < _weaponIds.Count; i++) WeaponButton.SetSelected(_weaponButtons[i], _weaponIds[i] == _aim.WeaponId);
            ShowDodge();
            _lock.interactable = true;
            ApplyGate(_gate);
            RefreshAim();
            Show();
        }

        public void SetRemaining(double seconds) => _timer.text = TF("hud.time", UiFactory.Seconds(seconds));

        public void ShowRejection(string code)
        {
            _aim.Unlock();
            _lock.interactable = true;
            UiFactory.ButtonLabel(_lock).text = T("select.lock");
            _status.text = TF("select.rejected", code);
            ApplyGate(_gate);
        }

        /// <summary>Tutorial gating: only the taught controls respond (others stay visible but disabled).</summary>
        public void ApplyGate(TutorialControl enabled)
        {
            _gate = enabled;
            bool open = !_aim.Locked;
            foreach (Button b in _weaponButtons) b.interactable = open && Allowed(TutorialControl.Weapon);
            foreach (Button b in _steps) b.interactable = open && Allowed(TutorialControl.Aim);
            _powerSlider.interactable = open && Allowed(TutorialControl.Power);
            foreach (Button b in _dodges) b.interactable = open && Allowed(TutorialControl.Dodge);
            _lock.interactable = open && Allowed(TutorialControl.Lock);
        }

        private bool Allowed(TutorialControl c) => (_gate & c) == c;

        private void SelectWeapon(int id)
        {
            if (!Allowed(TutorialControl.Weapon)) return;
            AimFeedback f = _aim.SelectWeapon(id);
            if (_aim.Locked) return;
            for (int i = 0; i < _weaponIds.Count; i++) WeaponButton.SetSelected(_weaponButtons[i], _weaponIds[i] == id);
            Ctx.Audio?.Play(Audio.AudioCue.UiClick);
            ChoiceMade?.Invoke(TutorialEvent.WeaponSelected);
            Feedback(f);
        }

        private void SetDodge(Dodge d)
        {
            if (!Allowed(TutorialControl.Dodge) || !_aim.SetDodge(d)) return;
            ShowDodge();
            ChoiceMade?.Invoke(TutorialEvent.DodgeSelected);
        }

        private void ShowDodge()
        {
            for (int i = 0; i < 4; i++) UiFactory.SetButtonColor(_dodges[i], i == (int)_aim.Dodge ? UiTheme.ButtonSelected : UiTheme.Button);
        }

        private void OnAimDrag(Vector2 delta)
        {
            if (!Allowed(TutorialControl.Aim)) return;
            AimFeedback f = _aim.Drag(delta.x, delta.y);
            if (f.Changed) ChoiceMade?.Invoke(TutorialEvent.AimChanged);
            Feedback(f);
        }

        private void Nudge(int pitchQ, int yawQ)
        {
            if (!Allowed(TutorialControl.Aim)) return;
            AimFeedback f = _aim.Nudge(pitchQ, yawQ);
            if (f.Changed) ChoiceMade?.Invoke(TutorialEvent.AimChanged);
            Feedback(f);
        }

        private void Feedback(AimFeedback f)
        {
            if (f.DegreeTick) Services.Haptics.Tick();
            RefreshAim();
            AimAtLimit = f.PitchAtLimit || f.YawAtLimit;
        }

        private void RefreshAim()
        {
            if (_aim.WeaponId == 0) return;
            QdegRange pr = _aim.PitchRange, yr = _aim.YawRange;
            bool pitchLimit = _aim.PitchQdeg == pr.Min || _aim.PitchQdeg == pr.Max;
            bool yawLimit = _aim.YawQdeg == yr.Min || _aim.YawQdeg == yr.Max;
            string pitch = PlayerVolleyReport.FormatDegrees(_aim.PitchQdeg), yaw = PlayerVolleyReport.FormatDegrees(_aim.YawQdeg);
            _pitch.text = pitchLimit ? TF("select.pitchLimit", pitch) : TF("select.pitch", pitch);
            _yaw.text = yawLimit ? TF("select.yawLimit", yaw) : TF("select.yaw", yaw);
            _power.text = TF("select.power", _aim.PowerPercent);
            AimAtLimit = pitchLimit || yawLimit;
            PreviewChanged?.Invoke(_side, _aim.WeaponId, _aim.PitchQdeg, _aim.YawQdeg, _aim.PowerPercent);
        }

        private void Lock()
        {
            if (!Allowed(TutorialControl.Lock)) return;
            VolleyInput input = _aim.Lock();
            if (input == null) return;
            ApplyGate(_gate);
            UiFactory.ButtonLabel(_lock).text = T("select.locked");
            _status.text = T("select.lockedWaiting");
            Ctx.Audio?.Play(Audio.AudioCue.Lock);
            ChoiceMade?.Invoke(TutorialEvent.Locked);
            LockRequested?.Invoke(_side, input);
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
