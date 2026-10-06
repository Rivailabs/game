using System;
using System.Collections.Generic;
using AstraKingdoms.Client.Match;
using AstraKingdoms.Client.Online.Protocol;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.Client.Online
{
    /// <summary>
    /// Client side of one online match. It never computes an outcome: it decodes the private
    /// <see cref="PlayerView"/> the server sends to this player, maps the server phase to the
    /// <see cref="HostStage"/> screens already understand, counts down the server's deadline for
    /// display, and turns local actions into commands.
    /// <para>
    /// <b>Idempotent recovery (ticket 54).</b> Every command keeps its request ID until a receipt
    /// arrives. After a reconnection the client asks for a snapshot plus the events after its
    /// acknowledged sequence and re-sends pending commands with the same request IDs; the engine
    /// returns the original receipt for an identical duplicate, so a retry can never create a
    /// second action.
    /// </para>
    /// </summary>
    public sealed class OnlineMatchSession : IMatchSession
    {
        private readonly Func<JsonNode, bool> _send;
        private readonly Func<string> _newId;
        private readonly Dictionary<string, JsonNode> _pending = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        private readonly object _gate = new object();

        private PlayerView _view;
        private Territory _territory;
        private ulong _territoryRevision;
        private int _historySeen;
        private ulong _stageRevision;
        private bool _ended;
        private double _remaining;

        public OnlineMatchSession(MatchStartMessage start, Func<JsonNode, bool> send, Func<string> newRequestId = null)
        {
            Start = start ?? throw new ArgumentNullException(nameof(start));
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _newId = newRequestId ?? (() => Guid.NewGuid().ToString("D"));
        }

        public MatchStartMessage Start { get; }
        public string MatchId => Start.MatchId;
        public PlayerSide LocalSide => Start.Side;
        public bool OpponentIsBot => Start.OpponentIsBot;
        public string OpponentLabel => Start.OpponentLabel;
        public MatchRules Rules => Start.Rules;
        public MatchConfig Config => Start.Rules.Config;

        /// <summary>Latest decoded private view (null until the first update).</summary>
        public PlayerView View => _view;
        /// <summary>Highest public event sequence applied (sent back as the acknowledgement).</summary>
        public long LastSeq { get; private set; }
        public bool OpponentConnected { get; private set; }
        public HostStage Stage { get; private set; }
        public PlayerSide? StageSide { get; private set; }
        public double StageSecondsRemaining => Math.Max(0, _remaining);
        public bool PauseAllowed => false; // never pause an online opponent's clock
        public MatchResult Result => _view?.Result;
        /// <summary>Set by match.end: completed, forfeit, void or technical_void.</summary>
        public string Outcome { get; private set; }
        public string ResultId { get; private set; }
        public int RewardXp { get; private set; }
        public int RewardCoins { get; private set; }
        public bool IsOver => _ended;
        /// <summary>Commands sent but not yet answered.</summary>
        public int PendingCount
        {
            get { lock (_gate) return _pending.Count; }
        }

        public event Action<HostStage> StageChanged;
        public event Action<RevealedVolley> VolleyRevealed;
        public event Action<PlayerSide, int> CutApplied;
        public event Action<MatchResult> MatchEnded;
        public event Action<string> CommandRejected;
        /// <summary>The view changed (any field, including the opponent's ready flag).</summary>
        public event Action<PlayerView> ViewChanged;
        /// <summary>The client lost track of state (decode failure or missing map); a snapshot was requested.</summary>
        public event Action ResyncRequested;

        public bool IsLocalHuman(PlayerSide side) => side == LocalSide;
        public bool CanView(PlayerSide side) => side == LocalSide && _view != null;

        public PlayerView ViewFor(PlayerSide side)
        {
            if (!CanView(side)) throw new InvalidOperationException("Only the local player's view exists on this device.");
            return _view;
        }

        public Territory CloneTerritory()
        {
            if (_territory == null) throw new InvalidOperationException("No state received yet.");
            return _territory.Clone();
        }

        /// <summary>Secret-free snapshot built from the local view (only public fields are copied).</summary>
        public PublicSnapshot Snapshot()
        {
            PlayerView v = _view;
            if (v == null) return new PublicSnapshot { Stage = Stage };
            bool a = v.Viewer == PlayerSide.A;
            return new PublicSnapshot
            {
                Phase = v.Phase,
                Stage = Stage,
                StageSide = StageSide,
                StageSecondsRemaining = StageSecondsRemaining,
                Paused = false,
                Round = v.RoundIndex,
                Volley = v.VolleyIndex,
                Attacker = v.Attacker,
                Terrain = v.DuelTerrain,
                FrontierCellId = v.FrontierCellId,
                HpA = a ? v.Self.HpUnits : v.Foe.HpUnits,
                HpB = a ? v.Foe.HpUnits : v.Self.HpUnits,
                StatusA = a ? v.Self : v.Foe,
                StatusB = a ? v.Foe : v.Self,
                CellsA = v.CellsA,
                CellsB = v.CellsB,
                LockedA = a ? v.OwnLock != null : v.OpponentLocked,
                LockedB = a ? v.OpponentLocked : v.OwnLock != null,
                DuelWinner = v.DuelWinner,
                HpDifferenceUnits = v.HpDifferenceUnits,
                OfferedCards = v.OfferedCards,
                OfferedQuotas = v.OfferedQuotas,
                Result = v.Result,
            };
        }

        /// <summary>Exact local preview using the public map and the offered quota (the server re-validates).</summary>
        public CutResult PreviewCut(PlayerSide side, CardId card, CardPose pose, int anchorCellId, CutMode mode, IReadOnlyList<CellPoint> vertices)
        {
            PlayerView v = _view;
            if (v == null || side != LocalSide || !v.IsCutTurn) return null;
            int index = -1;
            for (int i = 0; i < v.OfferedCards.Count; i++)
                if (v.OfferedCards[i] == card) index = i;
            if (index < 0 || !Board.IsValidCellId(anchorCellId)) return null;
            int quota = v.OfferedQuotas[index];
            CellPoint anchor = CellPoint.FromCellId(anchorCellId);
            Territory t = CloneTerritory();
            return mode == CutMode.Auto
                ? CutValidator.AutoCut(t, side, card, pose, anchor, quota)
                : CutValidator.Validate(t, side, card, pose, anchor, vertices ?? Array.Empty<CellPoint>(), quota);
        }

        // ------------------------------------------------------------------ actions

        public void SubmitLoadout(PlayerSide side, IReadOnlyList<int> weapons, int reserve) =>
            SendCommand(side, v => new SubmitLoadoutCommand(v.NewHeader(_newId()), weapons, reserve));

        public void SubmitLock(PlayerSide side, VolleyInput choice)
        {
            if (choice == null) throw new ArgumentNullException(nameof(choice));
            SendCommand(side, v => new LockInputCommand(v.NewHeader(_newId()), v.VolleyIndex, choice));
        }

        public void SubmitCut(PlayerSide side, CardId card, CardPose pose, int anchorCellId, CutMode mode, IReadOnlyList<CellPoint> vertices) =>
            SendCommand(side, v => new SubmitCutCommand(v.NewHeader(_newId()), v.MapRevision, card, anchorCellId, pose.CenterX, pose.CenterY,
                pose.Rotation, pose.ScaleQuarters, mode, mode == CutMode.Auto ? null : vertices));

        /// <summary>Sends an already-built command (tests and bots); returns its request ID.</summary>
        public string SendCommand(MatchCommand command)
        {
            JsonNode msg = ClientMessages.MatchCommand(command.Header.RequestId, command);
            lock (_gate) _pending[command.Header.RequestId] = msg;
            _send(msg);
            return command.Header.RequestId;
        }

        private void SendCommand(PlayerSide side, Func<PlayerView, MatchCommand> build)
        {
            if (side != LocalSide) throw new InvalidOperationException("This device only acts for " + LocalSide + ".");
            PlayerView v = _view;
            if (v == null) throw new InvalidOperationException("No state received yet.");
            SendCommand(build(v));
        }

        public void Tick(double seconds)
        {
            if (seconds > 0) _remaining -= seconds;
        }

        // ------------------------------------------------------------------ server messages

        /// <summary>Asks for a snapshot plus the events after <see cref="LastSeq"/> (after reconnecting).</summary>
        public void RequestResume() => _send(ClientMessages.MatchResume(_newId(), MatchId, LastSeq));

        /// <summary>Re-sends every unanswered command with its original request ID (idempotent on the server).</summary>
        public void ResendPending()
        {
            List<JsonNode> copy;
            lock (_gate) copy = new List<JsonNode>(_pending.Values);
            foreach (JsonNode m in copy) _send(m);
        }

        public void OnUpdate(MatchUpdateMessage m)
        {
            if (m.MatchId != MatchId) return;
            PlayerView view;
            try
            {
                bool hasMap = m.View.OptString("ownership") != null;
                ulong mapRev = m.View["map_revision"].AsULong();
                if (!hasMap && (_territory == null || _territoryRevision != mapRev))
                {
                    Resync();
                    return;
                }
                view = PlayerViewCodec.FromJson(m.View, _territory);
                if (hasMap)
                {
                    _territory = view.CloneTerritory();
                    _territoryRevision = mapRev;
                }
            }
            catch (Exception e) when (e is FormatException || e is RulesViolationException || e is OverflowException)
            {
                Resync();
                return;
            }

            foreach (WireEvent e in m.Events)
            {
                if (e.Seq <= LastSeq) continue; // already applied (a resent snapshot or duplicate)
                LastSeq = e.Seq;
                if (e.Type == nameof(MatchEventType.CutApplied) && e.Side.HasValue) CutApplied?.Invoke(e.Side.Value, e.Amount);
            }
            if (m.LastSeq > LastSeq) LastSeq = m.LastSeq;
            _send(ClientMessages.MatchAck(MatchId, LastSeq));

            _view = view;
            OpponentConnected = m.OpponentConnected;
            if (m.DeadlineRemainingMs >= 0) _remaining = m.DeadlineRemainingMs / 1000.0;
            else _remaining = 0;

            for (int i = _historySeen; i < view.History.Count; i++) VolleyRevealed?.Invoke(view.History[i]);
            _historySeen = Math.Max(_historySeen, view.History.Count);

            UpdateStage(view);
            ViewChanged?.Invoke(view);
            if (view.Phase == MatchPhase.MatchOver) RaiseEnded(view.Result);
        }

        public void OnReceipt(ReceiptMessage r)
        {
            bool known;
            lock (_gate) known = r.RequestId != null && _pending.Remove(r.RequestId);
            if (!known) return;
            if (!r.Accepted) CommandRejected?.Invoke(r.Code);
        }

        /// <summary>A service error answering one of our commands (rid = request ID).</summary>
        public bool OnError(ErrorMessage e)
        {
            bool known;
            lock (_gate) known = e.Rid != null && _pending.Remove(e.Rid);
            if (known) CommandRejected?.Invoke(e.Code);
            return known;
        }

        public void OnEnd(MatchEndMessage m)
        {
            if (m.MatchId != MatchId) return;
            Outcome = m.Outcome;
            ResultId = m.ResultId;
            RewardXp = m.RewardXp;
            RewardCoins = m.RewardCoins;
            lock (_gate) _pending.Clear();
            if (Stage != HostStage.MatchOver)
            {
                Stage = HostStage.MatchOver;
                StageSide = null;
                StageChanged?.Invoke(Stage);
            }
            RaiseEnded(m.Result);
        }

        private void RaiseEnded(MatchResult result)
        {
            if (_ended) return;
            _ended = true;
            MatchEnded?.Invoke(result);
        }

        private void Resync()
        {
            ResyncRequested?.Invoke();
            RequestResume();
        }

        private void UpdateStage(PlayerView v)
        {
            HostStage stage;
            PlayerSide? side = null;
            switch (v.Phase)
            {
                case MatchPhase.Setup:
                    stage = v.OwnLoadout == null ? HostStage.LoadoutEntry : HostStage.None;
                    side = LocalSide;
                    break;
                case MatchPhase.TerrainAnnounce: stage = HostStage.TerrainAnnounce; break;
                case MatchPhase.Selection:
                    stage = HostStage.Entry; // both players choose concurrently; StageSide is always the local player
                    side = LocalSide;
                    break;
                case MatchPhase.Resolution: stage = HostStage.Resolution; break;
                case MatchPhase.CardAndCut:
                    stage = HostStage.CardAndCut;
                    side = v.DuelWinner;
                    break;
                default: stage = HostStage.MatchOver; break;
            }
            // Stage events fire once per published phase snapshot (plus the local loadout step).
            ulong key = v.StateRevision * 2 + (v.Phase == MatchPhase.Setup && v.OwnLoadout != null ? 1UL : 0UL);
            bool changed = key != _stageRevision || stage != Stage;
            _stageRevision = key;
            Stage = stage;
            StageSide = side;
            if (changed) StageChanged?.Invoke(stage);
        }
    }
}
