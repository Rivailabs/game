using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.Client.Match
{
    /// <summary>Who controls a seat on this device.</summary>
    public enum SeatKind : byte
    {
        Human = 0,
        /// <summary>A clearly labelled bot that sees only its own <see cref="PlayerView"/>.</summary>
        Bot = 1,
    }

    /// <summary>
    /// What the local host is waiting for. These refine the engine's <see cref="MatchPhase"/> for one
    /// phone: the engine owns rules and secrets; the host only sequences private entry and timers.
    /// </summary>
    public enum HostStage : byte
    {
        None = 0,
        /// <summary>Opaque privacy screen before a private loadout: "pass the phone to Player X".</summary>
        LoadoutReady = 1,
        /// <summary>A human enters a private loadout (untimed pre-match setup).</summary>
        LoadoutEntry = 2,
        /// <summary>Terrain announcement, 2 s.</summary>
        TerrainAnnounce = 3,
        /// <summary>Privacy screen before the first private entry of a volley; the entrant's 12 s are already running.</summary>
        EntryReady = 4,
        /// <summary>A human enters their private volley choice (12 s per player).</summary>
        Entry = 5,
        /// <summary>The one opaque handover between the two entries (at most 6 s).</summary>
        Handover = 6,
        /// <summary>Resolution replay, at most 2.5 s.</summary>
        Resolution = 7,
        /// <summary>Card choice, pose and cut by the duel winner (20 s shared-phone).</summary>
        CardAndCut = 8,
        MatchOver = 9,
    }

    /// <summary>Timer values. Defaults are the plan's shared-phone column ("Commit reveal and clock behaviour").</summary>
    public sealed class HostTimings
    {
        public double TerrainAnnounceSeconds = RulesConstants.TerrainAnnouncementMs / 1000.0;
        /// <summary>Per player, entered privately in sequence.</summary>
        public double ChoiceSeconds = RulesConstants.ChoiceDeadlineMs / 1000.0;
        public double HandoverSeconds = RulesConstants.SharedPhoneHandoverMs / 1000.0;
        public double ResolutionSeconds = RulesConstants.ResolutionReplayMaxMs / 1000.0;
        /// <summary>Null uses the match config (20 s shared-phone/practice, 12 s online).</summary>
        public double? CutSeconds;
        /// <summary>Visible "thinking" delay before a bot's cut, so a person can follow it (0 in automation).</summary>
        public double BotCutDelaySeconds = 1.5;
    }

    /// <summary>Public, secret-free snapshot for the HUD and shared screens.</summary>
    public sealed class PublicSnapshot
    {
        public MatchPhase Phase;
        public HostStage Stage;
        /// <summary>The side the current stage concerns (entrant, handover target, cutter), if any.</summary>
        public PlayerSide? StageSide;
        public double StageSecondsRemaining;
        public bool Paused;
        public int Round;
        public int Volley;
        public PlayerSide Attacker;
        public TerrainType Terrain;
        /// <summary>The round's public frontier challenge cell (-1 outside a duel).</summary>
        public int FrontierCellId = -1;
        public int HpA;
        public int HpB;
        public PlayerStatus StatusA;
        public PlayerStatus StatusB;
        public int CellsA;
        public int CellsB;
        public bool LockedA;
        public bool LockedB;
        public PlayerSide? DuelWinner;
        public int HpDifferenceUnits;
        public IReadOnlyList<CardId> OfferedCards = Array.Empty<CardId>();
        public IReadOnlyList<int> OfferedQuotas = Array.Empty<int>();
        public MatchResult Result;

        public int Hp(PlayerSide side) => side == PlayerSide.A ? HpA : HpB;
        public int Cells(PlayerSide side) => side == PlayerSide.A ? CellsA : CellsB;
        public PlayerStatus Status(PlayerSide side) => side == PlayerSide.A ? StatusA : StatusB;
    }

    /// <summary>A resolved volley, ready for presentation on the shared screen.</summary>
    public sealed class ResolvedVolley
    {
        public int Round;
        public int Volley;
        public VolleyResult Result;
        /// <summary>
        /// True when that side's choice is veiled (Mist Veil) from a person watching this screen; the
        /// presentation must then withhold its weapon, element and exact controls.
        /// </summary>
        public bool ConcealA;
        public bool ConcealB;

        public bool Conceal(PlayerSide side) => side == PlayerSide.A ? ConcealA : ConcealB;
    }

    /// <summary>
    /// Host-side clock and sequencing for a local match (shared phone, practice vs a labelled bot, or
    /// automation with two bots) around the authoritative <see cref="MatchEngine"/>. It issues
    /// <see cref="AdvancePhaseCommand"/> exactly when the plan's shared-phone deadlines expire:
    /// 2 s terrain announcement, 12 s per player entered privately in sequence, one opaque handover of
    /// at most 6 s between the two entries, 2.5 s resolution replay and a 20 s cut window. Pause is
    /// available only in local practice. It never decides damage, hits or land: every outcome comes
    /// from the engine, and the UI receives only the entitled player's <see cref="PlayerView"/> or the
    /// secret-free <see cref="PublicSnapshot"/>.
    /// <para>
    /// A first entrant who lets the 12 s expire stays unlocked; the deadline Advance after the second
    /// entry gives them the engine's Pass. Entry order alternates by volley (<see cref="PlayerView.FirstEntrant"/>).
    /// </para>
    /// </summary>
    public sealed class LocalMatchHost
    {
        private readonly SeatKind[] _seats = new SeatKind[2];
        private readonly BotPlayer[] _bots = new BotPlayer[2];
        private readonly Func<string> _requestIds;
        private readonly HostTimings _timings;

        private ulong _syncedRevision;
        private PlayerSide _firstEntrant;
        private int _entryIndex;
        private int _openRound;
        private int _openVolley;
        private double _remaining;
        private double _botCutTimer;
        private bool _matchEndedRaised;

        /// <summary>Timers within a microsecond of zero count as expired (floating-point frame sums).</summary>
        private const double TimerEpsilon = 1e-6;

        public MatchEngine Engine { get; }
        public MatchConfig Config => Engine.Config;
        public HostStage Stage { get; private set; }
        public PlayerSide? StageSide { get; private set; }
        public bool Paused { get; private set; }
        public double StageSecondsRemaining => _remaining;
        public HostTimings Timings => _timings;
        public ResolvedVolley LastResolved { get; private set; }
        public CommandReceipt LastRejection { get; private set; }

        /// <summary>Privacy screens are needed only when two people share the phone.</summary>
        public bool PrivacyScreens => _seats[0] == SeatKind.Human && _seats[1] == SeatKind.Human;
        public bool PauseAllowed => Config.Mode == MatchMode.Practice;

        public event Action<HostStage> StageChanged;
        public event Action<ResolvedVolley> VolleyResolved;
        /// <summary>Cells moved by an accepted cut (0 for a cut timeout) and the cutter.</summary>
        public event Action<PlayerSide, int> CutApplied;
        public event Action<MatchResult> MatchEnded;
        public event Action<CommandReceipt> CommandRejected;

        public LocalMatchHost(MatchConfig config, byte[] seed, string matchId, SeatKind seatA, SeatKind seatB,
            BotDifficulty botDifficulty = BotDifficulty.Normal, Func<string> requestIds = null, HostTimings timings = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            Engine = MatchEngine.Create(config, seed, matchId);
            _seats[0] = seatA;
            _seats[1] = seatB;
            if (seatA == SeatKind.Bot) _bots[0] = BotPlayer.Create(PlayerSide.A, botDifficulty, seed);
            if (seatB == SeatKind.Bot) _bots[1] = BotPlayer.Create(PlayerSide.B, botDifficulty, seed);
            _requestIds = requestIds ?? NewRequestId;
            _timings = timings ?? new HostTimings();
        }

        public SeatKind Seat(PlayerSide side) => _seats[(int)side];

        public static string NewRequestId() => Guid.NewGuid().ToString("D");

        /// <summary>Begins Setup (loadouts). Bots submit at once; people are asked privately in seat order.</summary>
        public void Start()
        {
            if (Stage != HostStage.None) throw new InvalidOperationException("The host has already started.");
            Sync(force: true);
        }

        // ------------------------------------------------------------------ clock

        /// <summary>Advances host timers by real (unscaled) seconds. Nothing advances while paused.</summary>
        public void Tick(double seconds)
        {
            if (seconds <= 0 || Paused || Stage == HostStage.None || Stage == HostStage.MatchOver) return;
            // Large steps (tests, slow frames) are processed stage by stage so no deadline is skipped.
            int guard = 0;
            while (seconds > 0 && guard++ < 64 && Stage != HostStage.MatchOver)
            {
                if (Stage == HostStage.CardAndCut && _bots[(int)StageSide.Value] != null && _botCutTimer > 0)
                {
                    double step = Math.Min(seconds, _botCutTimer);
                    _botCutTimer -= step;
                    _remaining -= step;
                    seconds -= step;
                    if (_botCutTimer <= 0) BotCut(StageSide.Value);
                    continue;
                }
                if (!HasTimer(Stage)) return;
                if (seconds < _remaining - TimerEpsilon)
                {
                    _remaining -= seconds;
                    return;
                }
                seconds -= _remaining;
                _remaining = 0;
                OnTimerExpired();
            }
        }

        public bool SetPaused(bool paused)
        {
            if (paused && !PauseAllowed) return false;
            Paused = paused;
            return true;
        }

        private static bool HasTimer(HostStage stage) =>
            stage == HostStage.TerrainAnnounce || stage == HostStage.EntryReady || stage == HostStage.Entry ||
            stage == HostStage.Handover || stage == HostStage.Resolution || stage == HostStage.CardAndCut;

        private void OnTimerExpired()
        {
            switch (Stage)
            {
                case HostStage.TerrainAnnounce:
                case HostStage.Resolution:
                case HostStage.CardAndCut:
                    Advance();
                    break;
                case HostStage.EntryReady:
                case HostStage.Entry:
                    // The entrant's 12 s are over; an unlocked entrant receives Pass at the deadline.
                    if (_entryIndex == 0) BeginEntry(1);
                    else Advance();
                    break;
                case HostStage.Handover:
                    // At most 6 s between the two entries: the second entrant's window opens now.
                    SetStage(HostStage.Entry, StageSide, _timings.ChoiceSeconds);
                    break;
            }
        }

        // ------------------------------------------------------------------ player actions

        /// <summary>"Tap when ready" on a privacy screen.</summary>
        public bool ConfirmReady()
        {
            switch (Stage)
            {
                case HostStage.LoadoutReady:
                    SetStage(HostStage.LoadoutEntry, StageSide, 0);
                    return true;
                case HostStage.EntryReady:
                    SetStage(HostStage.Entry, StageSide, _remaining); // the same 12 s keep running
                    return true;
                case HostStage.Handover:
                    SetStage(HostStage.Entry, StageSide, _timings.ChoiceSeconds);
                    return true;
                default:
                    return false;
            }
        }

        public CommandReceipt SubmitLoadout(PlayerSide side, IReadOnlyList<int> weapons)
        {
            RequireStage(HostStage.LoadoutEntry, side);
            PlayerView view = Engine.GetView(side);
            CommandReceipt r = Engine.Submit(side, new SubmitLoadoutCommand(view.NewHeader(_requestIds()), weapons));
            Report(r);
            if (r.Accepted) Sync(force: true);
            return r;
        }

        public CommandReceipt SubmitLock(PlayerSide side, VolleyInput choice)
        {
            if (choice == null) throw new ArgumentNullException(nameof(choice));
            RequireStage(HostStage.Entry, side);
            PlayerView view = Engine.GetView(side);
            CommandReceipt r = Engine.Submit(side, new LockInputCommand(view.NewHeader(_requestIds()), view.VolleyIndex, choice));
            Report(r);
            if (r.Accepted) AfterLock();
            return r;
        }

        /// <summary>Exact preview of a cut by the duel winner, as the engine would evaluate it (no mutation).</summary>
        public CutResult PreviewCut(PlayerSide side, CardId card, CardPose pose, int anchorCellId, CutMode mode, IReadOnlyList<CellPoint> vertices)
        {
            if (Stage != HostStage.CardAndCut || StageSide != side) return null;
            return Engine.PreviewCut(side, BuildCut(side, card, pose, anchorCellId, mode, vertices));
        }

        public CommandReceipt SubmitCut(PlayerSide side, CardId card, CardPose pose, int anchorCellId, CutMode mode, IReadOnlyList<CellPoint> vertices)
        {
            RequireStage(HostStage.CardAndCut, side);
            if (_seats[(int)side] != SeatKind.Human) throw new InvalidOperationException("Bots submit their own cuts.");
            CommandReceipt r = Engine.Submit(side, BuildCut(side, card, pose, anchorCellId, mode, vertices));
            Report(r);
            if (r.Accepted)
            {
                CutApplied?.Invoke(side, r.CellsTransferred);
                Sync(force: false);
            }
            return r;
        }

        private SubmitCutCommand BuildCut(PlayerSide side, CardId card, CardPose pose, int anchorCellId, CutMode mode, IReadOnlyList<CellPoint> vertices)
        {
            PlayerView view = Engine.GetView(side);
            return new SubmitCutCommand(view.NewHeader(_requestIds()), view.MapRevision, card, anchorCellId, pose.CenterX, pose.CenterY,
                pose.Rotation, pose.ScaleQuarters, mode, mode == CutMode.Auto ? null : vertices);
        }

        // ------------------------------------------------------------------ views

        /// <summary>
        /// The private view of <paramref name="side"/>, available only while that side is entitled to
        /// see its own controls on this screen: its loadout or volley entry, its own cut window, or at
        /// any time when the opponent is a bot (nobody else can watch). Otherwise throws, so a screen
        /// cannot accidentally show a hidden choice behind the other player's back.
        /// </summary>
        public PlayerView ViewFor(PlayerSide side)
        {
            if (!CanView(side)) throw new InvalidOperationException("Player " + side + " is not entitled to a private view right now.");
            return Engine.GetView(side);
        }

        public bool CanView(PlayerSide side)
        {
            if (_seats[(int)side] != SeatKind.Human) return false;
            if (_seats[(int)Board.Opponent(side)] == SeatKind.Bot) return true;
            if (StageSide != side) return false;
            return Stage == HostStage.LoadoutEntry || Stage == HostStage.Entry || Stage == HostStage.CardAndCut || Stage == HostStage.MatchOver;
        }

        /// <summary>Secret-free state for HUD, announcements and shared screens.</summary>
        public PublicSnapshot Snapshot()
        {
            // Built from A's view, copying only public fields (never OwnLock / OwnLoadout).
            PlayerView a = Engine.GetView(PlayerSide.A);
            PlayerView b = Engine.GetView(PlayerSide.B);
            return new PublicSnapshot
            {
                Phase = a.Phase,
                Stage = Stage,
                StageSide = StageSide,
                StageSecondsRemaining = Math.Max(0, _remaining),
                Paused = Paused,
                Round = a.RoundIndex,
                Volley = a.VolleyIndex,
                Attacker = a.Attacker,
                Terrain = a.DuelTerrain,
                FrontierCellId = a.FrontierCellId,
                HpA = a.Self.HpUnits,
                HpB = a.Foe.HpUnits,
                StatusA = a.Self,
                StatusB = a.Foe,
                CellsA = a.CellsA,
                CellsB = a.CellsB,
                LockedA = b.OpponentLocked,
                LockedB = a.OpponentLocked,
                DuelWinner = a.DuelWinner,
                HpDifferenceUnits = a.HpDifferenceUnits,
                OfferedCards = a.OfferedCards,
                OfferedQuotas = a.OfferedQuotas,
                Result = a.Result,
            };
        }

        /// <summary>Public ownership map copy (for the land screen and result screen).</summary>
        public Territory CloneTerritory() => Engine.GetView(PlayerSide.A).CloneTerritory();

        /// <summary>Replay/support record. Contains the seed: only meaningful (and only written) after the match.</summary>
        public MatchRecord ToRecord() => MatchRecord.FromEngine(Engine);

        // ------------------------------------------------------------------ sequencing

        private void RequireStage(HostStage stage, PlayerSide side)
        {
            if (Stage != stage || StageSide != side)
                throw new InvalidOperationException("Not " + side + "'s " + stage + " (stage is " + Stage + " for " + StageSide + ").");
        }

        private void Report(CommandReceipt r)
        {
            if (r.Accepted) return;
            LastRejection = r;
            CommandRejected?.Invoke(r);
        }

        private void Advance()
        {
            CommandReceipt r = Engine.Advance(Engine.CreateAdvance(_requestIds()));
            if (!r.Accepted) throw new InvalidOperationException("Host advance rejected: " + r);
            Sync(force: false);
        }

        private void AfterLock()
        {
            if (Engine.Phase != MatchPhase.Selection)
            {
                Sync(force: false); // both locked: the engine resolved
                return;
            }
            if (_entryIndex == 0) BeginEntry(1);
            else Advance(); // the first entrant timed out earlier: deadline gives them Pass
        }

        /// <summary>Re-enters the stage matching the engine's phase after any transition.</summary>
        private void Sync(bool force)
        {
            if (!force && Engine.StateRevision == _syncedRevision) return;
            _syncedRevision = Engine.StateRevision;
            switch (Engine.Phase)
            {
                case MatchPhase.Setup:
                    EnterSetup();
                    break;
                case MatchPhase.TerrainAnnounce:
                    SetStage(HostStage.TerrainAnnounce, null, _timings.TerrainAnnounceSeconds);
                    break;
                case MatchPhase.Selection:
                    PlayerView pv = Engine.GetView(PlayerSide.A);
                    _firstEntrant = pv.FirstEntrant;
                    _openRound = pv.RoundIndex;
                    _openVolley = pv.VolleyIndex;
                    BeginEntry(0);
                    break;
                case MatchPhase.Resolution:
                    PublishResolution();
                    SetStage(HostStage.Resolution, null, _timings.ResolutionSeconds);
                    break;
                case MatchPhase.CardAndCut:
                    PlayerSide winner = Engine.GetView(PlayerSide.A).DuelWinner.Value;
                    double cut = _timings.CutSeconds ?? Config.CutWindowMs / 1000.0;
                    SetStage(HostStage.CardAndCut, winner, cut);
                    if (_bots[(int)winner] != null)
                    {
                        _botCutTimer = Math.Min(_timings.BotCutDelaySeconds, cut);
                        if (_botCutTimer <= 0) BotCut(winner);
                    }
                    break;
                case MatchPhase.MatchOver:
                    SetStage(HostStage.MatchOver, null, 0);
                    if (!_matchEndedRaised)
                    {
                        _matchEndedRaised = true;
                        MatchEnded?.Invoke(Engine.Result);
                    }
                    break;
            }
        }

        private void EnterSetup()
        {
            for (int i = 0; i < 2; i++)
            {
                var side = (PlayerSide)i;
                PlayerView view = Engine.GetView(side);
                if (view.OwnLoadout != null) continue;
                if (_bots[i] != null)
                {
                    Submit(side, _bots[i].Decide(view)); // re-syncs: the next seat (or round one) follows
                    return;
                }
                SetStage(PrivacyScreens ? HostStage.LoadoutReady : HostStage.LoadoutEntry, side, 0);
                return;
            }
        }

        private void BeginEntry(int index)
        {
            _entryIndex = index;
            PlayerSide side = index == 0 ? _firstEntrant : Board.Opponent(_firstEntrant);
            if (_bots[(int)side] != null)
            {
                SetStage(HostStage.Entry, side, _timings.ChoiceSeconds);
                Submit(side, _bots[(int)side].Decide(Engine.GetView(side)));
                AfterLock(); // next entrant, deadline, or (both locked) the engine's resolution
                return;
            }
            if (!PrivacyScreens)
                SetStage(HostStage.Entry, side, _timings.ChoiceSeconds);
            else if (index == 0)
                SetStage(HostStage.EntryReady, side, _timings.ChoiceSeconds);
            else
                SetStage(HostStage.Handover, side, _timings.HandoverSeconds);
        }

        private void BotCut(PlayerSide side)
        {
            _botCutTimer = 0;
            if (Stage != HostStage.CardAndCut || StageSide != side) return;
            MatchCommand cmd = _bots[(int)side].Decide(Engine.GetView(side));
            if (cmd == null) return; // the bot declines; the window times out with zero transfer
            CommandReceipt r = Engine.Submit(side, cmd);
            Report(r);
            if (r.Accepted)
            {
                CutApplied?.Invoke(side, r.CellsTransferred);
                Sync(force: false);
            }
        }

        private void Submit(PlayerSide side, MatchCommand cmd)
        {
            if (cmd == null) return;
            CommandReceipt r = Engine.Submit(side, cmd);
            Report(r);
            if (!r.Accepted) throw new InvalidOperationException("Bot command rejected: " + r);
            if (cmd is SubmitLoadoutCommand) Sync(force: true);
        }

        private void PublishResolution()
        {
            VolleyResult result = Engine.GetVolleyResult(_openRound, _openVolley);
            if (result == null) return;
            var resolved = new ResolvedVolley
            {
                Round = _openRound,
                Volley = _openVolley,
                Result = result,
                ConcealA = _seats[1] == SeatKind.Human && Engine.IsConcealedFrom(PlayerSide.B, _openRound, _openVolley),
                ConcealB = _seats[0] == SeatKind.Human && Engine.IsConcealedFrom(PlayerSide.A, _openRound, _openVolley),
            };
            LastResolved = resolved;
            VolleyResolved?.Invoke(resolved);
        }

        private void SetStage(HostStage stage, PlayerSide? side, double seconds)
        {
            Stage = stage;
            StageSide = side;
            _remaining = seconds;
            StageChanged?.Invoke(stage);
        }
    }
}
