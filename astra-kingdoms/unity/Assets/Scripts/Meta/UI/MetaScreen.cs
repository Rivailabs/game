using System;
using AstraKingdoms.Client.Services;
using AstraKingdoms.Client.UI;
using AstraKingdoms.Client.UI.Screens;
using UnityEngine;
using UnityEngine.UI;

namespace AstraKingdoms.Client.Meta.UI
{
    /// <summary>
    /// Base for the meta screens: an overlay above Home with a title, a content column rebuilt on each
    /// refresh, and a Back button. Content is rebuilt rather than diffed because these screens are small.
    /// </summary>
    public abstract class MetaScreen : UiScreen
    {
        protected readonly MetaServices Meta;
        private readonly RectTransform _content;
        private readonly Text _title;

        public event Action Closed;

        protected MetaScreen(ClientContext ctx, UiFactory ui, Transform parent, MetaServices meta, string name)
            : base(ctx, ui, parent, name, UiTheme.Background)
        {
            Meta = meta;
            RectTransform col = ui.Column(Root, "Column", 10, 24);
            UiFactory.Region(col, 0.12f, 0.02f, 0.88f, 0.98f);
            _title = ui.Label(col, string.Empty, UiFactory.SizeLarge, TextAnchor.MiddleCenter, UiTheme.Text, "Title");
            _content = ui.Column(col, "Content", 8, 0);
            UiFactory.Flexible(col);
            ui.Button(col, L("meta.back"), Close, UiTheme.Button, UiFactory.SizeBody, "Back");
        }

        /// <summary>A one-line result shown at the top of the next rebuild (e.g. after a claim or purchase).</summary>
        protected string Message;

        /// <summary>Shows <paramref name="message"/> and rebuilds.</summary>
        protected void Report(string message)
        {
            Message = message;
            Refresh();
        }

        protected string L(string key) => Meta.Text.L(key);
        protected string LF(string key, params object[] args) => Meta.Text.LF(key, args);

        protected void SetTitle(string text) => _title.text = text;

        public void Open()
        {
            Show();
            Refresh();
        }

        public void Close()
        {
            Hide();
            Closed?.Invoke();
        }

        /// <summary>Clears and rebuilds the content column.</summary>
        protected async void Refresh()
        {
            try
            {
                for (int i = _content.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(_content.GetChild(i).gameObject);
                if (!string.IsNullOrEmpty(Message)) Line(_content, Message, UiFactory.SizeSmall, UiTheme.Warning);
                Message = null;
                await Build(_content);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        protected abstract System.Threading.Tasks.Task Build(RectTransform content);

        protected Text Line(Transform parent, string text, int size = UiFactory.SizeBody, Color? color = null) =>
            Ui.Label(parent, text, size, TextAnchor.MiddleLeft, color ?? UiTheme.Text);

        protected RectTransform RowOf(Transform parent)
        {
            RectTransform row = Ui.Row(parent, "Row", 12);
            UiFactory.Prefer(row.gameObject, -1, Ui.Scaled(UiFactory.SizeBody) + 40);
            return row;
        }
    }
}
