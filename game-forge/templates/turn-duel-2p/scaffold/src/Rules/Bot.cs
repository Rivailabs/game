namespace __NAMESPACE__.Rules
{
    /// <summary>
    /// Bot opponent. It receives only what its player may see (here: the public state) and uses
    /// its own deterministic generator, so bot matches replay exactly from the seed.
    /// </summary>
    public sealed class SampleBot
    {
        private ulong _state;

        public SampleBot(int player, ulong seed)
        {
            Player = player;
            _state = seed ^ (0xA5A5A5A5UL + (ulong)player);
        }

        public int Player { get; }

        public Command Choose(MatchState publicState)
        {
            unchecked
            {
                _state = _state * 6364136223846793005UL + 1442695040888963407UL;
            }
            int choice = 1 + (int)((_state >> 33) % 3UL);
            return new Command(Player, publicState.Turn, choice);
        }
    }
}
