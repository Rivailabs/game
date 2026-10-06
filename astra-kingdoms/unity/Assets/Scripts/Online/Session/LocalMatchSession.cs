using System;
using System.Collections.Generic;
using AstraKingdoms.Client.Match;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Client.Online
{
    /// <summary>
    /// <see cref="IMatchSession"/> over the shared-phone/practice <see cref="LocalMatchHost"/>. It adds no
    /// behaviour: it exists so screens written against <see cref="IMatchSession"/> run identically for
    /// local and online matches (and so tests can drive both through one interface).
    /// </summary>
    public sealed class LocalMatchSession : IMatchSession
    {
        public LocalMatchHost Host { get; }

        public LocalMatchSession(LocalMatchHost host)
        {
            Host = host ?? throw new ArgumentNullException(nameof(host));
            host.StageChanged += s => StageChanged?.Invoke(s);
            host.CutApplied += (side, cells) => CutApplied?.Invoke(side, cells);
            host.MatchEnded += r => MatchEnded?.Invoke(r);
            host.CommandRejected += r => CommandRejected?.Invoke(r.RejectCode);
            host.VolleyResolved += OnResolved;
        }

        public MatchConfig Config => Host.Config;
        public HostStage Stage => Host.Stage;
        public PlayerSide? StageSide => Host.StageSide;
        public double StageSecondsRemaining => Host.StageSecondsRemaining;
        public bool PauseAllowed => Host.PauseAllowed;
        public MatchResult Result => Host.Engine.Result;

        public bool IsLocalHuman(PlayerSide side) => Host.Seat(side) == SeatKind.Human;
        public bool CanView(PlayerSide side) => Host.CanView(side);
        public PlayerView ViewFor(PlayerSide side) => Host.ViewFor(side);
        public PublicSnapshot Snapshot() => Host.Snapshot();
        public Territory CloneTerritory() => Host.CloneTerritory();

        public CutResult PreviewCut(PlayerSide side, CardId card, CardPose pose, int anchorCellId, CutMode mode, IReadOnlyList<CellPoint> vertices) =>
            Host.PreviewCut(side, card, pose, anchorCellId, mode, vertices);

        public void SubmitLoadout(PlayerSide side, IReadOnlyList<int> weapons, int reserve)
        {
            if (reserve != 0) throw new NotSupportedException("The local host submits Starter loadouts without a reserve.");
            Host.SubmitLoadout(side, weapons);
        }

        public void SubmitLock(PlayerSide side, VolleyInput choice) => Host.SubmitLock(side, choice);

        public void SubmitCut(PlayerSide side, CardId card, CardPose pose, int anchorCellId, CutMode mode, IReadOnlyList<CellPoint> vertices) =>
            Host.SubmitCut(side, card, pose, anchorCellId, mode, vertices);

        public void Tick(double seconds) => Host.Tick(seconds);

        public event Action<HostStage> StageChanged;
        public event Action<RevealedVolley> VolleyRevealed;
        public event Action<PlayerSide, int> CutApplied;
        public event Action<MatchResult> MatchEnded;
        public event Action<string> CommandRejected;

        private void OnResolved(ResolvedVolley v)
        {
            // The volley as the person watching may see it: through a human seat's view (Mist Veil applied
            // to the opponent). With two people on one phone, seat A's view is used, as the host does.
            PlayerSide watcher = Host.Seat(PlayerSide.A) == SeatKind.Human ? PlayerSide.A : PlayerSide.B;
            IReadOnlyList<RevealedVolley> history = Host.Engine.GetView(watcher).History;
            if (history.Count > 0) VolleyRevealed?.Invoke(history[history.Count - 1]);
        }
    }
}
