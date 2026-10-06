using System;
using System.Collections.Generic;
using AstraKingdoms.Client.Land;
using AstraKingdoms.Client.Match;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using UnityEngine;
using UnityEngine.UI;

namespace AstraKingdoms.Client.UI.Screens
{
    /// <summary>
    /// Card choice and cut (tickets 7-8 and 37-41). Shows exact totals (cells and reconciled
    /// percentages), owner patterns and the rendered border contour (so ownership reads without
    /// colour), terrain markers, every offered card with its allowance and the term of the quota
    /// formula that set it, the posed card envelope as the visible legal boundary, and the exact cells
    /// the engine's preview says would move. The envelope is placed by dragging and turned/resized
    /// with two fingers (or buttons); the cut is a finger-drawn loop captured by <see cref="CutGesture"/>
    /// (snapped with the rules' helper, at most 128 vertices) and previewed with
    /// <see cref="LocalMatchHost.PreviewCut"/>, which gives the specific rejection reason. Auto Cut is
    /// the accessible alternative. Nothing here decides legality or area.
    /// </summary>
    public sealed class LandScreen : UiScreen
    {
        private const float PreviewInterval = 0.12f;

        private readonly BoardTexture _board = new BoardTexture();
        private readonly BoardOverlayTexture _overlay = new BoardOverlayTexture();
        private readonly RawImage _image;
        private readonly Text _title;
        private readonly Text _timer;
        private readonly Text _ownership;
        private readonly Text _explain;
        private readonly Text _status;
        private readonly Text _hint;
        private readonly Text _legend;
        private readonly RectTransform _cardColumn;
        private readonly RectTransform _controls;
        private readonly Button _modeMove;
        private readonly Button _modeDraw;
        private readonly Button _confirm;
        private readonly List<Button> _cardButtons = new List<Button>();

        private readonly CutGesture _gesture = new CutGesture();
        private readonly Dictionary<int, Vector2> _pointers = new Dictionary<int, Vector2>();

        private LocalMatchHost _host;
        private PlayerSide _winner;
        private bool _interactive;
        private Territory _territory;
        private IReadOnlyList<CardId> _cards = Array.Empty<CardId>();
        private IReadOnlyList<int> _quotas = Array.Empty<int>();
        private CardChoiceExplanation _choices;
        private int _frontierCell = -1;
        private int _cardIndex = -1;
        private CardPose _pose;
        private bool _hasPose;
        private bool _drawMode;
        private bool _autoMode;
        private List<CellPoint> _polygon = new List<CellPoint>();
        private GestureHint _gestureHint;
        private int _anchor = -1;
        private CutResult _preview;
        private bool _dirty;
        private float _sincePreview;

        // Two-finger gesture state.
        private float _gestureAngle;
        private float _gestureDistance;
        private int _gestureRotation;
        private int _gestureScale;

        /// <summary>card, pose, anchor cell, mode, vertices (empty for Auto).</summary>
        public event Action<CardId, CardPose, int, CutMode, IReadOnlyList<CellPoint>> CutRequested;

