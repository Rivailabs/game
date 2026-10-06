using AstraKingdoms.Modes.FourPlayer;

namespace AstraKingdoms.Modes.Tests.FourPlayer;

/// <summary>Plan rows "Start" (seed-committed labels), "Pairing", "Three survivors", "Two survivors".</summary>
public class PairingAndLabelTests
{
    private static SurvivorHistory H(Kingdom k, int duels, bool bye = false) => new(k, duels, bye);

    [Test]
    public void FourSurvivors_CycleTheThreeScheduledWavesUpToSix()
    {
        var four = FourKit.All.Select(k => H(k, 0)).ToList();
        string[] expected = { "A-B/C-D", "A-C/B-D", "A-D/B-C", "A-B/C-D", "A-C/B-D", "A-D/B-C" };
        for (int w = 1; w <= 6; w++)
        {
            WavePlan p = WavePairing.Plan(w, four);
            Assert.That(string.Join("/", p.Pairs), Is.EqualTo(expected[w - 1]), "wave " + w);
            Assert.That(p.Bye, Is.Null);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => WavePairing.Plan(7, four), "at most six waves");
    }

    [Test]
    public void ThreeSurvivors_FewestCompletedDuelsPlay()
    {
        // C has played more: A and D (fewest) duel, C gets the bye.
        WavePlan p = WavePairing.Plan(3, new[] { H(Kingdom.A, 2), H(Kingdom.C, 3), H(Kingdom.D, 2) });
        Assert.That(p.Pairs.Single(), Is.EqualTo(new WavePair(Kingdom.A, Kingdom.D)));
        Assert.That(p.Bye, Is.EqualTo(Kingdom.C));
    }

    [Test]
    public void ThreeSurvivors_EqualCountsUseTheRotatingOrder()
    {
        var three = new[] { H(Kingdom.A, 2), H(Kingdom.B, 2), H(Kingdom.C, 2) };
        // Wave 3 rotates the order to C, D, A, B: C and A play, B gets the bye.
        Assert.That(WavePairing.RotatingRank(Kingdom.C, 3), Is.EqualTo(0));
        WavePlan w3 = WavePairing.Plan(3, three);
        Assert.That(w3.Pairs.Single(), Is.EqualTo(new WavePair(Kingdom.A, Kingdom.C)));
        Assert.That(w3.Bye, Is.EqualTo(Kingdom.B));
        // Wave 2 rotates to B, C, D, A: B and C play, A gets the bye.
        WavePlan w2 = WavePairing.Plan(2, three);
        Assert.That(w2.Pairs.Single(), Is.EqualTo(new WavePair(Kingdom.B, Kingdom.C)));
        Assert.That(w2.Bye, Is.EqualTo(Kingdom.A));
    }

    [Test]
    public void ThreeSurvivors_AvoidConsecutiveByesWhenPossible()
    {
        // B would get the bye by order, but sat out last wave: the next candidate takes it.
        WavePlan p = WavePairing.Plan(3, new[] { H(Kingdom.A, 2), H(Kingdom.B, 2, bye: true), H(Kingdom.C, 2) });
        Assert.That(p.Bye, Is.Not.EqualTo(Kingdom.B));
        Assert.That(p.Pairs.Single().Contains(Kingdom.B), Is.True);
    }

    [Test]
    public void ThreeSurvivors_OverSeveralWavesNobodySitsOutTwiceInARow()
    {
        var duels = new Dictionary<Kingdom, int> { [Kingdom.A] = 1, [Kingdom.B] = 1, [Kingdom.D] = 1 };
        Kingdom? lastBye = null;
        var byes = new Dictionary<Kingdom, int> { [Kingdom.A] = 0, [Kingdom.B] = 0, [Kingdom.D] = 0 };
        for (int w = 2; w <= 6; w++)
        {
            WavePlan p = WavePairing.Plan(w, duels.Keys.Select(k => H(k, duels[k], lastBye == k)).ToList());
            Assert.That(p.Bye, Is.Not.EqualTo(lastBye), "wave " + w);
            foreach (Kingdom k in new[] { p.Pairs[0].First, p.Pairs[0].Second }) duels[k]++;
            byes[p.Bye!.Value]++;
            lastBye = p.Bye;
        }
        Assert.That(byes.Values.Max() - byes.Values.Min(), Is.LessThanOrEqualTo(1), "byes are spread");
    }

