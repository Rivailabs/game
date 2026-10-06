using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AstraKingdoms.Client.Services;
using AstraKingdoms.Client.UI;
using AstraKingdoms.Meta.Client;
using AstraKingdoms.Meta.Cosmetics;
using UnityEngine;

namespace AstraKingdoms.Client.Meta.UI
{
    /// <summary>
    /// Cosmetics locker (ticket 59): one row per slot, browse with Previous/Next, wear owned items.
    /// The chosen appearance is presentation-only; nothing here reaches the match engine.
    /// </summary>
    public sealed class LockerScreen : MetaScreen
    {
        private readonly Dictionary<CosmeticSlot, int> _browse = new Dictionary<CosmeticSlot, int>();

        /// <summary>Raised after a successful change so the arena can re-tint the fighter.</summary>
        public event Action<CosmeticAppearance> AppearanceChanged;

        public LockerScreen(ClientContext ctx, UiFactory ui, Transform parent, MetaServices meta)
            : base(ctx, ui, parent, meta, "MetaLockerScreen")
        {
            SetTitle(L("meta.locker.title"));
        }

        protected override async Task Build(RectTransform content)
        {
            Line(content, L("meta.locker.hint"), UiFactory.SizeSmall, UiTheme.TextMuted);
            LockerView view = await Meta.Api.GetLockerAsync();
            foreach (CosmeticSlot slot in Enum.GetValues(typeof(CosmeticSlot)))
            {
                List<LockerEntry> items = view.InSlot(slot).ToList();
                if (items.Count == 0) continue;
                if (!_browse.TryGetValue(slot, out int index)) index = Math.Max(0, items.FindIndex(e => e.Equipped));
                index = ((index % items.Count) + items.Count) % items.Count;
                _browse[slot] = index;
                LockerEntry shown = items[index];

                RectTransform row = RowOf(content);
                Ui.Label(row, LF("meta.locker.slot", slot, shown.Item.EnglishName), UiFactory.SizeBody, TextAnchor.MiddleLeft, UiTheme.Text);
                CosmeticSlot s = slot;
                Ui.Button(row, L("meta.locker.prev"), () => Browse(s, -1), UiTheme.Panel, UiFactory.SizeSmall);
                Ui.Button(row, L("meta.locker.next"), () => Browse(s, +1), UiTheme.Panel, UiFactory.SizeSmall);
                if (shown.Equipped)
                    Ui.Label(row, L("meta.locker.equipped"), UiFactory.SizeSmall, TextAnchor.MiddleCenter, UiTheme.ButtonSelected);
                else if (!shown.Owned)
                    Ui.Label(row, L("meta.locker.locked"), UiFactory.SizeSmall, TextAnchor.MiddleCenter, UiTheme.TextMuted);
                else
                {
                    string id = shown.Item.Id;
                    Ui.Button(row, L("meta.locker.equip"), async () =>
                    {
                        if (await Meta.Api.EquipAsync(id) == EquipResult.Equipped)
                            AppearanceChanged?.Invoke((await Meta.Api.GetLockerAsync()).Appearance);
                        Refresh();
                    }, UiTheme.ButtonPrimary, UiFactory.SizeSmall);
                }
            }
        }

        private void Browse(CosmeticSlot slot, int delta)
        {
            _browse[slot] = (_browse.TryGetValue(slot, out int i) ? i : 0) + delta;
            Refresh();
        }
    }
}
