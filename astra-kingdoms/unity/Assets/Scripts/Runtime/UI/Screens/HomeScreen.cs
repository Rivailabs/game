using System;
using AstraKingdoms.Rules.Bots;
using UnityEngine;
using UnityEngine.UI;

namespace AstraKingdoms.Client.UI.Screens
{
    /// <summary>Home: Play on one phone, Practice against a labelled bot, Settings (plan: "Player journey").</summary>
    public sealed class HomeScreen : UiScreen
    {
        private readonly Button _difficulty;
        private BotDifficulty _level = BotDifficulty.Normal;

        public event Action PlaySharedPhone;
        public event Action<BotDifficulty> PlayPractice;
        public event Action OpenSettings;
        public event Action OpenReplay;

        public HomeScreen(Services.ClientContext ctx, UiFactory ui, Transform parent, bool developmentTools)
            : base(ctx, ui, parent, "HomeScreen", UiTheme.Background)
        {
            RectTransform col = ui.Column(Root, "Column", 18, 40);
            UiFactory.Region(col, 0.25f, 0.04f, 0.75f, 0.96f);
            ui.Label(col, T("app.title"), UiFactory.SizeTitle, TextAnchor.MiddleCenter, UiTheme.Text);
            ui.Button(col, T("home.play"), () => PlaySharedPhone?.Invoke(), UiTheme.ButtonPrimary, UiFactory.SizeLarge);
            ui.Label(col, T("home.playHint"), UiFactory.SizeSmall, TextAnchor.MiddleCenter, UiTheme.TextMuted);
            ui.Button(col, T("home.practice"), () => PlayPractice?.Invoke(_level), UiTheme.Button, UiFactory.SizeLarge);
            _difficulty = ui.Button(col, string.Empty, CycleDifficulty, UiTheme.Panel);
            ui.Label(col, T("home.practiceHint"), UiFactory.SizeSmall, TextAnchor.MiddleCenter, UiTheme.TextMuted);
            ui.Button(col, T("home.settings"), () => OpenSettings?.Invoke(), UiTheme.Button);
            if (developmentTools) ui.Button(col, T("home.replay"), () => OpenReplay?.Invoke(), UiTheme.Panel, UiFactory.SizeSmall);
            UiFactory.Flexible(col);
            ui.Label(col, TF("app.footer", Rules.Core.RulesConstants.RulesVersion, Application.version), UiFactory.SizeSmall,
                TextAnchor.LowerCenter, UiTheme.TextMuted);
            RefreshDifficulty();
        }

        private void CycleDifficulty()
        {
            _level = (BotDifficulty)(((int)_level + 1) % 3);
            RefreshDifficulty();
        }

        private void RefreshDifficulty() => UiFactory.ButtonLabel(_difficulty).text = TF("home.difficulty", Ctx.DifficultyName(_level));
    }
}
