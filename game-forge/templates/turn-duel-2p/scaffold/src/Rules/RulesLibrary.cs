namespace __NAMESPACE__.Rules
{
    /// <summary>The game's rules. The approved specification's rules task replaces <see cref="SampleRules"/>.</summary>
    public interface IRules
    {
        string Version { get; }
        MatchState NewMatch(ulong seed, int firstPlayer);
        bool IsLegal(MatchState state, Command command, out string reason);
        /// <summary>Choice used when a player's turn timer expires.</summary>
        int DefaultChoice(MatchState state, int player);
        /// <summary>Resolve one turn from both players' choices (revealed together).</summary>
        void Resolve(MatchState state, Command player0, Command player1);
    }

    /// <summary>
    /// PLACEHOLDER rules so the template's sample build has something real to test: each player
    /// chooses 1-3 and deals that much damage; 10 starting health; at most 10 turns. These are
    /// NOT a game design; Forge never ships them as a game's rules.
    /// </summary>
    public sealed class SampleRules : IRules
    {
        public const int StartingHealth = 10;
        public const int MaxTurns = 10;

        public string Version => "sample-1";

        public MatchState NewMatch(ulong seed, int firstPlayer) => new MatchState(seed, firstPlayer, StartingHealth);

        public bool IsLegal(MatchState state, Command command, out string reason)
        {
            if (state.IsOver) { reason = "match is over"; return false; }
            if (command.Player < 0 || command.Player >= MatchState.Players) { reason = "unknown player"; return false; }
            if (command.Turn != state.Turn) { reason = "wrong turn"; return false; }
            if (command.Choice < 1 || command.Choice > 3) { reason = "choice must be 1-3"; return false; }
            reason = "";
            return true;
        }

        public int DefaultChoice(MatchState state, int player) => 1;

        public void Resolve(MatchState state, Command player0, Command player1)
        {
            state.Health[1] -= player0.Choice;
            state.Health[0] -= player1.Choice;
            state.Turn++;
            state.FirstPlayer = 1 - state.FirstPlayer;
            bool dead0 = state.Health[0] <= 0, dead1 = state.Health[1] <= 0;
            if (dead0 && dead1) state.Outcome = MatchOutcome.Draw;
            else if (dead1) state.Outcome = MatchOutcome.Player0Wins;
            else if (dead0) state.Outcome = MatchOutcome.Player1Wins;
            else if (state.Turn >= MaxTurns)
                state.Outcome = state.Health[0] == state.Health[1] ? MatchOutcome.Draw
                    : state.Health[0] > state.Health[1] ? MatchOutcome.Player0Wins : MatchOutcome.Player1Wins;
        }
    }
}
