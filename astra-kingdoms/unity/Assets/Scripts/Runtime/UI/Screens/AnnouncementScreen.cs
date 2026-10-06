using AstraKingdoms.Client.Match;
using UnityEngine;
using UnityEngine.UI;

namespace AstraKingdoms.Client.UI.Screens
{
    /// <summary>Terrain announcement before a duel (2 s): round, terrain and attacker.</summary>
    public sealed class AnnouncementScreen : UiScreen
    {
        private readonly Text _text;

        public AnnouncementScreen(Services.ClientContext ctx, UiFactory ui, Transform parent)
            : base(ctx, ui, parent, "AnnouncementScreen", new Color(0, 0, 0, 0), false)
        {
            RectTransform box = ui.Panel(Root, "Box", UiTheme.Panel, false);
            UiFactory.Region(box, 0.25f, 0.35f, 0.75f, 0.65f);
            _text = ui.Label(box, string.Empty, UiFactory.SizeLarge, TextAnchor.MiddleCenter, UiTheme.Text);
            UiFactory.Stretch(_text.rectTransform, 20, 20, 20, 20);
        }

        /// <summary>Shows round, terrain and attacker, plus the previous round's land outcome when given.</summary>
        public void Open(PublicSnapshot s, string previousLandMessage = null)
        {
            _text.text = (string.IsNullOrEmpty(previousLandMessage) ? string.Empty : previousLandMessage + "\n\n") + TF("announce.round", s.Round) + "\n" +
                         TF("announce.terrain", T("terrain." + s.Terrain.ToString().ToLowerInvariant())) + "\n" +
                         TF("announce.attacker", Ctx.PlayerName(s.Attacker));
            Show();
        }
    }
}