        public LandScreen(Services.ClientContext ctx, UiFactory ui, Transform parent) : base(ctx, ui, parent, "LandScreen", UiTheme.Background)
        {
            _image = ui.Raw(Root, "Board", _board.Texture);
            RectTransform img = _image.rectTransform;
            img.anchorMin = new Vector2(0.02f, 0.5f);
            img.anchorMax = new Vector2(0.02f, 0.5f);
            img.pivot = new Vector2(0f, 0.5f);
            img.sizeDelta = new Vector2(980, 980);
            img.anchoredPosition = Vector2.zero;
            _image.raycastTarget = true;
            var input = _image.gameObject.AddComponent<BoardInput>();
            input.Down += OnDown;
            input.Moved += OnMoved;
            input.Up += OnUp;
            RawImage overlay = ui.Raw(_image.rectTransform, "Overlay", _overlay.Texture);
            UiFactory.Stretch(overlay.rectTransform);

            RectTransform col = ui.Column(Root, "Column", 6, 16);
            UiFactory.Region(col, 0.56f, 0.01f, 0.99f, 0.99f);
            _title = ui.Label(col, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleCenter, UiTheme.Text);
            _timer = ui.Label(col, string.Empty, UiFactory.SizeLarge, TextAnchor.MiddleCenter, UiTheme.Warning);
            _ownership = ui.Label(col, string.Empty, UiFactory.SizeSmall, TextAnchor.MiddleCenter, UiTheme.TextMuted);
            ui.Label(col, T("land.chooseCard"), UiFactory.SizeSmall, TextAnchor.MiddleLeft, UiTheme.TextMuted);
            _cardColumn = ui.Column(col, "Cards", 6, 0);
            _explain = ui.Label(col, string.Empty, UiFactory.SizeSmall, TextAnchor.MiddleLeft, UiTheme.TextMuted);
            _controls = ui.Column(col, "Controls", 6, 0);
            RectTransform modeRow = ui.Row(_controls, "Mode", 8);
            UiFactory.Prefer(modeRow.gameObject, -1, ui.Scaled(UiFactory.SizeBody) + 40);
            _modeMove = ui.Button(modeRow, T("land.modeMove"), () => SetDrawMode(false), UiTheme.Button);
            _modeDraw = ui.Button(modeRow, T("land.modeDraw"), () => SetDrawMode(true), UiTheme.Button);
            RectTransform poseRow = ui.Row(_controls, "Pose", 8);
            UiFactory.Prefer(poseRow.gameObject, -1, ui.Scaled(UiFactory.SizeSmall) + 40);
            ui.Button(poseRow, TF("land.withGlyph", "↺", T("land.rotateLeft")), () => Rotate(-1), UiTheme.Button, UiFactory.SizeSmall);
            ui.Button(poseRow, TF("land.withGlyph", "↻", T("land.rotateRight")), () => Rotate(1), UiTheme.Button, UiFactory.SizeSmall);
            ui.Button(poseRow, TF("land.withGlyph", "−", T("land.smaller")), () => Resize(0.8f), UiTheme.Button, UiFactory.SizeSmall);
            ui.Button(poseRow, TF("land.withGlyph", "+", T("land.bigger")), () => Resize(1.25f), UiTheme.Button, UiFactory.SizeSmall);
            RectTransform cutRow = ui.Row(_controls, "Cut", 8);
            UiFactory.Prefer(cutRow.gameObject, -1, ui.Scaled(UiFactory.SizeBody) + 40);
            ui.Button(cutRow, T("land.auto"), UseAutoCut, UiTheme.Button);
            ui.Button(cutRow, T("land.clear"), ClearCut, UiTheme.Button);
            _hint = ui.Label(col, string.Empty, UiFactory.SizeSmall, TextAnchor.MiddleLeft, UiTheme.TextMuted);
            _status = ui.Label(col, string.Empty, UiFactory.SizeBody, TextAnchor.MiddleLeft, UiTheme.Text);
            UiFactory.Flexible(col);
            _legend = ui.Label(col, string.Empty, UiFactory.SizeSmall, TextAnchor.MiddleLeft, UiTheme.TextMuted);
            _confirm = ui.Button(col, T("land.confirm"), Confirm, UiTheme.ButtonPrimary, UiFactory.SizeLarge);
        }

        public Texture2D BoardTextureForTests => _board.Texture;

        /// <summary>Fires when the player confirms a cut (tutorial hook).</summary>
        public event Action Confirmed;

        /// <summary>
        /// Opens the cut window. <paramref name="interactive"/> is false for spectators (a bot's cut or
        /// a player watching); they see the board, the cards' allowances and the countdown only.
        /// </summary>
        public void Open(LocalMatchHost host, PlayerSide winner, bool interactive, PublicSnapshot snapshot, int frontierCellId)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _winner = winner;
            _interactive = interactive;
            _territory = host.CloneTerritory();
            _cards = snapshot.OfferedCards;
            _quotas = snapshot.OfferedQuotas;
            _frontierCell = frontierCellId;
            _gesture.Clear();
            _polygon = new List<CellPoint>();
            _pointers.Clear();
            _preview = null;
            _anchor = -1;
            _autoMode = false;
            _hasPose = false;
            _cardIndex = -1;

