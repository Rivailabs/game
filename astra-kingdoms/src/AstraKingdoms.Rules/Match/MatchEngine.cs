using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;

namespace AstraKingdoms.Rules.Match
{
    /// <summary>
    /// The authoritative eight-round AK-TR-1 match (tickets 2, 9, 16, 19, 20).
    /// <para>
    /// <b>Flow.</b> Setup (both loadouts) → per round: TerrainAnnounce → (Selection → Resolution)
    /// × up to three volleys → CardAndCut after a won duel (skipped after a draw) → next round, until
    /// a player holds at least 45,936 cells right after a cut, round eight finishes, a player
    /// forfeits by two consecutive selection timeouts, or both do at once (void).
    /// </para>
    /// <para>
    /// <b>Commands.</b> Players call <see cref="Submit"/> with their authenticated side and a typed
    /// command (<see cref="SubmitLoadoutCommand"/>, <see cref="LockInputCommand"/>,
    /// <see cref="SubmitCutCommand"/>); the host calls <see cref="Advance"/> when a phase timer ends.
    /// Every command names the phase snapshot revision it acted on; the revision changes only on a
    /// phase transition, so both players' locks against the same published selection succeed. A
    /// request ID repeated with an identical canonical payload returns the original receipt; with a
    /// different payload it is rejected. Rejections never change state. All public members are
    /// thread-safe (one lock), so concurrent duplicates mutate state at most once.
    /// </para>
    /// <para>
    /// <b>Determinism.</b> No clock and no System.Random: initiative, terrain and cards use the
    /// seeded SHA-256 streams; identical config, seed, match ID and command log reproduce every
    /// state hash (see <c>Replayer</c>).
    /// </para>
    /// </summary>
    public sealed class MatchEngine
    {
        private sealed class Accepted
        {
            public PlayerSide? Sender;
            public byte[] Payload;
            public CommandReceipt Receipt;
        }

        private sealed class HistoryEntry
        {
            public int Round, Volley;
            public PlayerSide Defender;
            public TerrainType Terrain;
            public VolleyResult Result;
            public bool TimeoutA, TimeoutB;
            public VolleyInput InputA, InputB;
        }

        /// <summary>One accepted command with its authenticated sender (null = server).</summary>
        public sealed class LoggedCommand
        {
            public PlayerSide? Sender { get; }
            public MatchCommand Command { get; }

            internal LoggedCommand(PlayerSide? sender, MatchCommand command)
            {
                Sender = sender;
                Command = command;
            }
        }

        private readonly object _gate = new object();
        private readonly byte[] _seed;
        private readonly byte[] _rulesHash;
        private readonly Territory _territory;
        private readonly Loadout[] _loadouts = new Loadout[2];
        private readonly VolleyInput[] _locks = new VolleyInput[2];
        private readonly int[] _timeoutStreak = new int[2];
        private readonly bool[] _brahmastraAvailable = { true, true };
        private readonly Dictionary<string, Accepted> _byRequestId = new Dictionary<string, Accepted>(StringComparer.Ordinal);
        private readonly List<LoggedCommand> _commands = new List<LoggedCommand>();
        private readonly List<RoundRecord> _rounds = new List<RoundRecord>();
        private readonly List<HistoryEntry> _history = new List<HistoryEntry>();
        private readonly List<MatchEvent> _events = new List<MatchEvent>();

        private Duel _duel;
        private FrontierSelection _frontier;
        private RoundRecord _round;
        private CardOffer _offer;
        private PlayerSide? _duelWinner;
        private int _hpDifference;

        public MatchConfig Config { get; }
        /// <summary>The balance snapshot pinned to this match for its whole life (ticket 24).</summary>
        public RulesParameters Parameters => Config.Parameters;
        public string MatchId { get; }
        public byte[] RulesHash => (byte[])_rulesHash.Clone();
        public byte[] SeedCommitment { get; }
        public uint InitiativeStreamCounter { get; }
        public PlayerSide FirstAttacker { get; }

        public MatchPhase Phase { get; private set; }
        public ulong StateRevision { get; private set; }
        public ulong MapRevision => (ulong)_territory.Revision;
        /// <summary>One-based round; 0 during Setup.</summary>
        public int RoundIndex { get; private set; }
        /// <summary>The current duel's next volley to resolve (the last one once the duel is over); 0 in Setup.</summary>
        public int VolleyIndex => _duel == null ? 0 : _duel.CurrentVolley;
        public MatchResult Result { get; private set; }
        public bool IsOver => Phase == MatchPhase.MatchOver;

