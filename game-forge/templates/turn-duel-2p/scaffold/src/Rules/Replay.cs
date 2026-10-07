using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace __NAMESPACE__.Rules
{
    /// <summary>Replay evidence: rules version + seed + first player + ordered commands -> final state hash.</summary>
    public static class Replay
    {
        public static MatchState Run(IRules rules, ulong seed, int firstPlayer, IReadOnlyList<Command> commands)
        {
            var host = new MatchHost(rules, seed, firstPlayer);
            foreach (Command c in commands)
            {
                if (!host.Submit(c, out string reason))
                    throw new InvalidOperationException("replay command rejected (" + c + "): " + reason);
            }
            return host.State;
        }

        public static string StateHash(IRules rules, MatchState state)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(rules.Version + "|" + state.CanonicalText()));
                var sb = new StringBuilder(64);
                foreach (byte b in digest) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
