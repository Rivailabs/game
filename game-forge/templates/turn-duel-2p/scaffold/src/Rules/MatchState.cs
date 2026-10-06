using System.Text;

namespace __NAMESPACE__.Rules
{
    public enum MatchOutcome
    {
        InProgress = 0,
        Player0Wins = 1,
        Player1Wins = 2,
        Draw = 3,
    }

    /// <summary>
    /// Authoritative match state. Integer-only so every platform resolves identically; the
    /// canonical text is what the replay hash covers.
    /// </summary>
    public sealed class MatchState
    {
        public const int Players = 2;

        public MatchState(ulong seed, int firstPlayer, int startingHealth)
        {
            Seed = seed;
            RngState = seed;
            FirstPlayer = firstPlayer;
            Health = new[] { startingHealth, startingHealth };
        }

        public ulong Seed { get; }
        public ulong RngState { get; set; }
        public int Turn { get; set; }
        public int FirstPlayer { get; set; }
        public int[] Health { get; }
        public MatchOutcome Outcome { get; set; }

        public bool IsOver => Outcome != MatchOutcome.InProgress;

        public MatchState Clone()
        {
            var c = new MatchState(Seed, FirstPlayer, 0) { RngState = RngState, Turn = Turn, Outcome = Outcome };
            c.Health[0] = Health[0];
            c.Health[1] = Health[1];
            return c;
        }

        /// <summary>Deterministic 64-bit generator (SplitMix64). Never System.Random.</summary>
        public ulong NextRandom()
        {
            unchecked
            {
                RngState += 0x9E3779B97F4A7C15UL;
                ulong z = RngState;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        public string CanonicalText()
        {
            var sb = new StringBuilder();
            sb.Append("seed=").Append(Seed).Append(";rng=").Append(RngState).Append(";turn=").Append(Turn)
              .Append(";first=").Append(FirstPlayer).Append(";hp=").Append(Health[0]).Append(',').Append(Health[1])
              .Append(";outcome=").Append((int)Outcome);
            return sb.ToString();
        }
    }
}
