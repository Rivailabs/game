using System;
using System.Collections.Generic;
using AstraKingdoms.Client.Services;
using AstraKingdoms.Client.UI;
using AstraKingdoms.Client.UI.Screens;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using UnityEngine;
using UnityEngine.UI;

namespace AstraKingdoms.Client.Online.UI
{
    /// <summary>Private loadout for either catalog (Starter: 1-5 of 5; Full: 1-6 of the 20 loaned weapons). No reserve picker yet.</summary>
    public sealed class OnlineLoadoutScreen : UiScreen
    {
        private readonly RectTransform _grid;
        private readonly Text _title;
        private readonly Text _count;
        private readonly Text _status;
        private readonly List<Button> _buttons = new List<Button>();
        private readonly List<int> _ids = new List<int>();
        private readonly HashSet<int> _equipped = new HashSet<int>();
        private int _max;

        public event Action<int[]> Confirmed;

        public OnlineLoadoutScreen(ClientContext ctx, UiFactory ui, Transform parent) : base(ctx, ui, parent, "OnlineLoadout", UiTheme.Opaque)
        {
            RectTransform col = ui.Column(Root, "Column", 10, 24);
            UiFactory.Region(col, 0.05f, 0.02f, 0.95f, 0.98f);
            _title = ui.Label(col, string.Empty, UiFactory.SizeLarge, TextAnchor.MiddleCenter, UiTheme.Text);
            _count = ui.Label(col, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleCenter, UiTheme.TextMuted);
            _grid = ui.Row(col, "Weapons", 8);
            var le = _grid.gameObject.AddComponent<LayoutElement>();
            le.flexibleHeight = 1;
            _status = ui.Label(col, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleCenter, UiTheme.Warning);
            ui.Button(col, T("loadout.confirm"), Confirm, UiTheme.ButtonPrimary, UiFactory.SizeLarge);
        }

        public void Open(CatalogPreset catalog)
        {
            foreach (Button b in _buttons) UnityEngine.Object.Destroy(b.gameObject);
            _buttons.Clear();
            _ids.Clear();
            _equipped.Clear();
            _max = catalog == CatalogPreset.Starter ? RulesConstants.StarterMaxSlots : RulesConstants.FullMaxSlots;
            // Weapons in columns of five so twenty fit on a landscape phone.
            RectTransform column = null;
            int n = 0;
            foreach (WeaponDefinition w in WeaponCatalog.ForPreset(catalog))
            {
                if (n++ % 5 == 0) column = Ui.Column(_grid, "WeaponColumn", 6, 0);
                int id = w.Id;
                _ids.Add(id);
                _buttons.Add(WeaponButton.Create(Ui, column, Ctx, w, () => Toggle(id)));
                if (_equipped.Count < _max) _equipped.Add(id);
            }
            _title.text = TF("online.loadout.title", catalog == CatalogPreset.Starter ? T("online.catalog.starter") : T("online.catalog.full"));
            _status.text = TF("online.loadout.hint", _max);
            Refresh();
            Show();
        }

        public void ShowRejection(string code) => _status.text = TF("select.rejected", code);

        private void Toggle(int id)
        {
            if (!_equipped.Remove(id) && _equipped.Count < _max) _equipped.Add(id);
            Refresh();
        }

        private void Refresh()
        {
            for (int i = 0; i < _ids.Count; i++) WeaponButton.SetSelected(_buttons[i], _equipped.Contains(_ids[i]));
            _count.text = TF("loadout.count", _equipped.Count);
        }

        private void Confirm()
        {
            if (_equipped.Count == 0)
            {
                _status.text = T("loadout.needOne");
                return;
            }
            var list = new List<int>(_equipped);
            list.Sort();
            Confirmed?.Invoke(list.ToArray());
        }
    }

    /// <summary>
    /// The duel winner's online card window (12 s). Offers Auto Cut with the card and pose the rules
    /// bot's planner finds best on the public map, previewed exactly before sending. The full
    /// drawn-cut screen (LandScreen) is tied to the local host and is not yet reused online.
    /// </summary>
    public sealed class OnlineCutScreen : UiScreen
    {
        private readonly Text _cards;
        private readonly Text _timer;
        private readonly Text _status;
        private PlayerView _view;

        public event Action<CutPlan> CutRequested;