        public PlayerSide Attacker => AttackerOf(RoundIndex == 0 ? 1 : RoundIndex);
        public PlayerSide Defender => Board.Opponent(Attacker);
        public int Cells(PlayerSide side) => _territory.CellCount(side);

        /// <summary>The duel of the current round (null in Setup).</summary>
        public Duel CurrentDuel => _duel;
        public FrontierSelection CurrentFrontier => _frontier;

        private MatchEngine(MatchConfig config, byte[] seed, string matchId)
        {
            Config = config;
            MatchId = matchId;
            _seed = (byte[])seed.Clone();
            // The pinned snapshot's effective rules hash (RulesBundle.Hash for AK-TR-1).
            _rulesHash = config.Parameters.RulesHash;
            SeedCommitment = SeededStream.SeedCommitment(_rulesHash, _seed, matchId);
            _territory = Territory.CreateInitial(config.Template);

            SeededStream initiative = SeededStream.Initiative(_seed);
            FirstAttacker = initiative.NextIndex(2) == 0 ? PlayerSide.A : PlayerSide.B;
            InitiativeStreamCounter = initiative.Counter;

            Phase = MatchPhase.Setup;
            StateRevision = 1;
            Emit(MatchEventType.MatchCreated, null, (int)FirstAttacker, 0);
        }

        /// <summary>
        /// Creates a match in Setup. <paramref name="seed"/> is 32 bytes (kept secret until the match
        /// ends; <see cref="SeedCommitment"/> may be published immediately); <paramref name="matchId"/>
        /// is a canonical lowercase UUID.
        /// </summary>
        public static MatchEngine Create(MatchConfig config, byte[] seed, string matchId)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            config.Validate();
            if (seed == null || seed.Length != SeededStream.SeedLength) throw new ArgumentException("Seed must be 32 bytes.", nameof(seed));
            if (!SeededStream.IsCanonicalUuid(matchId)) throw new ArgumentException("Match ID must be a canonical lowercase UUID.", nameof(matchId));
            return new MatchEngine(config, seed, matchId);
        }

        /// <summary>Attacker of a round: the first attacker on odd rounds, the other player on even rounds.</summary>
        public PlayerSide AttackerOf(int round) => round % 2 == 1 ? FirstAttacker : Board.Opponent(FirstAttacker);

        /// <summary>The disclosed seed, available only after the match ends.</summary>
        public byte[] DisclosedSeed => IsOver ? (byte[])_seed.Clone() : null;

        public IReadOnlyList<MatchEvent> Events
        {
            get { lock (_gate) return _events.ToArray(); }
        }

        /// <summary>Public events after an acknowledged sequence number (reconnection).</summary>
        public IReadOnlyList<MatchEvent> EventsAfter(long acknowledgedSequence)
        {
            lock (_gate)
            {
                var list = new List<MatchEvent>();
                foreach (MatchEvent e in _events)
                    if (e.Sequence > acknowledgedSequence) list.Add(e);
                return list;
            }
        }

        public IReadOnlyList<LoggedCommand> CommandLog
        {
            get { lock (_gate) return _commands.ToArray(); }
        }

        public IReadOnlyList<RoundRecord> Rounds
        {
            get { lock (_gate) return _rounds.ToArray(); }
        }

        /// <summary>
        /// Full authoritative result of a resolved volley, including both choices and the event log
        /// for playback. Servers must not send it to a player while <see cref="IsConcealedFrom"/> is true.
        /// </summary>
        public VolleyResult GetVolleyResult(int round, int volley)
        {
            lock (_gate)
            {
                foreach (HistoryEntry h in _history)
                    if (h.Round == round && h.Volley == volley) return h.Result;
                return null;
            }
        }

        /// <summary>True when <paramref name="owner"/>'s choice in that volley is veiled from the opponent (until match end).</summary>
        public bool IsConcealedFrom(PlayerSide viewer, int round, int volley)
        {
            lock (_gate)
            {
                foreach (HistoryEntry h in _history)
                    if (h.Round == round && h.Volley == volley)
                        return !IsOver && h.Result.Explanation[Board.Opponent(viewer)].ConcealedFromOpponent;
                return false;
            }
        }

