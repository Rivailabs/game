using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;
using UnityEngine;
using UnityEngine.UI;

namespace AstraKingdoms.Client.UI.Screens
{
    /// <summary>Private Starter loadout: equip 1 to 5 of the five starter weapons (ticket 5 / 15 scope for the pilot).</summary>
    public sealed class LoadoutScreen : UiScreen
    {
        private readonly Text _title;
        private readonly Text _count;
        private readonly Text _status;
        private readonly List<Button> _buttons = new List<Button>();
        private readonly List<int> _ids = new List<int>();
        private readonly HashSet<int> _equipped = new HashSet<int>();
        private PlayerSide _side;

        public event Action<PlayerSide, int[]> Confirmed;

        public LoadoutScreen(Services.ClientContext ctx, UiFactory ui, Transform parent) : base(ctx, ui, parent, "LoadoutScreen", UiTheme.Opaque)
        {
            RectTransform col = ui.Column(Root, "Column", 14, 30);
            UiFactory.Region(col, 0.15f, 0.02f, 0.85f, 0.98f);
            _title = ui.Label(col, string.Empty, UiFactory.SizeLarge, TextAnchor.MiddleCenter, UiTheme.Text);
            ui.Label(col, T("loadout.hint"), UiFactory.SizeBody, TextAnchor.MiddleCenter, UiTheme.TextMuted);
            foreach (WeaponDefinition w in WeaponCatalog.ForPreset(CatalogPreset.Starter))
            {
                int id = w.Id;
                _ids.Add(id);
                Button b = WeaponButton.Create(ui, col, Ctx, w, () => Toggle(id));
                _buttons.Add(b);
            }
            _count = ui.Label(col, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleCenter, UiTheme.Text);
            _status = ui.Label(col, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleCenter, UiTheme.Warning);
            UiFactory.Flexible(col);
            ui.Button(col, T("loadout.confirm"), Confirm, UiTheme.ButtonPrimary, UiFactory.SizeLarge);
        }

        /// <summary>Opens a fresh, private loadout form for a player (nothing from a previous entry survives).</summary>
        public void Open(PlayerSide side)
        {
            _side = side;
            _equipped.Clear();
            foreach (int id in _ids) _equipped.Add(id);
            _status.text = string.Empty;
            _title.text = TF("loadout.title", Ctx.PlayerName(side));
            Refresh();
            Show();
        }

        private void Toggle(int id)
        {
            if (!_equipped.Remove(id)) _equipped.Add(id);
            Ctx.Audio?.Play(Services.Sfx.Click);
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
            Confirmed?.Invoke(_side, list.ToArray());
        }

        public void ShowRejection(string code) => _status.text = TF("select.rejected", code);
    }
}
