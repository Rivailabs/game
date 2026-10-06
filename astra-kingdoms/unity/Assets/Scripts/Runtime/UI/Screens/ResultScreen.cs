using System;
using AstraKingdoms.Client.Land;
using AstraKingdoms.Client.Match;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using AstraKingdoms.Rules.Match;
using UnityEngine;
using UnityEngine.UI;

namespace AstraKingdoms.Client.UI.Screens
{
    /// <summary>
    /// Match result (ticket 42): the final board with owner patterns, both final areas with
    /// reconciled percentages, the winner (or draw/void) and its reason, and a clear Rematch action
    /// that creates fresh state exactly once however often it is tapped (<see cref="RematchGuard"/>).
    /// </summary>
    public sealed class ResultScreen : UiScreen
    {
        private readonly Text _title;
        private readonly Text _detail;
        private readonly Button _rematch;
        private readonly BoardTexture _board = new BoardTexture();
        private readonly BoardOverlayTexture _overlay = new BoardOverlayTexture();
        private readonly RematchGuard _guard = new RematchGuard();

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
            RawImage overlay = ui.Raw(mapRect, "Overlay", _overlay.Texture);
            UiFactory.Stretch(overlay.rectTransform);
            RectTransform col = ui.Column(Root, "Column", 18, 30);
            UiFactory.Region(col, 0.52f, 0.05f, 0.97f, 0.95f);
            ui.Label(col, T("result.title"), UiFactory.SizeLarge, TextAnchor.MiddleCenter, UiTheme.TextMuted);
            _title = ui.Label(col, string.Empty, UiFactory.SizeTitle, TextAnchor.MiddleCenter, UiTheme.Text);
            _detail = ui.Label(col, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleCenter, UiTheme.Text);
            UiFactory.Flexible(col);
            _rematch = ui.Button(col, T("result.rematch"), OnRematch, UiTheme.ButtonPrimary, UiFactory.SizeLarge);
            ui.Button(col, T("result.home"), () => Home?.Invoke(), UiTheme.Button);
        }

        public void Open(MatchResult result, Territory finalTerritory, string matchId = null)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            ResultSummary s = ResultSummary.From(result, Ctx.PlayerName);
            _title.text = TF(s.Title.Key, s.Title.Args);
            LandTotals t = s.Totals;
            _detail.text = TF("result.detail",
                TF("result.areasPercent", Ctx.PlayerName(PlayerSide.A), t.CellsA, LandTotals.PercentText(t.PermilleA),
                    Ctx.PlayerName(PlayerSide.B), t.CellsB, LandTotals.PercentText(t.PermilleB)),
                TF(s.Reason.Key, s.Reason.Args), TF(s.Rounds.Key, s.Rounds.Args));
            if (finalTerritory != null)
            {
                _board.ShowOwnership(finalTerritory);
                _overlay.Show(finalTerritory, Ctx.Settings.ShowPatterns);
            }
            _guard.MatchFinished(matchId ?? Guid.NewGuid().ToString("D"));
            _rematch.interactable = true;
            Show();
        }

        private void OnRematch()
        {
            if (!_guard.TryRematch()) return; // a double tap must not create two matches
            _rematch.interactable = false;
            Rematch?.Invoke();
        }
    }
}