        // ------------------------------------------------------------------------------------
        // Commands
        // ------------------------------------------------------------------------------------

        /// <summary>Submits a player command. <paramref name="player"/> comes from the authenticated connection.</summary>
        public CommandReceipt Submit(PlayerSide player, MatchCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            if (command is AdvancePhaseCommand)
                return Reject(command, player, MatchErrors.WrongPhase, "AdvancePhase is server-only.");
            lock (_gate) return Handle(player, command);
        }

        /// <summary>Server timer: ends the current phase (see <see cref="AdvancePhaseCommand"/>).</summary>
        public CommandReceipt Advance(AdvancePhaseCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            lock (_gate) return Handle(null, command);
        }

        /// <summary>Convenience for hosts: an advance against the current snapshot.</summary>
        public AdvancePhaseCommand CreateAdvance(string requestId)
        {
            lock (_gate) return new AdvancePhaseCommand(new CommandHeader(_rulesHash, MatchId, requestId, RoundIndex, StateRevision));
        }

        private CommandReceipt Handle(PlayerSide? sender, MatchCommand cmd)
        {
            CommandHeader h = cmd.Header;
            if (h.SchemaVersion != CommandHeader.CurrentSchemaVersion) return Reject(cmd, sender, MatchErrors.SchemaVersion, "schema_version must be 1.");
            if (!Hashing.Equal(h.RulesHash, _rulesHash)) return Reject(cmd, sender, MatchErrors.RulesHash, "Rules hash does not match this match.");
            if (h.MatchId != MatchId) return Reject(cmd, sender, MatchErrors.MatchId, "Unknown match.");
            if (!SeededStream.IsCanonicalUuid(h.RequestId)) return Reject(cmd, sender, MatchErrors.RequestId, "request_id must be a canonical UUID.");

            byte[] payload = cmd.CanonicalBytes();
            if (_byRequestId.TryGetValue(h.RequestId, out Accepted prior))
            {
                if (prior.Sender == sender && Hashing.Equal(prior.Payload, payload)) return prior.Receipt;
                return Reject(cmd, sender, MatchErrors.RequestIdReused, "request_id was already used with a different payload.");
            }

            if (Phase == MatchPhase.MatchOver) return Reject(cmd, sender, MatchErrors.MatchOver, "The match has ended.");
            if (h.RoundIndex != RoundIndex) return Reject(cmd, sender, MatchErrors.WrongRound, "Round " + h.RoundIndex + " is not current (" + RoundIndex + ").");
            if (h.ExpectedStateRevision != StateRevision)
                return Reject(cmd, sender, MatchErrors.StaleStateRevision, "Revision " + h.ExpectedStateRevision + " is not current (" + StateRevision + ").");

            ulong acceptedRevision = StateRevision;
            int round = RoundIndex;
            int volley = VolleyIndex;
            string code;
            string message;
            int cells = 0;
            switch (cmd)
            {
                case SubmitLoadoutCommand lo: code = ApplyLoadout(sender.Value, lo, out message); break;
                case LockInputCommand li: code = ApplyLock(sender.Value, li, out message); break;
                case SubmitCutCommand cut: code = ApplyCut(sender.Value, cut, out message, out cells); break;
                case AdvancePhaseCommand adv: code = ApplyAdvance(adv, out message); break;
                default: code = MatchErrors.WrongPhase; message = "Unknown command."; break;
            }
            if (code != null) return Reject(cmd, sender, code, message);

            var receipt = new CommandReceipt(true, null, null, MatchId, h.RequestId, cmd.Kind, sender, round, volley,
                acceptedRevision, (ulong)_commands.Count, (byte[])_rulesHash.Clone(), cells, MapRevision);
            _byRequestId[h.RequestId] = new Accepted { Sender = sender, Payload = payload, Receipt = receipt };
            return receipt;
        }

        private CommandReceipt Reject(MatchCommand cmd, PlayerSide? sender, string code, string message) =>
            new CommandReceipt(false, code, message, MatchId, cmd.Header.RequestId, cmd.Kind, sender, RoundIndex, VolleyIndex,
                StateRevision, 0, (byte[])_rulesHash.Clone(), 0, MapRevision);

