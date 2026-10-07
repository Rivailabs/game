using System;
using System.Globalization;
using AstraKingdoms.Client.Flow;
using AstraKingdoms.Client.Localization;
using AstraKingdoms.Client.Services;
using UnityEngine;
using UnityEngine.UI;

namespace AstraKingdoms.Client.UI.Screens
{
    /// <summary>
    /// Settings, accessibility and data controls (ticket 46): language, independent music and effects
    /// volume, reduced camera shake, reduced motion, vibration, text size, colour-independent patterns,
    /// an untimed tutorial, and deletion of the data this phone keeps (with a confirmation step).
    /// Everything persists immediately. Shown first on the very first launch.
    /// </summary>
    public sealed class SettingsScreen : UiScreen
    {
        private readonly Text _music;
        private readonly Text _effects;
        private readonly Button _shake;
        private readonly Button _motion;
        private readonly Button _haptics;
        private readonly Button _textScale;
        private readonly Button _patterns;
        private readonly Button _untimed;
        private readonly Button _delete;
        private readonly Text _dataStatus;
        private readonly Button[] _languages;
        private bool _confirmDelete;

        /// <summary>Raised on Done; true when the language or text size changed (screens must be rebuilt).</summary>
        public event Action<bool> Done;
        /// <summary>The player confirmed deleting local data; the handler returns failed category IDs.</summary>
        public Func<System.Collections.Generic.List<string>> DeleteLocalData { get; set; }

        private readonly string _initialLanguage;
        private readonly float _initialScale;

        public SettingsScreen(ClientContext ctx, UiFactory ui, Transform parent) : base(ctx, ui, parent, "SettingsScreen", UiTheme.Background)
        {
            _initialLanguage = ctx.Settings.Language;
            _initialScale = ctx.Settings.TextScale;
            // Two columns so every control stays reachable at the largest text size.
            RectTransform left = ui.Column(Root, "Left", 10, 24);
            UiFactory.Region(left, 0.04f, 0.1f, 0.5f, 0.98f);
            RectTransform right = ui.Column(Root, "Right", 10, 24);
            UiFactory.Region(right, 0.5f, 0.1f, 0.96f, 0.98f);
            ui.Label(left, T("settings.title"), UiFactory.SizeLarge, TextAnchor.MiddleCenter, UiTheme.Text);
            if (!ctx.Settings.FirstRunComplete)
                ui.Label(left, T("settings.firstRun"), UiFactory.SizeBody, TextAnchor.MiddleCenter, UiTheme.Warning);

            ui.Label(left, T("settings.language"), UiFactory.SizeBody, TextAnchor.MiddleLeft, UiTheme.TextMuted);
            RectTransform langRow = ui.Row(left, "Languages", 12);
            UiFactory.Prefer(langRow.gameObject, -1, ui.Scaled(UiFactory.SizeBody) + 40);
            _languages = new Button[Localizer.SupportedLanguages.Count];
            for (int i = 0; i < _languages.Length; i++)
            {
                string code = Localizer.SupportedLanguages[i];
                _languages[i] = ui.Button(langRow, Localizer.Endonym(code), () => SetLanguage(code), UiTheme.Button);
            }
            ui.Label(left, T("settings.reviewNote"), UiFactory.SizeSmall, TextAnchor.MiddleLeft, UiTheme.TextMuted);

            _music = ui.Label(left, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleLeft, UiTheme.Text);
            ui.Slider(left, 0, 100, true, ctx.Settings.MusicVolume * 100f, v =>
            {
                Ctx.Settings.MusicVolume = v / 100f;
                Apply();
            });
            _effects = ui.Label(left, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleLeft, UiTheme.Text);
            ui.Slider(left, 0, 100, true, ctx.Settings.EffectsVolume * 100f, v =>
            {
                Ctx.Settings.EffectsVolume = v / 100f;
                Apply();
            });
            _textScale = ui.Button(left, string.Empty, () =>
            {
                Ctx.Settings.TextScale = Ctx.Settings.NextTextScale();
                Apply();
            }, UiTheme.Button);

            _shake = ui.Button(right, string.Empty, () => { Ctx.Settings.ReducedCameraShake = !Ctx.Settings.ReducedCameraShake; Apply(); }, UiTheme.Button);
            _motion = ui.Button(right, string.Empty, () => { Ctx.Settings.ReducedMotion = !Ctx.Settings.ReducedMotion; Apply(); }, UiTheme.Button);
            _haptics = ui.Button(right, string.Empty, () =>
            {
                Ctx.Settings.Haptics = !Ctx.Settings.Haptics;
                Apply();
                Haptics.Pulse();
            }, UiTheme.Button);
            _patterns = ui.Button(right, string.Empty, () => { Ctx.Settings.ShowPatterns = !Ctx.Settings.ShowPatterns; Apply(); }, UiTheme.Button);
            _untimed = ui.Button(right, string.Empty, () => { Ctx.Settings.TutorialUntimed = !Ctx.Settings.TutorialUntimed; Apply(); }, UiTheme.Button);
            ui.Label(right, T("settings.dataTitle"), UiFactory.SizeBody, TextAnchor.MiddleLeft, UiTheme.TextMuted);
            ui.Label(right, T("settings.dataHint"), UiFactory.SizeSmall, TextAnchor.MiddleLeft, UiTheme.TextMuted);
            _delete = ui.Button(right, T("settings.deleteData"), OnDelete, UiTheme.ButtonDanger);
            _dataStatus = ui.Label(right, string.Empty, UiFactory.SizeSmall, TextAnchor.MiddleLeft, UiTheme.Warning);

            Button done = ui.Button(Root, T("settings.done"), Finish, UiTheme.ButtonPrimary, UiFactory.SizeLarge);
            UiFactory.Region(done.GetComponent<RectTransform>(), 0.3f, 0.01f, 0.7f, 0.09f);
            Refresh();
        }

