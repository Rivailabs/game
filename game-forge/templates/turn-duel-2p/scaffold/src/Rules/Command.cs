namespace __NAMESPACE__.Rules
{
    /// <summary>One player's choice for one turn. The meaning of <see cref="Choice"/> is game-defined.</summary>
    public sealed class Command
    {
        public Command(int player, int turn, int choice)
        {
            Player = player;
            Turn = turn;
            Choice = choice;
        }

        public int Player { get; }
        public int Turn { get; }
        public int Choice { get; }

        public override string ToString() => "P" + Player + " T" + Turn + " C" + Choice;
    }
}
