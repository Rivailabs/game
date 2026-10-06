using System;
using System.Globalization;
using AstraKingdoms.Client.Localization;
using AstraKingdoms.Client.Services;
using UnityEngine;
using UnityEngine.UI;

namespace AstraKingdoms.Client.UI.Screens
{
    /// <summary>
    /// Language, independent music/effects volume, reduced camera shake, vibration and text size;
    /// persisted immediately. Shown first on the very first launch.
    /// </summary>
    public sealed class SettingsScreen : UiScreen
    {
        private readonly Text _music;
        private readonly Text _effects;
        private readonly Button _shake;
        private readonly Button _haptics;
        private readonly Button _textScale;
        private readonly Button[] _languages;

        /// <summary>Raised on Done; true when the language or text size changed (screens must be rebuilt).</summary>
        public event Action<bool> Done;

        private readonly string _initialLanguage;
        private readonly float _initialScale;

        public SettingsScreen(ClientContext ctx, UiFactory ui, Transform parent) : base(ctx, ui, parent, "SettingsScreen", UiTheme.Background)
        {
            _initialLanguage = ctx.Settings.Language;
            _initialScale = ctx.Settings.TextScale;
            RectTransform col = ui.Column(Root, "Column", 14, 30);
            UiFactory.Region(col, 0.2f, 0.02f, 0.8f, 0.98f);
            ui.Label(col, T("settings.title"), UiFactory.SizeLarge, TextAnchor.MiddleCenter, UiTheme.Text);
            if (!ctx.Settings.FirstRunComplete)
                ui.Label(col, T("settings.firstRun"), UiFactory.SizeBody, TextAnchor.MiddleCenter, UiTheme.Warning);

            ui.Label(col, T("settings.language"), UiFactory.SizeBody, TextAnchor.MiddleLeft, UiTheme.TextMuted);
            RectTransform langRow = ui.Row(col, "Languages", 12);
            UiFactory.Prefer(langRow.gameObject, -1, ui.Scaled(UiFactory.SizeBody) + 40);
            _languages = new Button[Localizer.SupportedLanguages.Count];
            for (int i = 0; i < _languages.Length; i++)
            {
                string code = Localizer.SupportedLanguages[i];
                _languages[i] = ui.Button(langRow, Localizer.Endonym(code), () => SetLanguage(code), UiTheme.Button);
            }
            ui.Label(col, T("settings.reviewNote"), UiFactory.SizeSmall, TextAnchor.MiddleLeft, UiTheme.TextMuted);

            _music = ui.Label(col, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleLeft, UiTheme.Text);
            ui.Slider(col, 0, 100, true, ctx.Settings.MusicVolume * 100f, v =>
            {
                Ctx.Settings.MusicVolume = v / 100f;
                Apply();
            });
            _effects = ui.Label(col, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleLeft, UiTheme.Text);
            ui.Slider(col, 0, 100, true, ctx.Settings.EffectsVolume * 100f, v =>
            {
                Ctx.Settings.EffectsVolume = v / 100f;
                Apply();
            });
            _shake = ui.Button(col, string.Empty, () =>
            {
                Ctx.Settings.ReducedCameraShake = !Ctx.Settings.ReducedCameraShake;
                Apply();
            }, UiTheme.Button);
            _haptics = ui.Button(col, string.Empty, () =>
            {
                Ctx.Settings.Haptics = !Ctx.Settings.Haptics;
                Apply();
                Haptics.Pulse();
            }, UiTheme.Button);
            _textScale = ui.Button(col, string.Empty, () =>
            {
                Ctx.Settings.TextScale = Ctx.Settings.NextTextScale();
                Apply();
            }, UiTheme.Button);
            UiFactory.Flexible(col);
            ui.Button(col, T("settings.done"), Finish, UiTheme.ButtonPrimary, UiFactory.SizeLarge);
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
            Ctx.Audio?.Play(Services.Sfx.Click);
            Refresh();
        }

        private void Refresh()
        {
            var s = Ctx.Settings;
            _music.text = TF("settings.music", Mathf.RoundToInt(s.MusicVolume * 100));
            _effects.text = TF("settings.effects", Mathf.RoundToInt(s.EffectsVolume * 100));
            UiFactory.ButtonLabel(_shake).text = TF("settings.shake", T(s.ReducedCameraShake ? "settings.on" : "settings.off"));
            UiFactory.ButtonLabel(_haptics).text = TF("settings.haptics", T(s.Haptics ? "settings.on" : "settings.off"));
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
