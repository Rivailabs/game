using AstraKingdoms.Client.Services;
using AstraKingdoms.Rules.Core;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace AstraKingdoms.Client.UI
{
    /// <summary>
    /// A weapon button: element icon SHAPE + weapon name + element label (colour is only a third cue),
    /// with a text check mark for the selected state.
    /// </summary>
    public static class WeaponButton
    {
        private const string SelectedMark = "✓ ";

        public static Button Create(UiFactory ui, Transform parent, ClientContext ctx, WeaponDefinition w, UnityAction onClick)
        {
            Button b = ui.Button(parent, Caption(ctx, w), onClick, UiTheme.Button, UiFactory.SizeBody, "Weapon" + w.Id);
            Text label = UiFactory.ButtonLabel(b);
            label.alignment = TextAnchor.MiddleLeft;
            label.rectTransform.offsetMin = new Vector2(ui.Scaled(UiFactory.SizeBody) + 34, label.rectTransform.offsetMin.y);
            RawImage icon = ui.Raw(b.transform, "Icon", ElementIcons.Get(w.Element));
            icon.color = ElementIcons.Tint(w.Element);
            RectTransform rt = icon.rectTransform;
            rt.anchorMin = new Vector2(0, 0.5f);
            rt.anchorMax = new Vector2(0, 0.5f);
            float s = ui.Scaled(UiFactory.SizeBody) + 14;
            rt.sizeDelta = new Vector2(s, s);
            rt.anchoredPosition = new Vector2(10 + s / 2, 0);
            label.name = "Label";
            label.text = Caption(ctx, w);
            b.gameObject.AddComponent<WeaponButtonState>().Caption = label.text;
            return b;
        }

        public static string Caption(ClientContext ctx, WeaponDefinition w) =>
            ctx.TF("weapon.withElement", ctx.T("weapon." + w.Id), ctx.T("element." + w.Element.ToString().ToLowerInvariant()));

        public static void SetSelected(Button b, bool selected)
        {
            var state = b.GetComponent<WeaponButtonState>();
            UiFactory.SetButtonColor(b, selected ? UiTheme.ButtonSelected : UiTheme.Button);
            Text label = UiFactory.ButtonLabel(b);
            if (state != null) label.text = (selected ? SelectedMark : string.Empty) + state.Caption;
        }
    }

    /// <summary>Remembers a weapon button's caption so the selected mark can be toggled.</summary>
    public sealed class WeaponButtonState : MonoBehaviour
    {
        public string Caption;
    }
}