        private void Log(PlayerSide? sender, MatchCommand cmd) => _commands.Add(new LoggedCommand(sender, cmd));

        // ---- Setup ----

        private string ApplyLoadout(PlayerSide player, SubmitLoadoutCommand cmd, out string message)
        {
            message = null;
            if (Phase != MatchPhase.Setup) { message = "Loadouts are submitted during Setup."; return MatchErrors.WrongPhase; }
            if (_loadouts[(int)player] != null) { message = "Loadout already submitted."; return MatchErrors.LoadoutAlreadySubmitted; }
            Loadout loadout;
            try
            {
                loadout = Loadout.Create(Config.Catalog, cmd.Weapons, cmd.Reserve);
            }
            catch (RulesViolationException e)
            {
                message = e.Message;
                return e.Code;
            }
            Log(player, cmd);
            _loadouts[(int)player] = loadout;
            Emit(MatchEventType.LoadoutSubmitted, player, 0, 0);
            if (_loadouts[0] != null && _loadouts[1] != null) BeginRound(1);
            return null;
        }

        // ---- Selection ----

        private string ApplyLock(PlayerSide player, LockInputCommand cmd, out string message)
        {
            message = null;
            if (Phase != MatchPhase.Selection) { message = "No selection is open."; return MatchErrors.WrongPhase; }
            if (_locks[(int)player] != null) { message = "This volley is already locked; it cannot be replaced."; return MatchErrors.AlreadyLocked; }
            if (!Enum.IsDefined(typeof(Dodge), cmd.Dodge)) { message = "Unknown dodge."; return InputValidator.DodgeInvalid; }
            VolleyInput choice = cmd.ToChoice();
            try
            {
                InputValidator.ValidateLock(_duel.State, player, new LockInput(cmd.VolleyIndex, choice));
            }
            catch (RulesViolationException e)
            {
                message = e.Message;
                return e.Code;
            }
            Log(player, cmd);
            _locks[(int)player] = choice;
            _timeoutStreak[(int)player] = 0; // a valid lock resets the match-level streak
            Emit(MatchEventType.PlayerLocked, player, 0, 0);
            if (_locks[0] != null && _locks[1] != null) ResolveVolley(false, false);
            return null;
        }

        private void ResolveVolley(bool timeoutA, bool timeoutB)
        {
            VolleyInput a = _locks[0] ?? VolleyInput.Pass();
            VolleyInput b = _locks[1] ?? VolleyInput.Pass();
            int volley = _duel.CurrentVolley;
            VolleyResult result = _duel.Resolve(a, b);
            _brahmastraAvailable[0] = result.NewState.A.BrahmastraAvailable;
            _brahmastraAvailable[1] = result.NewState.B.BrahmastraAvailable;

            _history.Add(new HistoryEntry
            {
                Round = RoundIndex, Volley = volley, Defender = Defender, Terrain = _frontier.Terrain, Result = result,
                TimeoutA = timeoutA, TimeoutB = timeoutB, InputA = a, InputB = b,
            });
            _round.Volleys.Add(new VolleyRecord
            {
                Round = RoundIndex, Volley = volley, WeaponA = a.WeaponId, WeaponB = b.WeaponId, TimeoutA = timeoutA, TimeoutB = timeoutB,
                HpA = result.NewState.A.HpUnits, HpB = result.NewState.B.HpUnits, ResultAfter = result.NewState.Result,
                LogHashHex = Hex.Encode(Hashing.Sha256(result.Log.ToCanonicalBytes())),
            });
            SetPhase(MatchPhase.Resolution);
            Emit(MatchEventType.VolleyResolved, null, result.NewState.A.HpUnits, result.NewState.B.HpUnits, volley);
        }

        // ---- Card and cut ----

