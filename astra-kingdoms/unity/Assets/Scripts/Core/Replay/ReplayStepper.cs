using System;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;

namespace AstraKingdoms.Client.Replay
{
    public enum ReplayStepKind : byte
    {
        Loadout = 1,
        Lock = 2,
        Advance = 3,
        Cut = 4,
    }

    /// <summary>What one re-executed command did.</summary>
    public sealed class ReplayStep
    {
        public int Index;
        public ReplayStepKind Kind;
        public PlayerSide? Sender;
        public CommandReceipt Receipt;
        /// <summary>Set when this command resolved a volley.</summary>
        public VolleyResult ResolvedVolley;
        public int ResolvedRound;
        public int ResolvedVolleyIndex;
        public int CellsTransferred;
        public MatchPhase PhaseAfter;
    }

    /// <summary>
    /// Plays a <see cref="MatchRecord"/> forward one command at a time through a fresh authoritative
    /// engine, so a viewer can show every volley (from the engine's own event logs) and every land
    /// change in order. Records of another rules version or hash are refused, never reinterpreted.
    /// Use <see cref="Replayer.Verify"/> for the full regression check.
    /// </summary>
    public sealed class ReplayStepper
    {
        private readonly MatchRecord _record;
        private int _next;

        public MatchEngine Engine { get; }
        public int Count => _record.Commands.Count;
        public int Position => _next;
        public bool Finished => _next >= _record.Commands.Count;
        public MatchRecord Record => _record;

        public ReplayStepper(MatchRecord record)
        {
            _record = record ?? throw new ArgumentNullException(nameof(record));
            if (record.RulesVersion != RulesConstants.RulesVersion || record.RulesHashHex != RulesBundle.HashHex)
                throw new InvalidOperationException("Record uses rules " + record.RulesVersion + " / " + record.RulesHashHex +
                                                    "; this build runs " + RulesConstants.RulesVersion + " / " + RulesBundle.HashHex + ".");
            Engine = MatchEngine.Create(record.Config, Hex.Decode(record.SeedHex), record.MatchId);
        }

        public static ReplayStepper FromJson(string json) => new ReplayStepper(MatchRecord.FromJson(json));

        public ReplayStep Step()
        {
            if (Finished) return null;
            RecordedCommand rc = _record.Commands[_next];
            int round = Engine.RoundIndex;
            int volley = Engine.VolleyIndex;
            MatchPhase before = Engine.Phase;
            CommandReceipt receipt = rc.Command is AdvancePhaseCommand adv ? Engine.Advance(adv) : Engine.Submit(rc.Sender.Value, rc.Command);
            if (!receipt.Accepted) throw new InvalidOperationException("Recorded command " + _next + " was rejected on replay: " + receipt);
            var step = new ReplayStep
            {
                Index = _next,
                Kind = KindOf(rc.Command),
                Sender = rc.Sender,
                Receipt = receipt,
                CellsTransferred = receipt.CellsTransferred,
                PhaseAfter = Engine.Phase,
            };
            if (before == MatchPhase.Selection && Engine.Phase != MatchPhase.Selection)
            {
                step.ResolvedVolley = Engine.GetVolleyResult(round, volley);
                step.ResolvedRound = round;
                step.ResolvedVolleyIndex = volley;
            }
            _next++;
            return step;
        }

        private static ReplayStepKind KindOf(MatchCommand c)
        {
            switch (c.Kind)
            {
                case CommandKind.SubmitLoadout: return ReplayStepKind.Loadout;
                case CommandKind.LockInput: return ReplayStepKind.Lock;
                case CommandKind.SubmitCut: return ReplayStepKind.Cut;
                default: return ReplayStepKind.Advance;
            }
        }
    }
}
