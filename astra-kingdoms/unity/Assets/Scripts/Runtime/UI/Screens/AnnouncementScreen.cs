using AstraKingdoms.Client.Land;
using AstraKingdoms.Client.Match;
using UnityEngine;
using UnityEngine.UI;

namespace AstraKingdoms.Client.UI.Screens
{
    /// <summary>
    /// Terrain announcement before a duel (2 s): round, terrain and attacker. After a cut it also plays
    /// the land transfer (ticket 40): the captured cells sweep outward from the anchor while a warrior
    /// marker jumps from the cutter's land to the centre of the capture; the last frame is exactly the
    /// engine's updated ownership. With reduced motion the board shows the final state at once.
    /// </summary>
    public sealed class AnnouncementScreen : UiScreen
    {
        private const float BoardSize = 400f;

        private readonly Text _text;
        private readonly BoardTexture _board = new BoardTexture();
        private readonly BoardOverlayTexture _overlay = new BoardOverlayTexture();
        private readonly RectTransform _boardRect;
        private readonly RectTransform _warrior;
        private LandTransferPlan _plan;
        private float _elapsed;
        private int _shownCount = -1;
        private bool _finalShown;

        public AnnouncementScreen(Services.ClientContext ctx, UiFactory ui, Transform parent)
            : base(ctx, ui, parent, "AnnouncementScreen", new Color(0, 0, 0, 0), false)
        {
            RectTransform box = ui.Panel(Root, "Box", UiTheme.Panel, false);
            UiFactory.Region(box, 0.25f, 0.35f, 0.75f, 0.65f);
            _text = ui.Label(box, string.Empty, UiFactory.SizeLarge, TextAnchor.MiddleCenter, UiTheme.Text);
            UiFactory.Stretch(_text.rectTransform, 20, 20, 20, 20);

            RawImage map = ui.Raw(Root, "TransferBoard", _board.Texture);
            _boardRect = map.rectTransform;
            _boardRect.anchorMin = new Vector2(0.5f, 0f);
            _boardRect.anchorMax = new Vector2(0.5f, 0f);
            _boardRect.pivot = new Vector2(0.5f, 0f);
            _boardRect.sizeDelta = new Vector2(BoardSize, BoardSize);
            _boardRect.anchoredPosition = new Vector2(0f, 20f);
            RawImage overlay = ui.Raw(_boardRect, "Overlay", _overlay.Texture);
            UiFactory.Stretch(overlay.rectTransform);
            _warrior = ui.Panel(_boardRect, "Warrior", UiTheme.ButtonSelected, false);
            _warrior.anchorMin = Vector2.zero;
            _warrior.anchorMax = Vector2.zero;
            _warrior.sizeDelta = new Vector2(22, 22);
            Text glyph = ui.Label(_warrior, "▲", UiFactory.SizeSmall, TextAnchor.MiddleCenter, UiTheme.TextDark);
            UiFactory.Stretch(glyph.rectTransform);
            _boardRect.gameObject.SetActive(false);
        }

        /// <summary>Shows round, terrain and attacker, plus the previous round's land outcome when given.</summary>
        public void Open(PublicSnapshot s, string previousLandMessage = null, LandTransferPlan transfer = null)
        {
            string announce = TF("announce.text", s.Round, T("terrain." + s.Terrain.ToString().ToLowerInvariant()), Ctx.PlayerName(s.Attacker));
            _text.text = string.IsNullOrEmpty(previousLandMessage) ? announce : TF("announce.withLand", previousLandMessage, announce);
            _plan = transfer;
            _elapsed = 0f;
            _shownCount = -1;
            _finalShown = false;
            _boardRect.gameObject.SetActive(transfer != null);
            if (transfer != null) Draw(Ctx.Settings.ReducedMotion ? 1.0 : 0.0);
            Show();
        }

        public override void Tick(float deltaSeconds)
        {
            if (_plan == null) return;
            _elapsed += deltaSeconds;
            double progress = Ctx.Settings.ReducedMotion ? 1.0 : Mathf.Clamp01(_elapsed / (float)_plan.Seconds);
            Draw(progress);
        }

        private void Draw(double progress)
        {
            int count = _plan.RevealedCount(progress);
            bool final = progress >= 1.0;
            if (count != _shownCount || (final && !_finalShown))
            {
                _shownCount = count;
                byte[] frame = _plan.Frame(progress);
                _board.ShowOwners(frame); // 256 x 256: cheap enough for every wave
                // The 1,024 px pattern/contour overlay is repainted only for the first and final frames.
                if (progress <= 0.0 || (final && !_finalShown)) _overlay.Show(frame, Ctx.Settings.ShowPatterns);
                _finalShown |= final;
            }
            _plan.Warrior(progress, out double x, out double y, out double height);
            float scale = BoardSize / Rules.Land.Board.Size;
            _warrior.anchoredPosition = new Vector2((float)x * scale, (Rules.Land.Board.Size - (float)y) * scale + (float)height * 40f);
            _warrior.gameObject.SetActive(progress < 1.0 || _plan.Cells.Count > 0);
        }
    }
}