    [Test]
    public void TwoSurvivors_DuelEveryWave()
    {
        for (int w = 1; w <= 6; w++)
        {
            WavePlan p = WavePairing.Plan(w, new[] { H(Kingdom.D, w), H(Kingdom.B, w) });
            Assert.That(p.Pairs.Single(), Is.EqualTo(new WavePair(Kingdom.B, Kingdom.D)));
            Assert.That(p.Bye, Is.Null);
        }
    }

    [Test]
    public void Labels_AreSeedCommittedAndVerifiable()
    {
        LabelAssignment a = LabelAssignment.Create(FourKit.Entrants, FourKit.Seed(1), FourKit.MatchId(1));
        LabelAssignment b = LabelAssignment.Create(FourKit.Entrants.Reverse().ToArray(), FourKit.Seed(1), FourKit.MatchId(1));
        string[] labelsA = FourKit.All.Select(a.EntrantOf).ToArray();
        Assert.That(FourKit.All.Select(b.EntrantOf), Is.EqualTo(labelsA), "roster order does not matter");
        Assert.That(labelsA.OrderBy(x => x, StringComparer.Ordinal), Is.EqualTo(FourKit.Entrants), "a permutation");
        Assert.That(a.Commitment, Is.EqualTo(b.Commitment));

        Assert.That(LabelAssignment.Verify(a.Commitment, FourKit.Seed(1), FourKit.MatchId(1), FourKit.Entrants, labelsA), Is.True);
        Assert.That(LabelAssignment.Verify(a.Commitment, FourKit.Seed(2), FourKit.MatchId(1), FourKit.Entrants, labelsA), Is.False, "other seed");
        string[] swapped = { labelsA[1], labelsA[0], labelsA[2], labelsA[3] };
        Assert.That(LabelAssignment.Verify(a.Commitment, FourKit.Seed(1), FourKit.MatchId(1), FourKit.Entrants, swapped), Is.False, "tampered labels");

        var distinct = new HashSet<string>();
        for (int n = 1; n <= 12; n++)
        {
            LabelAssignment x = LabelAssignment.Create(FourKit.Entrants, FourKit.Seed(n), FourKit.MatchId(n));
            distinct.Add(string.Join(",", FourKit.All.Select(x.EntrantOf)));
        }
        Assert.That(distinct.Count, Is.GreaterThan(3), "different seeds give different seats");
    }

    [Test]
    public void Labels_RejectBadRosters()
    {
        Assert.Throws<ArgumentException>(() => LabelAssignment.Create(new[] { "a", "b", "c" }, FourKit.Seed(1), FourKit.MatchId(1)));
        Assert.Throws<ArgumentException>(() => LabelAssignment.Create(new[] { "a", "b", "c", "a" }, FourKit.Seed(1), FourKit.MatchId(1)));
        Assert.Throws<ArgumentException>(() => LabelAssignment.Create(FourKit.Entrants, new byte[31], FourKit.MatchId(1)));
    }

    [Test]
    public void Match_PublishesTheCommitmentBeforeAnyChoiceAndDisclosesTheSeedOnlyAtTheEnd()
    {
        FourPlayerMatch m = FourKit.NewFull(3);
        FourPlayerEvent created = m.PublicEvents[0];
        Assert.That(created.Type, Is.EqualTo(FourPlayerEventType.MatchCreated));
        Assert.That(created.Detail, Does.Contain(m.Labels.CommitmentHex));
        Assert.That(m.DisclosedSeed, Is.Null);
        Assert.That(m.GetView(Kingdom.A).SeedHex, Is.Null);
        foreach (Kingdom k in FourKit.All) m.Submit(new Forfeit4P(k));
        Assert.That(m.IsFinished, Is.True);
        Assert.That(m.DisclosedSeed, Is.EqualTo(FourKit.Seed(3)));
        Assert.That(LabelAssignment.Verify(m.SeedCommitment, m.DisclosedSeed, m.MatchId, FourKit.Entrants,
            FourKit.All.Select(m.Labels.EntrantOf).ToArray()), Is.True);
    }
}