        public OnlineCutScreen(ClientContext ctx, UiFactory ui, Transform parent) : base(ctx, ui, parent, "OnlineCut", UiTheme.Background)
        {
            RectTransform col = ui.Column(Root, "Column", 12, 30);
            UiFactory.Region(col, 0.25f, 0.1f, 0.75f, 0.9f);
            ui.Label(col, T("online.cut.title"), UiFactory.SizeLarge, TextAnchor.MiddleCenter, UiTheme.Text);
            _timer = ui.Label(col, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleCenter, UiTheme.TextMuted);
            _cards = ui.Label(col, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleLeft, UiTheme.Text);
            _status = ui.Label(col, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleCenter, UiTheme.Warning);
            ui.Button(col, T("online.cut.auto"), Auto, UiTheme.ButtonPrimary, UiFactory.SizeLarge);
            ui.Label(col, T("online.cut.skipHint"), UiFactory.SizeSmall, TextAnchor.MiddleCenter, UiTheme.TextMuted);
        }

        public void Open(PlayerView view)
        {
            _view = view;
            var lines = new List<string>();
            for (int i = 0; i < view.OfferedCards.Count; i++)
                lines.Add(TF("online.cut.card", T("card." + (int)view.OfferedCards[i]), view.OfferedQuotas[i]));
            _cards.text = string.Join("\n", lines);
            _status.text = string.Empty;
            Show();
        }

        public void SetRemaining(double seconds) => _timer.text = TF("hud.time", UiFactory.Seconds(seconds));
        public void ShowRejection(string code) => _status.text = TF("select.rejected", code);

        private void Auto()
        {
            if (_view == null) return;
            var budget = new CutSearchBudget { Anchors = 2, Rotations = 2, AllCards = true, CenterOffsetPercent = 50 };
            CutPlan plan = CutPlanner.Plan(_view, budget, new BotRng((ulong)DateTime.UtcNow.Ticks));
            if (plan == null)
            {
                _status.text = T("online.cut.none");
                return;
            }
            CutRequested?.Invoke(plan);
        }
    }

    /// <summary>A full-screen notice while waiting on the opponent or the service (opaque: nothing private behind it).</summary>
    public sealed class OnlineWaitScreen : UiScreen
    {
        private readonly Text _text;
        private readonly Button _action;
        private Action _onAction;

        public OnlineWaitScreen(ClientContext ctx, UiFactory ui, Transform parent) : base(ctx, ui, parent, "OnlineWait", UiTheme.Background)
        {
            RectTransform col = ui.Column(Root, "Column", 16, 40);
            UiFactory.Region(col, 0.2f, 0.25f, 0.8f, 0.75f);
            _text = ui.Label(col, string.Empty, UiFactory.SizeLarge, TextAnchor.MiddleCenter, UiTheme.Text);
            _action = ui.Button(col, string.Empty, () => _onAction?.Invoke(), UiTheme.Button);
        }

        public void Open(string text, string actionLabel = null, Action onAction = null)
        {
            _text.text = text;
            _onAction = onAction;
            _action.gameObject.SetActive(actionLabel != null);
            if (actionLabel != null) UiFactory.ButtonLabel(_action).text = actionLabel;
            Show();
        }
    }

    /// <summary>Always-on strip: connection state, who the opponent is (a bot is always labelled) and whether they are online.</summary>
    public sealed class OnlineStatusBar : UiScreen
    {
        private readonly Text _left;
        private readonly Text _right;

        public OnlineStatusBar(ClientContext ctx, UiFactory ui, Transform parent) : base(ctx, ui, parent, "OnlineStatus", UiTheme.Panel, blocksInput: false)
        {
            UiFactory.Region(Root, 0f, 0.95f, 1f, 1f);
            _left = ui.Label(Root, string.Empty, UiFactory.SizeSmall, TextAnchor.MiddleLeft, UiTheme.Text);
            UiFactory.Stretch(_left.rectTransform, 16, 0, 16, 0);
            _right = ui.Label(Root, string.Empty, UiFactory.SizeSmall, TextAnchor.MiddleRight, UiTheme.TextMuted);
            UiFactory.Stretch(_right.rectTransform, 16, 0, 16, 0);
        }

        public void Set(string connection, string opponent, bool opponentOnline)
        {
            _left.text = connection;
            _right.text = opponent == null ? string.Empty : opponentOnline ? opponent : opponent + " - " + T("online.opponent.offline");
        }
    }
}