            _title.text = interactive ? TF("land.title", Ctx.PlayerName(winner)) : TF("land.watch", Ctx.PlayerName(winner));
            LandTotals totals = LandTotals.From(snapshot.CellsA, snapshot.CellsB);
            _ownership.text = TF("land.totals", Ctx.PlayerName(PlayerSide.A), totals.CellsA, LandTotals.PercentText(totals.PermilleA),
                Ctx.PlayerName(PlayerSide.B), totals.CellsB, LandTotals.PercentText(totals.PermilleB));
            _overlay.Show(_territory, Ctx.Settings.ShowPatterns);
            _legend.text = TF("land.legend", Ctx.PlayerName(PlayerSide.A), Ctx.PlayerName(PlayerSide.B));

            PlayerSide loser = Board.Opponent(winner);
            _choices = CardChoices.Explain(_cards, _quotas, snapshot.HpDifferenceUnits, snapshot.Cells(loser));
            foreach (Button b in _cardButtons)
            {
                b.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(b.gameObject);
            }
            _cardButtons.Clear();
            for (int i = 0; i < _choices.Choices.Count; i++)
            {
                int index = i;
                CardChoice c = _choices.Choices[i];
                Button b = Ui.Button(_cardColumn, TF("land.card", T(c.NameKey), c.Quota, c.CapPercent), () => SelectCard(index), UiTheme.Button,
                    UiFactory.SizeSmall);
                b.interactable = interactive;
                _cardButtons.Add(b);
            }
            _explain.text = Explanation(snapshot.HpDifferenceUnits, snapshot.Cells(loser));
            _controls.gameObject.SetActive(interactive);
            _confirm.gameObject.SetActive(interactive);
            _image.raycastTarget = interactive;
            _status.text = string.Empty;
            _hint.text = string.Empty;
            if (interactive && _cards.Count > 0) SelectCard(0);
            else Recompute();
            Show();
        }

        /// <summary>One line per card: the formula term that set its allowance (ticket 38).</summary>
        private string Explanation(int hpDifferenceUnits, int loserCells)
        {
            var lines = new List<string> { TF("land.margin", Hp.Format(hpDifferenceUnits)) };
            foreach (CardChoice c in _choices.Choices)
                lines.Add(TF(c.ReasonKey, T(c.NameKey), c.CapPercent, c.Quota, loserCells));
            if (_choices.VajraLockedByMargin) lines.Add(T("land.vajraLocked"));
            return string.Join("\n", lines);
        }

        public void SetRemaining(double seconds) => _timer.text = TF("hud.time", UiFactory.Seconds(seconds));

        public void ShowRejection(string code) => _status.text = TF("select.rejected", code);

        public override void Tick(float deltaSeconds)
        {
            _sincePreview += deltaSeconds;
            if (_dirty && _sincePreview >= PreviewInterval) Recompute();
        }

        public override void Hide()
        {
            base.Hide();
            _host = null;
        }

        // ------------------------------------------------------------------ choices

        private void SelectCard(int index)
        {
            if (!_interactive || index < 0 || index >= _cards.Count) return;
            _cardIndex = index;
            for (int i = 0; i < _cardButtons.Count; i++)
                UiFactory.SetButtonColor(_cardButtons[i], i == index ? UiTheme.ButtonSelected : UiTheme.Button);
            _hasPose = CutAssist.DefaultPose(_territory, _winner, _cards[index], _quotas[index], _frontierCell, out _pose);
            if (_hasPose) _pose = new CardPose(_pose.CenterX, _pose.CenterY, CutAssist.ClampScale(_cards[index], _pose.Rotation, _quotas[index], _pose.ScaleQuarters), _pose.Rotation);
            Ctx.Audio?.Play(Audio.AudioCue.CardSelect);
            SetDrawMode(false);
            Recompute();
        }

        private void SetDrawMode(bool draw)
        {
            _drawMode = draw;
            UiFactory.SetButtonColor(_modeMove, draw ? UiTheme.Button : UiTheme.ButtonSelected);
            UiFactory.SetButtonColor(_modeDraw, draw ? UiTheme.ButtonSelected : UiTheme.Button);
            _hint.text = T(draw ? "land.hintDraw" : "land.hintMove");
        }