        private void SetLanguage(string code)
        {
            Ctx.Settings.Language = code;
            Ctx.Loc.Language = code;
            Apply();
        }

        private void Apply()
        {
            Ctx.SaveSettings();
            Ctx.Audio?.Play(Audio.AudioCue.UiClick);
            Refresh();
        }

        private void OnDelete()
        {
            if (!_confirmDelete)
            {
                _confirmDelete = true;
                UiFactory.ButtonLabel(_delete).text = T("settings.deleteConfirm");
                _dataStatus.text = T("settings.deleteWarning");
                return;
            }
            _confirmDelete = false;
            var failed = DeleteLocalData != null ? DeleteLocalData() : new System.Collections.Generic.List<string>();
            UiFactory.ButtonLabel(_delete).text = T("settings.deleteData");
            _dataStatus.text = failed.Count == 0 ? T("settings.deleted") : TF("settings.deleteFailed", string.Join(", ", failed));
            Refresh();
        }

        private void Refresh()
        {
            var s = Ctx.Settings;
            string On(bool b) => T(b ? "settings.on" : "settings.off");
            _music.text = TF("settings.music", Mathf.RoundToInt(s.MusicVolume * 100));
            _effects.text = TF("settings.effects", Mathf.RoundToInt(s.EffectsVolume * 100));
            UiFactory.ButtonLabel(_shake).text = TF("settings.shake", On(s.ReducedCameraShake));
            UiFactory.ButtonLabel(_motion).text = TF("settings.motion", On(s.ReducedMotion));
            UiFactory.ButtonLabel(_haptics).text = TF("settings.haptics", On(s.Haptics));
            UiFactory.ButtonLabel(_patterns).text = TF("settings.patterns", On(s.ShowPatterns));
            UiFactory.ButtonLabel(_untimed).text = TF("settings.tutorialUntimed", On(s.TutorialUntimed));
            UiFactory.ButtonLabel(_textScale).text = TF("settings.textScale", Mathf.RoundToInt(s.TextScale * 100).ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < _languages.Length; i++)
                UiFactory.SetButtonColor(_languages[i], Localizer.SupportedLanguages[i] == s.Language ? UiTheme.ButtonSelected : UiTheme.Button);
        }

        private void Finish()
        {
            Ctx.Settings.FirstRunComplete = true;
            Ctx.SaveSettings();
            Done?.Invoke(Ctx.Settings.Language != _initialLanguage || !Mathf.Approximately(Ctx.Settings.TextScale, _initialScale));
        }
    }
}