        private string ApplyCut(PlayerSide player, SubmitCutCommand cmd, out string message, out int cellsMoved)
        {
            message = null;
            cellsMoved = 0;
            if (Phase != MatchPhase.CardAndCut) { message = "No cut window is open."; return MatchErrors.WrongPhase; }
            if (_duelWinner != player) { message = "Only the duel winner may cut."; return MatchErrors.NotDuelWinner; }
            if (cmd.ExpectedMapRevision != MapRevision) { message = "Map revision is not current."; return MatchErrors.StaleMapRevision; }
            if (!_offer.Contains(cmd.CardId)) { message = "Card " + cmd.CardId + " was not offered."; return MatchErrors.CardNotOffered; }
            if (!Board.IsValidCellId(cmd.AnchorCellId)) { message = "anchor_cell_id must be 0-65535."; return MatchErrors.AnchorRange; }
            if (cmd.Mode != CutMode.Manual && cmd.Mode != CutMode.Auto) { message = "Unknown cut mode."; return MatchErrors.CutModeInvalid; }
            if (cmd.Mode == CutMode.Auto && cmd.Vertices.Count != 0) { message = "Auto Cut has no stroke vertices."; return MatchErrors.AutoHasVertices; }

            CutResult cut = EvaluateCut(player, cmd);
            if (!cut.IsAccepted) { message = cut.ToString(); return MatchErrors.CutPrefix + cut.Rejection; }

            Log(player, cmd);
            TransferOutcome outcome = LandTransfer.Apply(_territory, cut);
            if (!outcome.Applied) throw new InvalidOperationException("Validated cut failed to apply: " + outcome.Failure);
            cellsMoved = outcome.CellsTransferred;
            _round.CellsTransferred = cellsMoved;
            Emit(MatchEventType.CutApplied, player, cellsMoved, (int)cmd.CardId);
            if (_territory.HasReachedVictory(player, Parameters.VictoryCells))
            {
                FinishRoundRecord();
                EndMatch(TerminalReason.Territory90, player, null);
            }
            else
            {
                EndRound();
            }
            return null;
        }

        private CutResult EvaluateCut(PlayerSide player, SubmitCutCommand cmd)
        {
            int quota = LandQuota.Compute(_territory.CellCount(Board.Opponent(player)), _hpDifference, cmd.CardId, Parameters);
            CellPoint anchor = CellPoint.FromCellId(cmd.AnchorCellId);
            return cmd.Mode == CutMode.Auto
                ? CutValidator.AutoCut(_territory, player, cmd.CardId, cmd.Pose, anchor, quota)
                : CutValidator.Validate(_territory, player, cmd.CardId, cmd.Pose, anchor, cmd.Vertices, quota);
        }

        /// <summary>
        /// Previews a cut exactly as <see cref="Submit"/> would evaluate it, without mutating anything.
        /// Returns null when no cut by <paramref name="player"/> with that card is possible now.
        /// </summary>
        public CutResult PreviewCut(PlayerSide player, SubmitCutCommand cmd)
        {
            if (cmd == null) throw new ArgumentNullException(nameof(cmd));
            lock (_gate)
            {
                if (Phase != MatchPhase.CardAndCut || _duelWinner != player || !_offer.Contains(cmd.CardId)) return null;
                if (!Board.IsValidCellId(cmd.AnchorCellId)) return null;
                return EvaluateCut(player, cmd);
            }
        }

        /// <summary>Allowance Q for an offered card in the open cut window (0 otherwise).</summary>
        public int QuotaFor(CardId card)
        {
            lock (_gate)
            {
                if (Phase != MatchPhase.CardAndCut || !_duelWinner.HasValue || !_offer.Contains(card)) return 0;
                return LandQuota.Compute(_territory.CellCount(Board.Opponent(_duelWinner.Value)), _hpDifference, card, Parameters);
            }
        }

        // ---- Phase timers ----

        private string ApplyAdvance(AdvancePhaseCommand cmd, out string message)
        {
            message = null;
            switch (Phase)
            {
                case MatchPhase.Setup:
                    message = "Setup ends when both loadouts are submitted.";
                    return MatchErrors.WrongPhase;
                case MatchPhase.TerrainAnnounce:
                    Log(null, cmd);
                    OpenSelection();
                    return null;
                case MatchPhase.Selection:
                    Log(null, cmd);
                    SelectionDeadline();
                    return null;
                case MatchPhase.Resolution:
                    Log(null, cmd);
                    if (!_duel.IsOver) OpenSelection();
                    else FinishDuel();
                    return null;
                case MatchPhase.CardAndCut:
                    Log(null, cmd);
                    // A cut timeout transfers zero cells; it is not a selection timeout.
                    _round.CutTimedOut = true;
                    Emit(MatchEventType.CutTimedOut, _duelWinner, 0, 0);
                    EndRound();
                    return null;
                default:
                    message = "The match has ended.";
                    return MatchErrors.MatchOver;
            }
        }

