using System;
using System.Collections.Generic;
using AstraKingdoms.Client.Flow;
using AstraKingdoms.Rules.Core;
using UnityEngine;
using UnityEngine.UI;

namespace AstraKingdoms.Client.UI.Screens
{
    /// <summary>
    /// Private loadout (tickets 15 and 44). The rows are the room's symmetric catalogue from
    /// <see cref="LoadoutModel"/>: each weapon says whether it is Owned or Loaned for this room, so
    /// loans are clear, and account progression never removes a weapon the opponent could equip.
    /// Slot limits follow the rules (Starter 1-5; Full 1-6 plus a reserve once six are equipped).
    /// </summary>
    public sealed class LoadoutScreen : UiScreen
    {
        private readonly Text _title;
        private readonly Text _hint;
        private readonly Text _count;
        private readonly Text _status;
        private readonly RectTransform _list;
        private readonly Button _reserveButton;
        private readonly List<Button> _buttons = new List<Button>();
        private LoadoutModel _model;
        private PlayerSide _side;
        private bool _pickingReserve;

        /// <summary>side, equipped weapon IDs, reserve (0 = none).</summary>
        public event Action<PlayerSide, int[], int> ConfirmedWithReserve;
        public event Action<PlayerSide, int[]> Confirmed;

        public LoadoutScreen(Services.ClientContext ctx, UiFactory ui, Transform parent) : base(ctx, ui, parent, "LoadoutScreen", UiTheme.Opaque)
        {
            RectTransform col = ui.Column(Root, "Column", 8, 24);
            UiFactory.Region(col, 0.1f, 0.02f, 0.9f, 0.98f);
            _title = ui.Label(col, string.Empty, UiFactory.SizeLarge, TextAnchor.MiddleCenter, UiTheme.Text);
            _hint = ui.Label(col, string.Empty, UiFactory.SizeSmall, TextAnchor.MiddleCenter, UiTheme.TextMuted);
            _list = ui.Column(col, "Weapons", 4, 0);
            _count = ui.Label(col, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleCenter, UiTheme.Text);
            _reserveButton = ui.Button(col, T("loadout.pickReserve"), () => { _pickingReserve = !_pickingReserve; Refresh(); }, UiTheme.Panel, UiFactory.SizeSmall);
            _status = ui.Label(col, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleCenter, UiTheme.Warning);
            UiFactory.Flexible(col);
            ui.Button(col, T("loadout.confirm"), Confirm, UiTheme.ButtonPrimary, UiFactory.SizeLarge);
        }

        /// <summary>Opens a fresh, private loadout form for a player (nothing from a previous entry survives).</summary>
        public void Open(PlayerSide side) => Open(side, CatalogPreset.Starter, Ctx.AccountLevel());

        public void Open(PlayerSide side, CatalogPreset preset, int accountLevel)
        {
            _side = side;
            _pickingReserve = false;
            _model = new LoadoutModel(preset, accountLevel);
            _status.text = string.Empty;
            _title.text = TF("loadout.title", Ctx.PlayerName(side));
            _hint.text = TF(preset == CatalogPreset.Starter ? "loadout.hintStarter" : "loadout.hintFull", _model.MaxSlots);
            foreach (Button b in _buttons) UnityEngine.Object.Destroy(b.gameObject);
            _buttons.Clear();
            foreach (LoadoutRow row in _model.Rows)
            {
                int id = row.Weapon.Id;
                Button b = WeaponButton.Create(Ui, _list, Ctx, row.Weapon, () => Toggle(id));
                _buttons.Add(b);
            }
            _reserveButton.gameObject.SetActive(preset == CatalogPreset.Full);
            Refresh();
            Show();
        }

        private void Toggle(int id)
        {
            string refusal = _pickingReserve ? _model.SetReserve(_model.Reserve == id ? 0 : id) : _model.Toggle(id);
            _status.text = refusal == null ? string.Empty : T(refusal);
            _pickingReserve = false;
            Ctx.Audio?.Play(Audio.AudioCue.UiClick);
            Refresh();
        }

        private void Refresh()
        {
            for (int i = 0; i < _model.Rows.Count; i++)
            {
                LoadoutRow row = _model.Rows[i];
                WeaponButton.SetSelected(_buttons[i], row.Equipped);
                string access = T(row.Access == WeaponAccess.Owned ? "loadout.owned" : "loadout.loaned");
                string caption = WeaponButton.Caption(Ctx, row.Weapon);
                string text = row.IsReserve ? TF("loadout.rowReserve", caption, access) : TF("loadout.row", caption, access);
                UiFactory.ButtonLabel(_buttons[i]).text = (row.Equipped ? "✓ " : string.Empty) + text;
            }
            _count.text = TF("loadout.countOf", _model.EquippedCount, _model.MaxSlots);
            UiFactory.ButtonLabel(_reserveButton).text = T(_pickingReserve ? "loadout.pickReserveActive" : "loadout.pickReserve");
            if (_model.AnyLoaned && string.IsNullOrEmpty(_status.text)) _status.text = T("loadout.loanNote");
        }

        private void Confirm()
        {
            int[] ids = _model.EquippedIds();
            if (ids.Length == 0)
            {
                _status.text = T("loadout.needOne");
                return;
            }
            if (ConfirmedWithReserve != null) ConfirmedWithReserve(_side, ids, _model.Reserve);
            else Confirmed?.Invoke(_side, ids);
        }

        public void ShowRejection(string code) => _status.text = TF("select.rejected", code);
    }
}
