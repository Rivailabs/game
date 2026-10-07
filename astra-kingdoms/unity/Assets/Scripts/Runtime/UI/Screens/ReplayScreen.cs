using System;
using System.IO;
using AstraKingdoms.Client.Arena;
using AstraKingdoms.Client.Combat;
using AstraKingdoms.Client.Match;
using AstraKingdoms.Client.Replay;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;
using UnityEngine;
using UnityEngine.UI;

namespace AstraKingdoms.Client.UI.Screens
{
    /// <summary>
    /// Debug replay viewer (pilot: "deterministic replay for debugging"). Loads a MatchRecord JSON,
    /// re-executes it command by command through a fresh authoritative engine and plays every volley
    /// from the engine's own tracks; the board updates after each accepted cut. "Verify" runs the
    /// full <see cref="Replayer.Verify"/> regression check (state hashes and result).
    /// </summary>
    public sealed class ReplayScreen : UiScreen
    {
        private readonly ArenaView _arena;
        private readonly BoardTexture _board = new BoardTexture();
        private readonly Text _status;
        private readonly Text _explanation;
        private ReplayStepper _stepper;
        private MatchRecord _record;
        private bool _autoPlay;
        private float _wait;

        public event Action Closed;

        public ReplayScreen(Services.ClientContext ctx, UiFactory ui, Transform parent, ArenaView arena)
            : base(ctx, ui, parent, "ReplayScreen", new Color(0, 0, 0, 0), false)
        {
            _arena = arena;
            RectTransform panel = ui.Panel(Root, "Controls", UiTheme.Panel);
            UiFactory.Region(panel, 0f, 0f, 0.3f, 1f);
            RectTransform col = ui.Column(panel, "Column", 10, 16);
            UiFactory.Stretch(col);
            ui.Label(col, T("replay.title"), UiFactory.SizeLarge, TextAnchor.MiddleCenter, UiTheme.Text);
            ui.Button(col, T("replay.loadLatest"), () => Open(null), UiTheme.Button);
            ui.Button(col, T("replay.next"), () => StepOnce(), UiTheme.Button);
            ui.Button(col, T("replay.playAll"), () => _autoPlay = true, UiTheme.ButtonPrimary);
            ui.Button(col, T("replay.verify"), Verify, UiTheme.Button);
            _status = ui.Label(col, string.Empty, UiFactory.SizeSmall, TextAnchor.UpperLeft, UiTheme.Text);
            UiFactory.Flexible(col);
            ui.Button(col, T("common.back"), () =>
            {
                _autoPlay = false;
                Closed?.Invoke();
            }, UiTheme.Button);

            RawImage map = ui.Raw(Root, "Board", _board.Texture);
            RectTransform mr = map.rectTransform;
            mr.anchorMin = new Vector2(1f, 1f);
            mr.anchorMax = new Vector2(1f, 1f);
            mr.pivot = new Vector2(1f, 1f);
            mr.sizeDelta = new Vector2(420, 420);
            mr.anchoredPosition = new Vector2(-20, -20);

            RectTransform box = ui.Panel(Root, "Explanation", UiTheme.Background, false);
            UiFactory.Region(box, 0.31f, 0.01f, 0.99f, 0.3f);
            _explanation = ui.Label(box, string.Empty, UiFactory.SizeSmall, TextAnchor.UpperLeft, UiTheme.Text);
            UiFactory.Stretch(_explanation.rectTransform, 12, 8, 12, 8);
        }

        public static string DefaultPath => Path.Combine(MatchFlowPaths.RecordsDirectory, "latest-record.json");

        public void Open(string path)
        {
            Show();
            _autoPlay = false;
            _explanation.text = string.Empty;
            Ctx.PlayerName = side => Ctx.T(side == PlayerSide.A ? "player.one" : "player.two");
            string file = string.IsNullOrEmpty(path) ? DefaultPath : path;
            try
            {
                if (!File.Exists(file))
                {
                    _status.text = TF("replay.none", file);
                    _stepper = null;
                    return;
                }
                _record = MatchRecord.FromJson(File.ReadAllText(file));
                _stepper = new ReplayStepper(_record);
                _status.text = TF("replay.loaded", _stepper.Count, _record.RulesVersion);
                _board.ShowOwnership(_stepper.Engine.GetView(PlayerSide.A).CloneTerritory());
                _arena.ResetPoses(0, 0);
            }
            catch (Exception ex)
            {
                _stepper = null;
                _status.text = TF("error.generic", ex.Message);
            }
        }

        public override void Tick(float deltaSeconds)
        {
            if (!_autoPlay || _stepper == null) return;
            if (_arena.IsPlaying && !_arena.FlightFinished) return;
            _wait -= deltaSeconds;
            if (_wait > 0) return;
            if (!StepOnce()) _autoPlay = false;
        }

        private bool StepOnce()
        {
            if (_stepper == null || _stepper.Finished) return false;
            ReplayStep step;
            try
            {
                step = _stepper.Step();
            }
            catch (Exception ex)
            {
                _status.text = TF("error.generic", ex.Message);
                return false;
            }
            _status.text = TF("replay.step", step.Index + 1, _stepper.Count);
            _wait = 0.05f;
            if (step.ResolvedVolley != null)
            {
                _arena.Stop();
                var resolved = new ResolvedVolley { Round = step.ResolvedRound, Volley = step.ResolvedVolleyIndex, Result = step.ResolvedVolley };
                _arena.Play(resolved, 2.5f);
                var builder = new ExplanationBuilder(Ctx.Loc, Ctx.PlayerName);
                _explanation.text = string.Join("\n", builder.Build(step.ResolvedVolley.Explanation));
                _wait = 0.8f;
            }
            if (step.Kind == ReplayStepKind.Cut || step.PhaseAfter == MatchPhase.MatchOver)
                _board.ShowOwnership(_stepper.Engine.GetView(PlayerSide.A).CloneTerritory());
            return true;
        }

        private void Verify()
        {
            if (_record == null) return;
            ReplayReport report = Replayer.Verify(_record);
            _status.text = report.Success ? T("replay.verified") : TF("replay.failed", report.ToString());
        }
    }
}