        private void SelectionDeadline()
        {
            bool timeoutA = _locks[0] == null;
            bool timeoutB = _locks[1] == null;
            if (timeoutA) _timeoutStreak[0]++;
            if (timeoutB) _timeoutStreak[1]++;
            if (timeoutA) Emit(MatchEventType.SelectionTimeout, PlayerSide.A, _timeoutStreak[0], 0);
            if (timeoutB) Emit(MatchEventType.SelectionTimeout, PlayerSide.B, _timeoutStreak[1], 0);

            bool forfeitA = _timeoutStreak[0] >= RulesConstants.ConsecutiveTimeoutsToForfeit;
            bool forfeitB = _timeoutStreak[1] >= RulesConstants.ConsecutiveTimeoutsToForfeit;
            if (forfeitA && forfeitB)
            {
                FinishRoundRecord();
                EndMatch(TerminalReason.Void, null, null);
                return;
            }
            if (forfeitA || forfeitB)
            {
                PlayerSide loser = forfeitA ? PlayerSide.A : PlayerSide.B;
                FinishRoundRecord();
                EndMatch(TerminalReason.Forfeit, Board.Opponent(loser), loser);
                return;
            }
            ResolveVolley(timeoutA, timeoutB);
        }

        // ---- Round lifecycle ----

        private void BeginRound(int round)
        {
            RoundIndex = round;
            PlayerSide attacker = AttackerOf(round);
            PlayerSide defender = Board.Opponent(attacker);
            _frontier = Frontier.SelectDuelTerrain(_territory, attacker, _seed, round, Parameters.MaxRounds);
            _duel = Duel.Start(round, _frontier.Terrain, defender, _loadouts[0], _loadouts[1], Parameters, Config.BrahmastraEnabled,
                _brahmastraAvailable[0], _brahmastraAvailable[1]);
            _offer = null;
            _duelWinner = null;
            _hpDifference = 0;
            _round = new RoundRecord
            {
                Round = round, Attacker = attacker, FrontierCellId = _frontier.CellId, Terrain = _frontier.Terrain,
                FrontierCount = _frontier.FrontierCount, TerrainStreamCounter = _frontier.StreamCounter,
            };
            _rounds.Add(_round);
            SetPhase(MatchPhase.TerrainAnnounce);
            Emit(MatchEventType.RoundStarted, attacker, (int)_frontier.Terrain, _frontier.CellId);
        }

        private void OpenSelection()
        {
            _locks[0] = null;
            _locks[1] = null;
            SetPhase(MatchPhase.Selection);
            Emit(MatchEventType.SelectionOpened, null, 0, 0);
        }

        private void FinishDuel()
        {
            _duelWinner = _duel.Winner;
            _hpDifference = _duel.HpDifferenceUnits;
            _round.DuelResult = _duel.Result;
            _round.HpA = _duel.HpUnits(PlayerSide.A);
            _round.HpB = _duel.HpUnits(PlayerSide.B);
            _round.HpDifference = _hpDifference;
            Emit(MatchEventType.DuelEnded, _duelWinner, (int)_duel.Result, _hpDifference);

            if (!_duelWinner.HasValue)
            {
                EndRound(); // a draw offers no card and transfers nothing
                return;
            }
            _offer = Config.CardOffers == CardOfferRule.Pilot
                ? CardOffers.Pilot(_hpDifference)
                : CardOffers.V1(_seed, RoundIndex, _hpDifference, Parameters);
            _round.OfferedCards.AddRange(_offer.Cards);
            _round.CardsStreamCounter = _offer.StreamCounter;
            SetPhase(MatchPhase.CardAndCut);
            Emit(MatchEventType.CardsOffered, _duelWinner, _offer.Cards.Count, _hpDifference);
        }

