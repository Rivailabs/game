using System;
using System.Collections.Generic;
using System.Text;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Modes.FourPlayer
{
    /// <summary>Room configuration of a four-player match: the symmetric catalog every entrant receives.</summary>
    public sealed class FourPlayerConfig
    {
        public CatalogPreset Catalog { get; }

        public FourPlayerConfig(CatalogPreset catalog)
        {
            if (!Enum.IsDefined(typeof(CatalogPreset), catalog)) throw new ArgumentOutOfRangeException(nameof(catalog));
            Catalog = catalog;
        }

        public static FourPlayerConfig Starter => new FourPlayerConfig(CatalogPreset.Starter);
        public static FourPlayerConfig Full => new FourPlayerConfig(CatalogPreset.Full);

        /// <summary>
        /// Duel terrain. The candidate uses Plain for every pair: the V1 Full map is mirrored only
        /// across x, so it cannot give four equal sectors. A four-way terrain template is an open
        /// geometry fixture.
        /// </summary>
        public TerrainType DuelTerrain => TerrainType.Plain;

        public override string ToString() => FourPlayerRules.RulesId + " " + Catalog;
    }

    /// <summary>
    /// The authoritative PROPOSED four-player territory match, ruleset <see cref="FourPlayerRules.RulesId"/>
    /// (plan: "Proposed four player territory mode"). It is a candidate gameplay specification, not an
    /// implementation-ready network design.
    /// <para>
    /// <b>Flow.</b> Setup (four private loadouts) → waves 1..6. At wave start the board is frozen and
    /// the pairs come from <see cref="WavePairing"/>. Each pair plays the unchanged AK-TR-1
    /// up-to-three-volley <see cref="Duel"/> (round index = wave, Plain terrain, the pair's second
    /// label as the terrain defender). A pair winner gets a three-card offer and cuts only the
    /// defeated opponent's cells on the frozen board (<see cref="FourPlayerCutRules"/>); a draw
    /// transfers nothing and still completes the pair. When every pair is done the wave settles:
    /// the (disjoint) transfers apply together, forfeited land becomes locked neutral, and players at
    /// zero land or forfeited are eliminated. One survivor wins immediately; otherwise after wave six
    /// the most land wins and identical areas share placement.
    /// </para>
    /// <para>
    /// <b>Secrets.</b> Locks are stored privately until both of a pair have locked (or the deadline
    /// passes); no event or view other than the locker's own reveals them. Timeouts follow AK-TR-1:
    /// a missing lock becomes Pass and two consecutive timeouts forfeit. No bot ever replaces an
    /// absent participant.
    /// </para>
    /// <para>All public members are thread-safe (one lock).</para>
    /// </summary>
    public sealed class FourPlayerMatch
    {
        private sealed class KingdomState
        {
            public Loadout Loadout;
            public int TimeoutStreak;
            public int CompletedDuels;
            public bool HadByeLastWave;
            public bool Forfeited;
            public bool LandLocked;
            public int EliminatedWave; // 0 = alive
            public EliminationReason Reason;
            public bool Alive => Reason == EliminationReason.None;
        }

        private sealed class PairState
        {
            public int Slot;
            public WavePair Pair;
            public Duel Duel;
            public readonly VolleyInput[] Locks = new VolleyInput[2];
            public readonly List<VolleyInput[]> Inputs = new List<VolleyInput[]>();
            public PairStage Stage;
            public Kingdom? Winner;
            public int HpDifference;
            public List<CardId> Cards = new List<CardId>();
            public int[] Quotas = Array.Empty<int>();
            public FourPlayerCutResult Cut;
            public bool EndedByForfeit;
            public bool CutTimedOut;

            public PlayerSide SideOf(Kingdom k) => k == Pair.First ? PlayerSide.A : PlayerSide.B;
            public Kingdom KingdomOf(PlayerSide s) => s == PlayerSide.A ? Pair.First : Pair.Second;
        }

        private readonly object _gate = new object();
        private readonly byte[] _seed;
        private readonly KingdomState[] _k = new KingdomState[FourPlayerRules.Seats];
        private readonly FourOwnerTerritory _territory;
        private readonly List<FourPlayerCommand> _log = new List<FourPlayerCommand>();
        private readonly List<FourPlayerEvent> _events = new List<FourPlayerEvent>();
        private readonly List<FourPlayerWaveRecord> _waves = new List<FourPlayerWaveRecord>();
        private readonly Dictionary<int, byte[]> _settledBoards = new Dictionary<int, byte[]>();

        private FourOwnerTerritory _frozen;
        private WavePlan _plan;
        private List<PairState> _pairs = new List<PairState>();
        private List<FourPlayerStanding> _standings;

        public FourPlayerConfig Config { get; }
        public string MatchId { get; }
        public LabelAssignment Labels { get; }
        public string RulesId => FourPlayerRules.RulesId;
        public byte[] SeedCommitment => (byte[])Labels.Commitment.Clone();
        public FourPlayerPhase Phase { get; private set; }

        /// <summary>The wave in progress (or the last one once finished); 0 in Setup.</summary>
        public int Wave { get; private set; }

        /// <summary>The last fully settled wave (0 before the first settlement).</summary>
        public int SettledWave { get; private set; }

        public bool IsFinished => Phase == FourPlayerPhase.Finished;

        private FourPlayerMatch(FourPlayerConfig config, IReadOnlyList<string> entrants, byte[] seed, string matchId,
            FourOwnerTerritory initial)
        {
            _territory = initial;
            Config = config;
            MatchId = matchId;
            _seed = (byte[])seed.Clone();
            Labels = LabelAssignment.Create(entrants, seed, matchId);
            for (int i = 0; i < _k.Length; i++) _k[i] = new KingdomState();
            Phase = FourPlayerPhase.Setup;
            _settledBoards[0] = _territory.ToOwnerBytes();
            var sb = new StringBuilder();
            for (int i = 0; i < FourPlayerRules.Seats; i++)
                sb.Append((Kingdom)i).Append('=').Append(Labels.EntrantOf((Kingdom)i)).Append(i < 3 ? "," : string.Empty);
            Emit(FourPlayerEventType.MatchCreated, 0, -1, null, null, 0, 0, 0, sb + ";commit=" + Labels.CommitmentHex);
        }

        /// <summary>Creates a match in Setup. The seed stays secret until the match finishes.</summary>
        public static FourPlayerMatch Create(FourPlayerConfig config, IReadOnlyList<string> entrants, byte[] seed, string matchId)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            return new FourPlayerMatch(config, entrants, seed, matchId, FourOwnerTerritory.CreateEqualSectors());
        }

        /// <summary>
        /// Geometry-fixture hook for tests only: starts from a prepared board instead of the equal
        /// sectors (e.g. a nearly eliminated kingdom). Not reachable from production code.
        /// </summary>
        internal static FourPlayerMatch CreateWithBoard(FourPlayerConfig config, IReadOnlyList<string> entrants, byte[] seed,
            string matchId, FourOwnerTerritory board)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (board == null) throw new ArgumentNullException(nameof(board));
            board.CheckInvariants();
            return new FourPlayerMatch(config, entrants, seed, matchId, board.Clone());
        }

        /// <summary>Disclosed only after the match finishes.</summary>
        public byte[] DisclosedSeed
        {
            get { lock (_gate) return IsFinished ? (byte[])_seed.Clone() : null; }
        }

        public int Cells(Kingdom k)
        {
            lock (_gate) return _territory.CellCount(k);
        }

        public int NeutralCells
        {
            get { lock (_gate) return _territory.NeutralCellCount; }
        }

        public bool IsAlive(Kingdom k)
        {
            lock (_gate) return _k[(int)k].Alive;
        }

        public int CompletedDuels(Kingdom k)
        {
            lock (_gate) return _k[(int)k].CompletedDuels;
        }

        public WavePlan CurrentPlan
        {
            get { lock (_gate) return _plan; }
        }

        public PairStage StageOf(int pairSlot)
        {
            lock (_gate) return _pairs[pairSlot].Stage;
        }

        public IReadOnlyList<FourPlayerCommand> CommandLog
        {
            get { lock (_gate) return _log.ToArray(); }
        }

        public IReadOnlyList<FourPlayerEvent> PublicEvents
        {
            get { lock (_gate) return _events.ToArray(); }
        }

        public IReadOnlyList<FourPlayerWaveRecord> WaveRecords
        {
            get { lock (_gate) return _waves.ToArray(); }
        }

        /// <summary>Final standings, or null until the match finishes.</summary>
        public IReadOnlyList<FourPlayerStanding> Standings
        {
            get { lock (_gate) return _standings?.ToArray(); }
        }

        /// <summary>Public owner bytes after a settled wave (wave 0 = the starting sectors).</summary>
        public byte[] SettledBoard(int wave)
        {
            lock (_gate) return _settledBoards.TryGetValue(wave, out byte[] b) ? (byte[])b.Clone() : null;
        }

        /// <summary>A copy of the live territory (tests and invariant checks).</summary>
        public FourOwnerTerritory CloneTerritory()
        {
            lock (_gate) return _territory.Clone();
        }

        // ------------------------------------------------------------------ commands

        public ModeReceipt Submit(FourPlayerCommand command)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));
            lock (_gate)
            {
                ModeReceipt r = Dispatch(command);
                if (r.Accepted)
                {
                    _log.Add(command);
                    AdvanceIfReady();
                }
                return r;
            }
        }

        private ModeReceipt Dispatch(FourPlayerCommand c)
        {
            if (Phase == FourPlayerPhase.Finished) return ModeReceipt.Reject("MATCH_OVER", "The match has finished.");
            switch (c)
            {
                case SubmitLoadout4P l: return OnLoadout(l);
                case Lock4P l: return OnLock(l);
                case SubmitCut4P s: return OnCut(s);
                case Forfeit4P f: return OnForfeit(f);
                case ExpireSetup4P _: return OnExpireSetup();
                case ExpireSelection4P e: return OnExpireSelection(e);
                case ExpireCut4P e: return OnExpireCut(e);
                default: return ModeReceipt.Reject("UNKNOWN_COMMAND", c.GetType().Name);
            }
        }

        private ModeReceipt OnLoadout(SubmitLoadout4P c)
        {
            if (Phase != FourPlayerPhase.Setup) return ModeReceipt.Reject("WRONG_PHASE", "Loadouts are submitted in Setup.");
            KingdomState k = _k[(int)c.Sender.Value];
            if (k.Forfeited) return ModeReceipt.Reject("FORFEITED", "This entrant has left the match.");
            if (k.Loadout != null) return ModeReceipt.Reject("ALREADY_SUBMITTED", "The loadout is already locked.");
            try
            {
                k.Loadout = Loadout.Create(Config.Catalog, c.Weapons, c.Reserve);
            }
            catch (RulesViolationException e)
            {
                return ModeReceipt.Reject(e.Code, e.Message);
            }
            return ModeReceipt.Ok;
        }

        private ModeReceipt OnLock(Lock4P c)
        {
            if (Phase != FourPlayerPhase.Wave) return ModeReceipt.Reject("WRONG_PHASE", "No wave is in progress.");
            if (c.Wave != Wave) return ModeReceipt.Reject("WRONG_WAVE", "Lock targets wave " + c.Wave + " but wave " + Wave + " is open.");
            Kingdom sender = c.Sender.Value;
            PairState p = PairOf(sender);
            if (p == null) return ModeReceipt.Reject("NOT_IN_DUEL", sender + " has no duel this wave.");
            if (p.Stage != PairStage.Selection) return ModeReceipt.Reject("WRONG_STAGE", "The pair is not selecting.");
            PlayerSide side = p.SideOf(sender);
            if (p.Locks[(int)side] != null) return ModeReceipt.Reject("ALREADY_LOCKED", "A choice is locked once per volley.");
            try
            {
                p.Duel.ValidateLock(side, new LockInput(c.Volley, c.Choice));
            }
            catch (RulesViolationException e)
            {
                return ModeReceipt.Reject(e.Code, e.Message);
            }
            p.Locks[(int)side] = c.Choice;
            if (p.Locks[0] != null && p.Locks[1] != null) ResolveVolley(p, false, false);
            return ModeReceipt.Ok;
        }

        private ModeReceipt OnExpireSelection(ExpireSelection4P c)
        {
            if (Phase != FourPlayerPhase.Wave || c.Wave != Wave) return ModeReceipt.Reject("WRONG_WAVE", "No such wave in progress.");
            if (c.PairSlot < 0 || c.PairSlot >= _pairs.Count) return ModeReceipt.Reject("NO_PAIR", "Unknown pair slot.");
            PairState p = _pairs[c.PairSlot];
            if (p.Stage != PairStage.Selection) return ModeReceipt.Reject("WRONG_STAGE", "The pair is not selecting.");
            ResolveVolley(p, p.Locks[0] == null, p.Locks[1] == null);
            return ModeReceipt.Ok;
        }

        private ModeReceipt OnCut(SubmitCut4P c)
        {
            if (Phase != FourPlayerPhase.Wave || c.Wave != Wave) return ModeReceipt.Reject("WRONG_WAVE", "No such wave in progress.");
            Kingdom sender = c.Sender.Value;
            PairState p = PairOf(sender);
            if (p == null || p.Stage != PairStage.Cut || p.Winner != sender)
                return ModeReceipt.Reject("NOT_CUT_TURN", sender + " has no open cut.");
            int index = p.Cards.IndexOf(c.Card);
            if (index < 0) return ModeReceipt.Reject("CARD_NOT_OFFERED", c.Card + " was not offered.");
            Kingdom loser = p.Pair.OpponentOf(sender);
            int quota = p.Quotas[index];
            FourPlayerCutResult result = c.Vertices == null
                ? FourPlayerCutRules.AutoCut(_frozen, sender, loser, c.Card, c.Pose, c.Anchor, quota)
                : FourPlayerCutRules.Validate(_frozen, sender, loser, c.Card, c.Pose, c.Anchor, c.Vertices, quota);
            if (!result.IsAccepted) return ModeReceipt.Reject("CUT_" + result.Rejection, "Cut rejected: " + result.Rejection);
            p.Cut = result;
            p.Stage = PairStage.Done;
            Emit(FourPlayerEventType.CutCommitted, Wave, p.Slot, sender, loser, 0, result.Cells.Count, quota, c.Card.ToString());
            return ModeReceipt.Ok;
        }

        private ModeReceipt OnExpireCut(ExpireCut4P c)
        {
            if (Phase != FourPlayerPhase.Wave || c.Wave != Wave) return ModeReceipt.Reject("WRONG_WAVE", "No such wave in progress.");
            if (c.PairSlot < 0 || c.PairSlot >= _pairs.Count) return ModeReceipt.Reject("NO_PAIR", "Unknown pair slot.");
            PairState p = _pairs[c.PairSlot];
            if (p.Stage != PairStage.Cut) return ModeReceipt.Reject("WRONG_STAGE", "The pair has no open cut.");
            p.Stage = PairStage.Done;
            p.CutTimedOut = true;
            Emit(FourPlayerEventType.CutTimedOut, Wave, p.Slot, p.Winner, null, 0, 0, 0, null);
            return ModeReceipt.Ok;
        }

        private ModeReceipt OnForfeit(Forfeit4P c)
        {
            Kingdom k = c.Sender.Value;
            KingdomState s = _k[(int)k];
            if (!s.Alive || s.Forfeited) return ModeReceipt.Reject("NOT_ACTIVE", k + " is no longer in the match.");
            MarkForfeit(k);
            return ModeReceipt.Ok;
        }

        private ModeReceipt OnExpireSetup()
        {
            if (Phase != FourPlayerPhase.Setup) return ModeReceipt.Reject("WRONG_PHASE", "Setup is over.");
            for (int i = 0; i < FourPlayerRules.Seats; i++)
                if (_k[i].Loadout == null && !_k[i].Forfeited) MarkForfeit((Kingdom)i);
            return ModeReceipt.Ok;
        }

        // ------------------------------------------------------------------ flow

        private void MarkForfeit(Kingdom k)
        {
            KingdomState s = _k[(int)k];
            s.Forfeited = true;
            Emit(FourPlayerEventType.PlayerForfeited, Wave, -1, k, null, 0, 0, 0, null);
            if (Phase == FourPlayerPhase.Setup)
            {
                // No wave is active, so nothing is frozen: lock the land at once.
                LockAndEliminate(k, -1);
                return;
            }
            PairState p = PairOf(k);
            if (p == null) return; // bye: locked at settlement
            if (p.Stage == PairStage.Selection)
            {
                // The duel is abandoned: nobody gains the forfeiter's land (it becomes neutral).
                p.Stage = PairStage.Done;
                p.EndedByForfeit = true;
                CompletePair(p);
            }
            else if (p.Stage == PairStage.Cut && p.Winner == k)
            {
                p.Stage = PairStage.Done;
                p.EndedByForfeit = true;
            }
            // A loser who forfeits after the duel keeps the winner's earned cut window open: the
            // earned transfer applies first at settlement, then the remainder becomes neutral.
        }

        private void ResolveVolley(PairState p, bool timeoutA, bool timeoutB)
        {
            VolleyInput a = timeoutA ? VolleyInput.Pass() : p.Locks[0];
            VolleyInput b = timeoutB ? VolleyInput.Pass() : p.Locks[1];
            int volley = p.Duel.CurrentVolley;
            VolleyResult result = p.Duel.Resolve(a, b);
            p.Inputs.Add(new[] { a, b });
            p.Locks[0] = p.Locks[1] = null;

            KingdomState first = _k[(int)p.Pair.First], second = _k[(int)p.Pair.Second];
            first.TimeoutStreak = timeoutA ? first.TimeoutStreak + 1 : 0;
            second.TimeoutStreak = timeoutB ? second.TimeoutStreak + 1 : 0;

            string Weapon(PlayerSide s, VolleyInput input) =>
                result.Explanation[s].ConcealedFromOpponent ? "?" : input.WeaponId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Emit(FourPlayerEventType.VolleyResolved, Wave, p.Slot, p.Pair.First, p.Pair.Second, volley,
                p.Duel.HpUnits(PlayerSide.A), p.Duel.HpUnits(PlayerSide.B),
                "w=" + Weapon(PlayerSide.A, a) + "|" + Weapon(PlayerSide.B, b) + ";t=" + (timeoutA ? 1 : 0) + (timeoutB ? 1 : 0));

            bool forfeitA = first.TimeoutStreak >= FourPlayerRules.ConsecutiveTimeoutsToForfeit;
            bool forfeitB = second.TimeoutStreak >= FourPlayerRules.ConsecutiveTimeoutsToForfeit;
            if (forfeitA || forfeitB)
            {
                if (forfeitA) MarkForfeit(p.Pair.First);
                if (forfeitB) MarkForfeit(p.Pair.Second);
                return;
            }
            if (p.Duel.IsOver) EndDuel(p);
        }

        private void EndDuel(PairState p)
        {
            CompletePair(p);
            if (p.Duel.Winner.HasValue)
            {
                Kingdom winner = p.KingdomOf(p.Duel.Winner.Value);
                Kingdom loser = p.Pair.OpponentOf(winner);
                p.Winner = winner;
                p.HpDifference = p.Duel.HpDifferenceUnits;
                var stream = new SeededStream(FourPlayerRules.CardStream, _seed, (uint)(Wave * 4 + p.Slot));
                IReadOnlyList<CardId> eligible = Rules.Core.Cards.Eligible(p.HpDifference);
                p.Cards = stream.DrawWithoutReplacement(eligible, Math.Min(FourPlayerRules.CardOfferSize, eligible.Count));
                int loserCells = _frozen.CellCount(loser);
                p.Quotas = new int[p.Cards.Count];
                for (int i = 0; i < p.Cards.Count; i++) p.Quotas[i] = FourPlayerCutRules.Allowance(loserCells, p.HpDifference, p.Cards[i]);
                p.Stage = loserCells > 0 ? PairStage.Cut : PairStage.Done;
                Emit(FourPlayerEventType.DuelEnded, Wave, p.Slot, winner, loser, 0, p.HpDifference, 0, p.Duel.Result.ToString());
            }
            else
            {
                p.Stage = PairStage.Done; // a draw transfers nothing but completes participation
                Emit(FourPlayerEventType.DuelEnded, Wave, p.Slot, p.Pair.First, p.Pair.Second, 0, 0, 0, p.Duel.Result.ToString());
            }
        }

        private void CompletePair(PairState p)
        {
            _k[(int)p.Pair.First].CompletedDuels++;
            _k[(int)p.Pair.Second].CompletedDuels++;
        }

        private void AdvanceIfReady()
        {
            if (Phase == FourPlayerPhase.Setup)
            {
                for (int i = 0; i < FourPlayerRules.Seats; i++)
                    if (_k[i].Alive && _k[i].Loadout == null) return;
                if (Survivors().Count <= 1) Finish();
                else StartWave(1);
                return;
            }
            while (Phase == FourPlayerPhase.Wave && AllPairsDone())
            {
                Settle();
                if (Phase == FourPlayerPhase.Wave && !AllPairsDone()) break;
            }
        }

        private bool AllPairsDone()
        {
            foreach (PairState p in _pairs)
                if (p.Stage != PairStage.Done) return false;
            return true;
        }

        private void StartWave(int wave)
        {
            Wave = wave;
            Phase = FourPlayerPhase.Wave;
            var histories = new List<SurvivorHistory>();
            foreach (Kingdom k in Survivors())
                histories.Add(new SurvivorHistory(k, _k[(int)k].CompletedDuels, _k[(int)k].HadByeLastWave));
            _plan = WavePairing.Plan(wave, histories);
            _frozen = _territory.Clone();
            _pairs = new List<PairState>();
            for (int i = 0; i < _plan.Pairs.Count; i++)
            {
                WavePair pair = _plan.Pairs[i];
                _pairs.Add(new PairState
                {
                    Slot = i,
                    Pair = pair,
                    Duel = Duel.Start(wave, Config.DuelTerrain, PlayerSide.B, _k[(int)pair.First].Loadout, _k[(int)pair.Second].Loadout),
                    Stage = PairStage.Selection,
                });
            }
            Emit(FourPlayerEventType.WaveStarted, wave, -1, _plan.Bye, null, 0, _plan.Pairs.Count, 0, _plan.ToString());

            // Anyone who forfeited while sitting out the previous settlement cannot be paired (they
            // were eliminated), so every pair here has two active players.
        }

        private void Settle()
        {
            if (_territory.Revision != _frozen.Revision) throw new InvalidOperationException("The board changed during a wave.");
            var record = new FourPlayerWaveRecord { Wave = Wave, Plan = _plan };
            var transfers = new List<CellTransfer>();
            foreach (PairState p in _pairs)
            {
                if (p.Cut != null && p.Cut.IsAccepted && p.Cut.Cells.Count > 0) transfers.Add(p.Cut.ToTransfer());
                record.Pairs.Add(new PairOutcome
                {
                    Slot = p.Slot,
                    Pair = p.Pair,
                    DuelResult = p.Duel.Result,
                    HpFirst = p.Duel.HpUnits(PlayerSide.A),
                    HpSecond = p.Duel.HpUnits(PlayerSide.B),
                    Winner = p.Winner,
                    HpDifference = p.HpDifference,
                    EndedByForfeit = p.EndedByForfeit,
                    CutTimedOut = p.CutTimedOut,
                    CellsTransferred = p.Cut != null ? p.Cut.Cells.Count : 0,
                });
            }

            // Disjoint transfers computed against the frozen board are applied together.
            _territory.ApplyTransfers(transfers);
            foreach (CellTransfer t in transfers)
                Emit(FourPlayerEventType.CutApplied, Wave, -1, t.To, t.From, 0, t.Cells.Count, 0, null);

            for (int i = 0; i < FourPlayerRules.Seats; i++)
            {
                KingdomState s = _k[i];
                if (s.Forfeited && !s.LandLocked) LockAndEliminate((Kingdom)i, Wave);
            }
            for (int i = 0; i < FourPlayerRules.Seats; i++)
            {
                KingdomState s = _k[i];
                if (s.Alive && _territory.CellCount((Kingdom)i) == 0)
                {
                    s.Reason = EliminationReason.NoLand;
                    s.EliminatedWave = Wave;
                    Emit(FourPlayerEventType.Eliminated, Wave, -1, (Kingdom)i, null, 0, 0, (int)EliminationReason.NoLand, null);
                }
            }
            for (int i = 0; i < FourPlayerRules.Seats; i++)
            {
                KingdomState s = _k[i];
                if (s.EliminatedWave == Wave) record.Eliminated.Add((Kingdom)i);
                s.HadByeLastWave = _plan.Bye.HasValue && (int)_plan.Bye.Value == i;
            }

            record.CellsAfter = new[]
            {
                _territory.CellCount(Kingdom.A), _territory.CellCount(Kingdom.B), _territory.CellCount(Kingdom.C),
                _territory.CellCount(Kingdom.D), _territory.NeutralCellCount,
            };
            record.OwnershipHashHex = Hex.Encode(_territory.ComputeOwnershipHash());
            record.StateHashHex = StateHash(record);
            _waves.Add(record);
            _settledBoards[Wave] = _territory.ToOwnerBytes();
            SettledWave = Wave;
            Emit(FourPlayerEventType.WaveSettled, Wave, -1, null, null, 0, 0, 0,
                "cells=" + string.Join(",", record.CellsAfter) + ";own=" + record.OwnershipHashHex);

            if (Survivors().Count <= 1 || Wave >= FourPlayerRules.MaxWaves) Finish();
            else StartWave(Wave + 1);
        }

        private void LockAndEliminate(Kingdom k, int wave)
        {
            KingdomState s = _k[(int)k];
            int locked = _territory.LockToNeutral(k);
            s.LandLocked = true;
            if (s.Alive)
            {
                s.Reason = EliminationReason.Forfeit;
                s.EliminatedWave = wave;
            }
            Emit(FourPlayerEventType.Eliminated, Math.Max(0, wave), -1, k, null, 0, locked, (int)EliminationReason.Forfeit, "neutral");
        }

        private void Finish()
        {
            Phase = FourPlayerPhase.Finished;
            _pairs = new List<PairState>();
            _standings = ComputeStandings();
            var sb = new StringBuilder();
            foreach (FourPlayerStanding s in _standings) sb.Append(s.Kingdom).Append(':').Append(s.Place).Append(',');
            Emit(FourPlayerEventType.MatchFinished, Wave, -1, null, null, 0, 0, 0, sb + "seed=" + Hex.Encode(_seed));
        }

        /// <summary>
        /// Survivors rank by owned cells (identical areas share a place). Eliminated kingdoms rank
        /// below every survivor, later elimination first; kingdoms eliminated in the same wave share a
        /// place (PROPOSED: no coin flip and no hidden tiebreak).
        /// </summary>
        private List<FourPlayerStanding> ComputeStandings()
        {
            var list = new List<FourPlayerStanding>();
            for (int i = 0; i < FourPlayerRules.Seats; i++)
            {
                KingdomState s = _k[i];
                list.Add(new FourPlayerStanding
                {
                    Kingdom = (Kingdom)i,
                    EntrantId = Labels.EntrantOf((Kingdom)i),
                    Cells = _territory.CellCount((Kingdom)i),
                    EliminatedWave = s.Alive ? 0 : s.EliminatedWave,
                    Reason = s.Reason,
                });
            }
            long Key(FourPlayerStanding s) => s.Reason == EliminationReason.None
                ? 1_000_000L + s.Cells
                : 100L + s.EliminatedWave; // −1 (setup forfeit) ranks lowest
            foreach (FourPlayerStanding s in list)
            {
                int better = 0;
                foreach (FourPlayerStanding o in list)
                    if (Key(o) > Key(s)) better++;
                s.Place = better + 1;
            }
            list.Sort((x, y) => x.Place != y.Place ? x.Place.CompareTo(y.Place) : x.Kingdom.CompareTo(y.Kingdom));
            return list;
        }

        private List<Kingdom> Survivors()
        {
            var list = new List<Kingdom>();
            for (int i = 0; i < FourPlayerRules.Seats; i++)
                if (_k[i].Alive) list.Add((Kingdom)i);
            return list;
        }

        private PairState PairOf(Kingdom k)
        {
            foreach (PairState p in _pairs)
                if (p.Pair.Contains(k)) return p;
            return null;
        }

        private string StateHash(FourPlayerWaveRecord r)
        {
            var w = new CanonicalWriter();
            w.Ascii(FourPlayerRules.RulesId).Ascii(MatchId).I32(r.Wave).Ascii(r.OwnershipHashHex);
            foreach (int c in r.CellsAfter) w.I32(c);
            foreach (PairOutcome p in r.Pairs)
                w.U8((int)p.Pair.First).U8((int)p.Pair.Second).U8((int)p.DuelResult).I32(p.HpFirst).I32(p.HpSecond).I32(p.CellsTransferred)
                 .Bool(p.EndedByForfeit).Bool(p.CutTimedOut);
            for (int i = 0; i < FourPlayerRules.Seats; i++) w.U8((int)_k[i].Reason).I32(_k[i].EliminatedWave).I32(_k[i].CompletedDuels);
            return Hex.Encode(w.Sha256());
        }

        private void Emit(FourPlayerEventType type, int wave, int slot, Kingdom? k, Kingdom? other, int volley, int amount, int amount2, string detail) =>
            _events.Add(new FourPlayerEvent(_events.Count + 1, type, wave, slot, k, other, volley, amount, amount2, detail));

        // ------------------------------------------------------------------ views

        /// <summary>The private view of <paramref name="viewer"/> (see <see cref="FourPlayerParticipantView"/>).</summary>
        public FourPlayerParticipantView GetView(Kingdom viewer)
        {
            lock (_gate)
            {
                KingdomState s = _k[(int)viewer];
                FourOwnerTerritory boardAtStart = (_frozen ?? _territory).Clone();
                var view = new FourPlayerParticipantView(() => { lock (_gate) return (_frozen ?? _territory).Clone(); })
                {
                    Viewer = viewer,
                    MatchId = MatchId,
                    RulesId = RulesId,
                    SeedCommitmentHex = Labels.CommitmentHex,
                    SeedHex = IsFinished ? Hex.Encode(_seed) : null,
                    Phase = Phase,
                    Wave = Wave,
                    Eliminated = !s.Alive,
                    OwnLoadout = s.Loadout,
                    Cells = new[]
                    {
                        boardAtStart.CellCount(Kingdom.A), boardAtStart.CellCount(Kingdom.B), boardAtStart.CellCount(Kingdom.C),
                        boardAtStart.CellCount(Kingdom.D), boardAtStart.NeutralCellCount,
                    },
                };
                PairState p = Phase == FourPlayerPhase.Wave ? PairOf(viewer) : null;
                view.IsSpectating = Phase == FourPlayerPhase.Wave && (p == null || !s.Alive);
                if (p == null || !s.Alive) return view;

                PlayerSide side = p.SideOf(viewer);
                PlayerSide foe = CombatGeometry.Opponent(side);
                PlayerDuelState own = p.Duel.State[side], opp = p.Duel.State[foe];
                int n = p.Duel.State.VolleyIndex;
                view.InDuel = true;
                view.PairSlot = p.Slot;
                view.Opponent = p.Pair.OpponentOf(viewer);
                view.DuelSide = side;
                view.Stage = p.Stage;
                view.Volley = p.Duel.CurrentVolley;
                view.OwnHp = own.HpUnits;
                view.OpponentHp = opp.HpUnits;
                view.OwnBaselineRaw = own.BaselineOffsetRight.Raw;
                view.OpponentBaselineRaw = opp.BaselineOffsetRight.Raw;
                view.OwnQuakeDue = !p.Duel.IsOver && own.QuakeDueVolley == n;
                view.OwnNetDue = !p.Duel.IsOver && own.NetDueVolley == n;
                view.OwnLock = p.Stage == PairStage.Selection ? p.Locks[(int)side] : null;
                view.OpponentLocked = p.Stage == PairStage.Selection && p.Locks[(int)foe] != null;
                var history = new List<ResolvedVolley4P>();
                for (int i = 0; i < p.Duel.Volleys.Count; i++)
                {
                    VolleyResult vr = p.Duel.Volleys[i];
                    VolleyInput[] inputs = p.Inputs[i];
                    history.Add(new ResolvedVolley4P
                    {
                        Volley = i + 1,
                        OwnWeaponId = inputs[(int)side].WeaponId,
                        OpponentWeaponId = vr.Explanation[foe].ConcealedFromOpponent ? -1 : inputs[(int)foe].WeaponId,
                        OpponentEffectiveDodge = vr.Explanation[foe].EffectiveDodge,
                        OwnHpAfter = vr.Explanation[side].HpAfterUnits,
                        OpponentHpAfter = vr.Explanation[foe].HpAfterUnits,
                    });
                }
                view.DuelHistory = history;
                if (p.Stage == PairStage.Cut && p.Winner == viewer)
                {
                    view.IsCutTurn = true;
                    view.OfferedCards = p.Cards.ToArray();
                    view.OfferedQuotas = (int[])p.Quotas.Clone();
                }
                return view;
            }
        }
    }
}
