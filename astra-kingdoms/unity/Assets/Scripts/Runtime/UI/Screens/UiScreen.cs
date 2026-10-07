using AstraKingdoms.Client.Services;
using UnityEngine;

namespace AstraKingdoms.Client.UI.Screens
{
    /// <summary>A full-screen (or overlay) panel built in code.</summary>
    public abstract class UiScreen
    {
        protected readonly ClientContext Ctx;
        protected readonly UiFactory Ui;

        public RectTransform Root { get; }
        public bool Visible => Root != null && Root.gameObject.activeSelf;

        protected UiScreen(ClientContext ctx, UiFactory ui, Transform parent, string name, Color background, bool blocksInput = true)
        {
            Ctx = ctx;
            Ui = ui;
            Root = ui.Panel(parent, name, background, blocksInput);
            UiFactory.Stretch(Root);
            Root.gameObject.SetActive(false);
        }

        public virtual void Show()
        {
            Root.gameObject.SetActive(true);
            Root.SetAsLastSibling();
        }

        public virtual void Hide() => Root.gameObject.SetActive(false);

        /// <summary>Called every frame while visible.</summary>
        public virtual void Tick(float deltaSeconds)
        {
        }

        protected string T(string key) => Ctx.T(key);
        protected string TF(string key, params object[] args) => Ctx.TF(key, args);
    }
}
