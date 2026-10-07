using System;
using System.Collections.Generic;

namespace __NAMESPACE__.Rules
{
    /// <summary>
    /// Turn sequencing: both players submit a hidden choice, then the turn resolves. Invalid
    /// commands are rejected with a reason (the player chooses again); an expired timer submits
    /// the rules' default choice. Every accepted command is recorded in order for replay.
    /// </summary>
    public sealed class MatchHost
    {
        private readonly IRules _rules;
        private readonly Command[] _pending = new Command[MatchState.Players];
        private readonly List<Command> _log = new List<Command>();

        public MatchHost(IRules rules, ulong seed, int firstPlayer)
        {
            _rules = rules ?? throw new ArgumentNullException(nameof(rules));
            State = rules.NewMatch(seed, firstPlayer);
        }

        public MatchState State { get; }
        public IReadOnlyList<Command> Log => _log;

        public bool Submit(Command command, out string reason)
        {
            if (!_rules.IsLegal(State, command, out reason)) return false;
            if (_pending[command.Player] != null) { reason = "already chosen this turn"; return false; }
            _pending[command.Player] = command;
            if (_pending[0] != null && _pending[1] != null)
            {
                _log.Add(_pending[0]);
                _log.Add(_pending[1]);
                _rules.Resolve(State, _pending[0], _pending[1]);
                _pending[0] = null;
                _pending[1] = null;
            }
            return true;
        }

        /// <summary>The player's turn timer expired: submit the rules' default choice.</summary>
        public void TimerExpired(int player)
        {
            if (State.IsOver || _pending[player] != null) return;
            var auto = new Command(player, State.Turn, _rules.DefaultChoice(State, player));
            if (!Submit(auto, out string reason)) throw new InvalidOperationException("default choice rejected: " + reason);
        }
    }
}