        private void Rotate(int steps)
        {
            if (!_hasPose) return;
            int r = ((_pose.Rotation + steps) % RulesConstants.RotationSteps + RulesConstants.RotationSteps) % RulesConstants.RotationSteps;
            SetPose(_pose.CenterX, _pose.CenterY, _pose.ScaleQuarters, r);
        }

        private void Resize(float factor)
        {
            if (!_hasPose) return;
            int s = Mathf.RoundToInt(_pose.ScaleQuarters * factor);
            if (s == _pose.ScaleQuarters) s += factor > 1f ? 1 : -1;
            SetPose(_pose.CenterX, _pose.CenterY, s, _pose.Rotation);
        }

        private void SetPose(int cx, int cy, int scale, int rotation)
        {
            if (_cardIndex < 0) return;
            int clamped = CutAssist.ClampScale(_cards[_cardIndex], rotation, _quotas[_cardIndex], scale);
            _pose = new CardPose(Mathf.Clamp(cx, 0, Board.Size - 1), Mathf.Clamp(cy, 0, Board.Size - 1), clamped, rotation);
            _hasPose = true;
            _dirty = true;
        }

        private void UseAutoCut()
        {
            if (_cardIndex < 0) return;
            _autoMode = true;
            _gesture.Clear();
            _polygon = new List<CellPoint>();
            Recompute();
        }

        private void ClearCut()
        {
            _autoMode = false;
            _gesture.Clear();
            _polygon = new List<CellPoint>();
            _gestureHint = GestureHint.None;
            Recompute();
        }

        private void Confirm()
        {
            if (_cardIndex < 0 || _preview == null || !_preview.IsAccepted || _anchor < 0) return;
            Ctx.Audio?.Play(Audio.AudioCue.Lock);
            Confirmed?.Invoke();
            CutRequested?.Invoke(_cards[_cardIndex], _pose, _anchor, _autoMode ? CutMode.Auto : CutMode.Manual,
                _autoMode ? (IReadOnlyList<CellPoint>)Array.Empty<CellPoint>() : _polygon);
        }

        // ------------------------------------------------------------------ pointer gestures

        private void OnDown(int id, Vector2 screen)
        {
            if (!_interactive || _cardIndex < 0) return;
            _pointers[id] = screen;
            if (_drawMode)
            {
                if (_pointers.Count > 1) return;
                _autoMode = false;
                if (ToUv(screen, out double u, out double v)) _gesture.Begin(u, v);
                return;
            }
            if (_pointers.Count == 2) BeginTwoFinger();
            else MoveCentre(screen);
        }

        private void OnMoved(int id, Vector2 screen)
        {
            if (!_interactive || !_pointers.ContainsKey(id)) return;
            _pointers[id] = screen;
            if (_drawMode)
            {
                if (_gesture.Drawing && ToUv(screen, out double u, out double v) && _gesture.Add(u, v))
                {
                    _polygon = _gesture.Polygon(out _gestureHint);
                    _dirty = true;
                }
                return;
            }
            if (_pointers.Count >= 2) UpdateTwoFinger();
            else MoveCentre(screen);
        }

        private void OnUp(int id, Vector2 screen)
        {
            if (!_pointers.Remove(id)) return;
            if (_drawMode && _gesture.Drawing && _pointers.Count == 0)
            {
                ToUv(screen, out double u, out double v);
                _gesture.End(u, v);
                _polygon = _gesture.Polygon(out _gestureHint);
                Recompute();
            }
        }

        private void BeginTwoFinger()
        {
            GetTwo(out Vector2 a, out Vector2 b);
            _gestureAngle = Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg;
            _gestureDistance = Mathf.Max(1f, Vector2.Distance(a, b));
            _gestureRotation = _pose.Rotation;
            _gestureScale = _pose.ScaleQuarters;
        }

