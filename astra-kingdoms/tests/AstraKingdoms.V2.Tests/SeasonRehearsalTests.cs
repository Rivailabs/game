using AstraKingdoms.V2.Seasons;
using NUnit.Framework;

namespace AstraKingdoms.V2.Tests;

/// <summary>
/// Plan: "Validate V2 with two rehearsed season settlements" and "V2 uses an 18-day season only after
/// the full season lifecycle works in a test environment". Each run simulates two complete 18-day
/// seasons end to end through the real services and reports every invariant as a named check.
/// </summary>
public class SeasonRehearsalTests
{
    private static RehearsalReport _first;

    private static RehearsalReport First => _first ??= SeasonRehearsal.Run(new RehearsalOptions());

    [Test]
    public void TwoFullSeasonsSettleWithEveryInvariantHolding()
    {
        RehearsalReport r = First;
        TestContext.WriteLine(r.Describe());
        Assert.That(r.Seasons, Has.Count.EqualTo(2));
        Assert.That(r.AllChecks.Where(c => !c.Passed).Select(c => c.ToString()), Is.Empty);
        foreach (RehearsalSeasonReport s in r.Seasons)
        {
            Assert.That(s.Tickets, Is.GreaterThan(100), s.SeasonId + " had a realistic number of ranked matches");
            Assert.That(s.LeagueRewards, Is.GreaterThan(0));
            Assert.That(s.PassBuyers, Is.GreaterThan(0));
            Assert.That(s.PassFreeDelivered + s.PassPaidDelivered, Is.GreaterThan(0), "unclaimed rewards were delivered at settlement");
            Assert.That(s.LatePaidDelivered, Is.GreaterThan(0), "the late purchase delivered earned paid tiers");
        }
        Assert.That(r.CrossSeasonChecks.Select(c => c.Name), Has.Some.Contains("soft reset"));
    }

    [Test]
    public void TheRehearsalIsDeterministic()
    {
        RehearsalReport again = SeasonRehearsal.Run(new RehearsalOptions());
        Assert.That(again.Digest, Is.EqualTo(First.Digest));
        RehearsalReport other = SeasonRehearsal.Run(new RehearsalOptions { Seed = 7 });
        Assert.That(other.AllPassed, Is.True, other.Describe());
        Assert.That(other.Digest, Is.Not.EqualTo(First.Digest));
    }

    [Test]
    public void ARehearsalOfThreeSeasonsAlsoHolds()
    {
        RehearsalReport r = SeasonRehearsal.Run(new RehearsalOptions { Seasons = 3, Players = 16, Seed = 99 });
        Assert.That(r.AllPassed, Is.True, r.Describe());
        Assert.That(r.Seasons, Has.Count.EqualTo(3));
    }
}
