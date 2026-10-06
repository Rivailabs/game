namespace __NAMESPACE__.Rules
{
    /// <summary>Rematch: same settings, a new seed, and the other player goes first.</summary>
    public static class Rematch
    {
        public static MatchHost Start(IRules rules, MatchState finished, ulong newSeed)
        {
            return new MatchHost(rules, newSeed, 1 - FirstPlayerOfMatch(finished));
        }

        /// <summary>The player who went first on turn 0 (first player alternates each turn).</summary>
        public static int FirstPlayerOfMatch(MatchState state)
        {
            return state.Turn % 2 == 0 ? state.FirstPlayer : 1 - state.FirstPlayer;
        }
    }
}
