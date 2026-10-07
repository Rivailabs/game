using System.Collections.Generic;
using __NAMESPACE__.Rules;
using NUnit.Framework;

namespace __NAMESPACE__.Rules.Tests
{
    /// <summary>Template skeleton checks (TPL-* cases). Game-specific acceptance cases are added per task.</summary>
    public class TemplateTests
    {
        private static MatchHost PlayBots(ulong seed)
        {
            var rules = new SampleRules();
            var host = new MatchHost(rules, seed, 0);
            var bots = new[] { new SampleBot(0, seed), new SampleBot(1, seed) };
            while (!host.State.IsOver)
            {
                foreach (SampleBot bot in bots)
                    Assert.That(host.Submit(bot.Choose(host.State.Clone()), out string reason), Is.True, reason);
            }
            return host;
        }

        [Test]
        public void InvalidCommandIsRejectedWithAReason()
        {
            var host = new MatchHost(new SampleRules(), 7, 0);
            Assert.That(host.Submit(new Command(0, 0, 9), out string reason), Is.False);
            Assert.That(reason, Is.Not.Empty);
            Assert.That(host.Submit(new Command(0, 5, 1), out _), Is.False);
        }

        [Test]
        public void TurnResolvesOnlyWhenBothPlayersChose()
        {
            var host = new MatchHost(new SampleRules(), 7, 0);
            Assert.That(host.Submit(new Command(0, 0, 2), out _), Is.True);
            Assert.That(host.State.Turn, Is.EqualTo(0));
            host.TimerExpired(1);
            Assert.That(host.State.Turn, Is.EqualTo(1));
            Assert.That(host.State.Health[0], Is.EqualTo(SampleRules.StartingHealth - 1));
        }

        [Test]
        public void ReplayReproducesTheFinalStateHash([Values(1UL, 42UL, 20261006UL)] ulong seed)
        {
            var rules = new SampleRules();
            MatchHost live = PlayBots(seed);
            MatchState replayed = Replay.Run(rules, seed, 0, new List<Command>(live.Log));
            Assert.That(Replay.StateHash(rules, replayed), Is.EqualTo(Replay.StateHash(rules, live.State)));
        }

        [Test]
        public void RematchSwapsTheFirstPlayer()
        {
            var rules = new SampleRules();
            MatchHost first = PlayBots(3);
            MatchHost next = Rematch.Start(rules, first.State, 4);
            Assert.That(next.State.FirstPlayer, Is.EqualTo(1));
            Assert.That(next.State.Turn, Is.EqualTo(0));
        }
    }
}
