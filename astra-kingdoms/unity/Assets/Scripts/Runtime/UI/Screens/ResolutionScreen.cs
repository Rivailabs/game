using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace AstraKingdoms.Client.UI.Screens
{
    /// <summary>Concise explanation shown once the replayed arrows have landed.</summary>
    public sealed class ResolutionScreen : UiScreen
    {
        private readonly Text _text;
        private readonly RectTransform _box;

        public ResolutionScreen(Services.ClientContext ctx, UiFactory ui, Transform parent)
            : base(ctx, ui, parent, "ResolutionScreen", new Color(0, 0, 0, 0), false)
        {
            _box = ui.Panel(Root, "Explanation", UiTheme.Background, false);
            UiFactory.Region(_box, 0.08f, 0.02f, 0.92f, 0.36f);
            _text = ui.Label(_box, string.Empty, UiFactory.SizeBody, TextAnchor.UpperLeft, UiTheme.Text);
            UiFactory.Stretch(_text.rectTransform, 20, 12, 20, 12);
        }

        public void Open()
        {
            _box.gameObject.SetActive(false);
            Show();
        }

        public void ShowExplanation(IReadOnlyList<string> lines)
        {
            _text.text = string.Join("\n", lines);
            _box.gameObject.SetActive(true);
        }
    }
}
