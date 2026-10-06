using System;
using System.Collections.Generic;
using AstraKingdoms.Client.Flow;
using AstraKingdoms.Client.Presentation;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Core;
using UnityEngine;
using UnityEngine.UI;

namespace AstraKingdoms.Client.UI.Screens
{
    /// <summary>
    /// Home and mode selection (ticket 43; plan "Player journey": Play, Practice and Play with a
    /// Friend first). Entries come from <see cref="ModeCatalog"/>: shared phone, practice and the
    /// tutorial always work offline; online entries stay visible but disabled with the specific
    /// reason (offline, or not in this build) instead of failing later. Practice has its own bot
    /// level, room catalogue (Starter or the loaned Full set) and arena.
    /// </summary>
    public sealed class HomeScreen : UiScreen
    {
        private readonly Button _difficulty;
        private readonly Button _catalog;
        private readonly Button _arena;
        private readonly RectTransform _modes;
        private readonly List<GameObject> _modeObjects = new List<GameObject>();
        private BotDifficulty _level = BotDifficulty.Normal;
        private CatalogPreset _practiceCatalog = CatalogPreset.Starter;
        private int _arenaIndex;

        public event Action PlaySharedPhone;
        public event Action<BotDifficulty> PlayPractice;
        public event Action StartTutorial;
        public event Action OpenSettings;
        public event Action OpenReplay;
        public event Action OpenWeapons;

        public CatalogPreset PracticeCatalog => _practiceCatalog;
        public string ArenaVariantId => ArenaVariants.All[_arenaIndex].Id;
        /// <summary>Set by the online layer when its service is configured in this build.</summary>
        public Func<bool> OnlineServiceConfigured { get; set; } = () => OnlineHook != null;

        /// <summary>Set by the optional online module (Assets/Scripts/Online/UI); adds "Play online" when present.</summary>
        public static Action<HomeScreen, Services.ClientContext> OnlineHook;

        public HomeScreen(Services.ClientContext ctx, UiFactory ui, Transform parent, bool developmentTools)
            : base(ctx, ui, parent, "HomeScreen", UiTheme.Background)
        {
            RectTransform col = ui.Column(Root, "Column", 12, 30);
            UiFactory.Region(col, 0.22f, 0.02f, 0.78f, 0.98f);
            ui.Label(col, T("app.title"), UiFactory.SizeTitle, TextAnchor.MiddleCenter, UiTheme.Text);
            _modes = ui.Column(col, "Modes", 10, 0);
            RectTransform practiceRow = ui.Row(col, "PracticeOptions", 8);
            UiFactory.Prefer(practiceRow.gameObject, -1, ui.Scaled(UiFactory.SizeSmall) + 40);
            _difficulty = ui.Button(practiceRow, string.Empty, CycleDifficulty, UiTheme.Panel, UiFactory.SizeSmall);
            _catalog = ui.Button(practiceRow, string.Empty, CycleCatalog, UiTheme.Panel, UiFactory.SizeSmall);
            _arena = ui.Button(practiceRow, string.Empty, CycleArena, UiTheme.Panel, UiFactory.SizeSmall);
            RectTransform row = ui.Row(col, "More", 8);
            UiFactory.Prefer(row.gameObject, -1, ui.Scaled(UiFactory.SizeBody) + 40);
            ui.Button(row, T("home.weapons"), () => OpenWeapons?.Invoke(), UiTheme.Button);
            ui.Button(row, T("home.settings"), () => OpenSettings?.Invoke(), UiTheme.Button);
            if (developmentTools) ui.Button(col, T("home.replay"), () => OpenReplay?.Invoke(), UiTheme.Panel, UiFactory.SizeSmall);
            UiFactory.Flexible(col);
            ui.Label(col, TF("app.footer", RulesConstants.RulesVersion, Application.version), UiFactory.SizeSmall, TextAnchor.LowerCenter, UiTheme.TextMuted);
            RefreshOptions();
        }

        public override void Show()
        {
            BuildModes();
            base.Show();
        }

        private void BuildModes()
        {
            foreach (GameObject go in _modeObjects) UnityEngine.Object.Destroy(go);
            _modeObjects.Clear();
            Connectivity c = Application.internetReachability == NetworkReachability.NotReachable ? Connectivity.Offline : Connectivity.Online;
            foreach (ModeEntry m in ModeCatalog.Build(c, OnlineServiceConfigured()))
            {
                PlayModeId mode = m.Mode;
                Button b = Ui.Button(_modes, T(m.TitleKey), () => Start(mode), m.Primary ? UiTheme.ButtonPrimary : UiTheme.Button,
                    m.Primary ? UiFactory.SizeLarge : UiFactory.SizeBody);
                b.interactable = m.Available;
                _modeObjects.Add(b.gameObject);
                Text hint = Ui.Label(_modes, m.Available ? T(m.HintKey) : T(m.UnavailableKey), UiFactory.SizeSmall, TextAnchor.MiddleCenter,
                    m.Available ? UiTheme.TextMuted : UiTheme.Warning);
                _modeObjects.Add(hint.gameObject);
            }
        }

        private void Start(PlayModeId mode)
        {
            switch (mode)
            {
                case PlayModeId.SharedPhone: PlaySharedPhone?.Invoke(); break;
                case PlayModeId.Practice: PlayPractice?.Invoke(_level); break;
                case PlayModeId.Tutorial: StartTutorial?.Invoke(); break;
                case PlayModeId.PlayOnline:
                case PlayModeId.FriendRoom:
                    // The online module's lobby offers both the queue and friend rooms.
                    OnlineHook?.Invoke(this, Ctx);
                    break;
                default: break;
            }
        }

        private void CycleDifficulty()
        {
            _level = (BotDifficulty)(((int)_level + 1) % 3);
            RefreshOptions();
        }

        private void CycleCatalog()
        {
            _practiceCatalog = _practiceCatalog == CatalogPreset.Starter ? CatalogPreset.Full : CatalogPreset.Starter;
            RefreshOptions();
        }

        private void CycleArena()
        {
            _arenaIndex = (_arenaIndex + 1) % ArenaVariants.All.Count;
            RefreshOptions();
        }

        private void RefreshOptions()
        {
            UiFactory.ButtonLabel(_difficulty).text = TF("home.difficulty", Ctx.DifficultyName(_level));
            UiFactory.ButtonLabel(_catalog).text = TF("home.catalog", T(_practiceCatalog == CatalogPreset.Starter ? "catalog.starter" : "catalog.full"));
            UiFactory.ButtonLabel(_arena).text = TF("home.arena", T(ArenaVariants.All[_arenaIndex].NameKey));
        }
    }
}