        private void UpdateTwoFinger()
        {
            GetTwo(out Vector2 a, out Vector2 b);
            float angle = Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg;
            float distance = Mathf.Max(1f, Vector2.Distance(a, b));
            // Screen angles are counter-clockwise (y up); rotation indices are clockwise on the y-down map.
            int steps = BoardMapping.RotationFromDegrees(-(angle - _gestureAngle));
            int rotation = (_gestureRotation + steps) % RulesConstants.RotationSteps;
            int scale = Mathf.RoundToInt(_gestureScale * distance / _gestureDistance);
            SetPose(_pose.CenterX, _pose.CenterY, scale, rotation);
        }

        private void GetTwo(out Vector2 a, out Vector2 b)
        {
            a = Vector2.zero;
            b = Vector2.zero;
            int n = 0;
            foreach (KeyValuePair<int, Vector2> kv in _pointers)
            {
                if (n == 0) a = kv.Value;
                else if (n == 1) b = kv.Value;
                n++;
            }
        }

        private void MoveCentre(Vector2 screen)
        {
            if (!ToUv(screen, out double u, out double v) || !_hasPose || u < 0 || u > 1 || v < 0 || v > 1) return;
            CellPoint cell = BoardMapping.Snap(u, v);
            SetPose(cell.X, cell.Y, _pose.ScaleQuarters, _pose.Rotation);
        }

        /// <summary>Screen point to normalized board-image coordinates (top-left origin); may lie outside 0..1.</summary>
        private bool ToUv(Vector2 screen, out double u, out double vDown)
        {
            u = vDown = -1;
            RectTransform rt = _image.rectTransform;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, screen, null, out Vector2 local)) return false;
            Rect r = rt.rect;
            u = (local.x - r.xMin) / r.width;
            vDown = 1.0 - (local.y - r.yMin) / r.height;
            return true;
        }

        // ------------------------------------------------------------------ preview

        private void Recompute()
        {
            _dirty = false;
            _sincePreview = 0;
            BoardRaster raster = _board.Raster;
            raster.PaintOwnership(_territory);
            _preview = null;
            _anchor = -1;
            string status = string.Empty;
            if (_interactive && _cardIndex >= 0 && _hasPose)
            {
                CardId card = _cards[_cardIndex];
                raster.OverlayEnvelope(CardEnvelope.RasterizeOnBoard(card, _pose));
                bool hasLoop = _polygon.Count >= 3;
                _anchor = CutAssist.PickAnchor(_territory, _winner, card, _pose, hasLoop ? _polygon : null);
                if (_anchor < 0)
                {
                    status = T("land.noAnchor");
                }
                else if (_autoMode || hasLoop)
                {
                    _preview = _host?.PreviewCut(_winner, card, _pose, _anchor, _autoMode ? CutMode.Auto : CutMode.Manual,
                        _autoMode ? null : _polygon);
                    if (_preview != null)
                    {
                        raster.OverlayCells(_preview.DiscardedCells, CellPaint.Discarded);
                        raster.OverlayCells(_preview.PreviewCells, _preview.IsAccepted ? CellPaint.Preview : CellPaint.Rejected);
                        status = _preview.IsAccepted
                            ? TF("land.preview", _preview.Cells.Count, _quotas[_cardIndex])
                            : T("cut.reject." + _preview.Rejection);
                        if (_preview.DiscardedCells.Count > 0)
                            status += "\n" + TF("land.previewDiscarded", _preview.DiscardedCells.Count, Ctx.PlayerName(Board.Opponent(_winner)));
                    }
                }
                else
                {
                    status = TF("land.allowance", _quotas[_cardIndex]);
                }
                if (!_autoMode && _polygon.Count > 0)
                {
                    raster.OverlayPolyline(_polygon, !_gesture.Drawing);
                    status += "\n" + TF("land.vertices", _polygon.Count);
                }
                string gestureKey = CutGesture.HintKey(_gestureHint);
                if (!_autoMode && gestureKey != null) status += "\n" + T(gestureKey);
                raster.OverlayCells(_anchor >= 0 ? new[] { _anchor } : null, CellPaint.Anchor);
            }
            _board.Upload();
            if (_interactive) _status.text = status;
            _confirm.interactable = _preview != null && _preview.IsAccepted && _preview.Cells.Count > 0;
        }
    }
}