        private void EndRound()
        {
            FinishRoundRecord();
            Emit(MatchEventType.RoundEnded, null, _territory.CellCount(PlayerSide.A), _territory.CellCount(PlayerSide.B));
            if (RoundIndex >= Parameters.MaxRounds)
            {
                int a = _territory.CellCount(PlayerSide.A);
                int b = _territory.CellCount(PlayerSide.B);
                PlayerSide? winner = a > b ? PlayerSide.A : b > a ? PlayerSide.B : (PlayerSide?)null;
                EndMatch(TerminalReason.RoundsComplete, winner, null);
            }
            else
            {
                BeginRound(RoundIndex + 1);
            }
        }

        private void FinishRoundRecord()
        {
            if (_round == null) return;
            _territory.CheckInvariants();
            if (_duel != null && _round.DuelResult == DuelResult.InProgress)
            {
                _round.HpA = _duel.HpUnits(PlayerSide.A);
                _round.HpB = _duel.HpUnits(PlayerSide.B);
            }
            byte[] ownership = _territory.ComputeOwnershipHash();
            _round.CellsA = _territory.CellCount(PlayerSide.A);
            _round.CellsB = _territory.CellCount(PlayerSide.B);
            _round.MapRevision = MapRevision;
            _round.OwnershipHashHex = Hex.Encode(ownership);
            var w = new CanonicalWriter();
            w.Ascii("AK-TR-1/round-state").I32(_round.Round).Block(ownership).I32(_round.CellsA).I32(_round.CellsB)
             .U64(_round.MapRevision).U8((int)_round.DuelResult).I32(_round.HpA).I32(_round.HpB).I32(_round.CellsTransferred)
             .Bool(_round.CutTimedOut);
            _round.StateHashHex = Hex.Encode(w.Sha256());
        }

        private void EndMatch(TerminalReason reason, PlayerSide? winner, PlayerSide? forfeitedBy)
        {
            Result = new MatchResult(reason, winner, forfeitedBy, _territory.CellCount(PlayerSide.A), _territory.CellCount(PlayerSide.B), RoundIndex);
            SetPhase(MatchPhase.MatchOver);
            Emit(MatchEventType.MatchEnded, winner, (int)reason, forfeitedBy.HasValue ? (int)forfeitedBy.Value + 1 : 0);
        }

        private void SetPhase(MatchPhase phase)
        {
            Phase = phase;
            StateRevision++;
        }

        private void Emit(MatchEventType type, PlayerSide? player, int amount, int amount2, int volley = -1) =>
            _events.Add(new MatchEvent(_events.Count + 1, type, RoundIndex, volley >= 0 ? volley : VolleyIndex, player, amount, amount2, StateRevision));

        // ------------------------------------------------------------------------------------
        // Private views
        // ------------------------------------------------------------------------------------

        /// <summary>Builds the private view of <paramref name="viewer"/>.</summary>
        public PlayerView GetView(PlayerSide viewer)
        {
            lock (_gate)
            {
                PlayerSide foe = Board.Opponent(viewer);
                var view = new PlayerView(() => { lock (_gate) return _territory.Clone(); })
                {
                    Viewer = viewer,
                    MatchId = MatchId,
                    Config = Config,
                    RulesHashBytes = _rulesHash,
                    SeedCommitmentHex = Hex.Encode(SeedCommitment),
                    SeedHex = IsOver ? Hex.Encode(_seed) : null,
                    Phase = Phase,
                    StateRevision = StateRevision,
                    MapRevision = MapRevision,
                    RoundIndex = RoundIndex,
                    VolleyIndex = VolleyIndex,
                    VolleysResolved = _history.Count,
                    FirstAttacker = FirstAttacker,
                    Attacker = Attacker,
                    DuelTerrain = _duel == null ? TerrainType.Plain : _frontier.Terrain,
                    FrontierCellId = _duel == null ? -1 : _frontier.CellId,
                    FortCoverActive = _duel != null && _duel.State.FortCoverActive,
                    ForestChargeAvailable = _duel != null && _duel.State.ForestChargeAvailable,
                    OwnLoadout = _loadouts[(int)viewer],
                    OpponentLoadoutSubmitted = _loadouts[(int)foe] != null,
                    OpponentLoadoutRevealed = IsOver ? _loadouts[(int)foe] : null,
                    ReserveEligible = Phase == MatchPhase.Selection && _duel.State.ReserveEligible(viewer),
                    OwnLock = Phase == MatchPhase.Selection ? _locks[(int)viewer] : null,
                    OpponentLocked = Phase == MatchPhase.Selection && _locks[(int)foe] != null,
                    Self = Status(viewer),
                    Foe = Status(foe),
                    CellsA = _territory.CellCount(PlayerSide.A),
                    CellsB = _territory.CellCount(PlayerSide.B),
                    DuelWinner = _duelWinner,
                    HpDifferenceUnits = _hpDifference,
                    Result = Result,
                };
                if (Phase == MatchPhase.CardAndCut)
                {
                    view.OfferedCards = _offer.Cards;
                    var quotas = new int[_offer.Cards.Count];
                    int loserCells = _territory.CellCount(Board.Opponent(_duelWinner.Value));
                    for (int i = 0; i < quotas.Length; i++) quotas[i] = LandQuota.Compute(loserCells, _hpDifference, _offer.Cards[i], Parameters);
                    view.OfferedQuotas = quotas;
                }
                var history = new List<RevealedVolley>(_history.Count);
                foreach (HistoryEntry h in _history) history.Add(Reveal(h, viewer));
                view.History = history;
                return view;
            }
        }

