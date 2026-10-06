using System;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using AstraKingdoms.Rules.Match;
using UnityEngine;
using UnityEngine.UI;

namespace AstraKingdoms.Client.UI.Screens
{
    /// <summary>Match result: both final areas, the result and its reason, Rematch and Home.</summary>
    public sealed class ResultScreen : UiScreen
    {
        private readonly Text _title;
        private readonly Text _detail;
        private readonly BoardTexture _board = new BoardTexture();

        public event Action Rematch;
        public event Action Home;

        public ResultScreen(Services.ClientContext ctx, UiFactory ui, Transform parent) : base(ctx, ui, parent, "ResultScreen", UiTheme.Background)
        {
            RawImage map = ui.Raw(Root, "FinalBoard", _board.Texture);
            RectTransform mapRect = map.rectTransform;
            mapRect.anchorMin = new Vector2(0.04f, 0.5f);
            mapRect.anchorMax = new Vector2(0.04f, 0.5f);
            mapRect.pivot = new Vector2(0f, 0.5f);
            mapRect.sizeDelta = new Vector2(820, 820);
            mapRect.anchoredPosition = Vector2.zero;
            RectTransform col = ui.Column(Root, "Column", 18, 30);
            UiFactory.Region(col, 0.52f, 0.05f, 0.97f, 0.95f);
            ui.Label(col, T("result.title"), UiFactory.SizeLarge, TextAnchor.MiddleCenter, UiTheme.TextMuted);
            _title = ui.Label(col, string.Empty, UiFactory.SizeTitle, TextAnchor.MiddleCenter, UiTheme.Text);
            _detail = ui.Label(col, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleCenter, UiTheme.Text);
            UiFactory.Flexible(col);
            ui.Button(col, T("result.rematch"), () => Rematch?.Invoke(), UiTheme.ButtonPrimary, UiFactory.SizeLarge);
            ui.Button(col, T("result.home"), () => Home?.Invoke(), UiTheme.Button);
        }

        public void Open(MatchResult result, Territory finalTerritory)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (result.IsVoid) _title.text = T("result.void");
            else if (result.Winner.HasValue) _title.text = TF("result.winner", Ctx.PlayerName(result.Winner.Value));
            else _title.text = T("result.draw");

            string reason = result.Reason == TerminalReason.Forfeit && result.ForfeitedBy.HasValue
                ? TF("reason.Forfeit", Ctx.PlayerName(result.ForfeitedBy.Value))
                : T("reason." + result.Reason);
            _detail.text = TF("result.areas", Ctx.PlayerName(PlayerSide.A), result.CellsA, Ctx.PlayerName(PlayerSide.B), result.CellsB) + "\n" +
                           reason + "\n" + TF("result.rounds", result.RoundsPlayed);
            if (finalTerritory != null) _board.ShowOwnership(finalTerritory);
            Show();
        }
    }
}