        private PlayerStatus Status(PlayerSide side)
        {
            var s = new PlayerStatus
            {
                HpUnits = Parameters.StartHpUnits,
                BrahmastraAvailable = Config.BrahmastraEnabled && _brahmastraAvailable[(int)side],
                ConsecutiveTimeouts = _timeoutStreak[(int)side],
                Cells = _territory.CellCount(side),
            };
            if (_duel == null) return s;
            PlayerDuelState p = _duel.State[side];
            int n = _duel.State.VolleyIndex;
            s.HpUnits = p.HpUnits;
            if (!_duel.IsOver)
            {
                s.BurnDue = p.BurnDueVolley == n;
                s.ShockDue = p.ShockDueVolley == n;
                s.NetDue = p.NetDueVolley == n;
                s.QuakeDue = p.QuakeDueVolley == n;
                s.IronWallActive = p.IronWallActiveIn(n);
            }
            s.BaselineOffsetRightRaw = p.BaselineOffsetRight.Raw;
            return s;
        }

        private RevealedVolley Reveal(HistoryEntry h, PlayerSide viewer) => new RevealedVolley
        {
            Round = h.Round,
            Volley = h.Volley,
            Defender = h.Defender,
            Terrain = h.Terrain,
            A = RevealSide(h, PlayerSide.A, viewer),
            B = RevealSide(h, PlayerSide.B, viewer),
            ResultAfter = h.Result.Explanation.ResultAfter,
        };

        private RevealedChoice RevealSide(HistoryEntry h, PlayerSide side, PlayerSide viewer)
        {
            PlayerVolleyReport r = h.Result.Explanation[side];
            VolleyInput input = side == PlayerSide.A ? h.InputA : h.InputB;
            bool concealed = side != viewer && r.ConcealedFromOpponent && !IsOver;
            var c = new RevealedChoice
            {
                Side = side,
                Concealed = concealed,
                EffectiveDodge = r.EffectiveDodge,
                TimedOut = side == PlayerSide.A ? h.TimeoutA : h.TimeoutB,
                LandedHit = r.LandedHit,
                HpBeforeUnits = r.HpBeforeUnits,
                HpAfterUnits = r.HpAfterUnits,
                DirectDamageTakenUnits = r.DirectDamageUnits,
                BurnTakenUnits = r.BurnDamageUnits,
                HealedUnits = r.OceanHealUnits + r.RiverHealUnits,
            };
            if (concealed)
            {
                c.WeaponId = -1;
                c.Element = Element.Neutral;
            }
            else
            {
                c.WeaponId = input.WeaponId;
                c.Element = input.DefensiveElement;
                c.PitchQdeg = input.PitchQdeg;
                c.YawQdeg = input.YawQdeg;
                c.PowerPercent = input.PowerPercent;
                c.SubmittedDodge = input.Dodge;
            }
            return c;
        }

        // ------------------------------------------------------------------------------------
        // Record
        // ------------------------------------------------------------------------------------

        /// <summary>Seed bytes for the replay record (authoritative service only).</summary>
        internal byte[] SeedForRecord => (byte[])_seed.Clone();

        internal bool[] BrahmastraAvailability => (bool[])_brahmastraAvailable.Clone();
    }
}
