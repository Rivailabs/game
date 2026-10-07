using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Tests.Land;

[TestFixture]
public class SeededStreamTests
{
    private static readonly byte[] ZeroSeed = new byte[32];

    private static string FirstEightHex(string label, uint round)
    {
        byte[] digest = new SeededStream(label, ZeroSeed, round).NextDigest();
        return Convert.ToHexString(digest, 0, 8).ToLowerInvariant();
    }

    [Test]
    public void GoldenVector_Initiative()
    {
        Assert.That(FirstEightHex(StreamLabels.Initiative, 0), Is.EqualTo("79e4c771f74e35fb"));
        var stream = SeededStream.Initiative(ZeroSeed);
        Assert.That(stream.NextIndex(2), Is.EqualTo(1));
        Assert.That(stream.Counter, Is.EqualTo(1u));
    }

    [Test]
    public void GoldenVector_Terrain()
    {
        Assert.That(FirstEightHex(StreamLabels.Terrain, 1), Is.EqualTo("fe50068810271381"));
        var stream = SeededStream.Terrain(ZeroSeed, 1);
        Assert.That(stream.NextIndex(100), Is.EqualTo(89));
        Assert.That(stream.Counter, Is.EqualTo(1u));
    }

    [Test]
    public void GoldenVector_Cards()
    {
        Assert.That(FirstEightHex(StreamLabels.Cards, 1), Is.EqualTo("c4baaed295bda319"));
        var stream = SeededStream.Cards(ZeroSeed, 1);
        Assert.That(stream.NextIndex(5), Is.EqualTo(2));
        Assert.That(stream.Counter, Is.EqualTo(1u));
    }

    [Test]
    public void Candidate_IsBigEndianFirstEightBytes()
    {
        Assert.That(SeededStream.Candidate(StreamLabels.Terrain, ZeroSeed, 1, 0), Is.EqualTo(0xfe50068810271381UL));
    }

    [Test]
    public void CounterIncrementsAfterEveryDigestAndCanResume()
    {
        var stream = SeededStream.Map(ZeroSeed);
        ulong first = stream.NextCandidate();
        ulong second = stream.NextCandidate();
        Assert.That(stream.Counter, Is.EqualTo(2u));
        Assert.That(first, Is.Not.EqualTo(second));

        var resumed = new SeededStream(StreamLabels.Map, ZeroSeed, 0, startCounter: 1);
        Assert.That(resumed.NextCandidate(), Is.EqualTo(second));
    }

    [Test]
    public void StreamsAndRoundsAreIndependent()
    {
        ulong terrain1 = SeededStream.Candidate(StreamLabels.Terrain, ZeroSeed, 1, 0);
        ulong terrain2 = SeededStream.Candidate(StreamLabels.Terrain, ZeroSeed, 2, 0);
        ulong cards1 = SeededStream.Candidate(StreamLabels.Cards, ZeroSeed, 1, 0);
        Assert.That(new[] { terrain1, terrain2, cards1 }, Is.Unique);
    }

    [Test]
    public void RejectionBoundary_IsFloorOfTwoToTheSixtyFourOverKTimesK()
    {
        // 2^64 mod 3 == 1, so the limit is 2^64 - 1 and only ulong.MaxValue is rejected.
        Assert.That(SeededStream.IsAccepted(ulong.MaxValue, 3), Is.False);
        Assert.That(SeededStream.IsAccepted(ulong.MaxValue - 1, 3), Is.True);
        // Powers of two never reject.
        Assert.That(SeededStream.IsAccepted(ulong.MaxValue, 2), Is.True);
        Assert.That(SeededStream.IsAccepted(ulong.MaxValue, 1UL << 20), Is.True);
        // 2^64 mod 100 == 16.
        Assert.That(SeededStream.IsAccepted(ulong.MaxValue - 15, 100), Is.False);
        Assert.That(SeededStream.IsAccepted(ulong.MaxValue - 16, 100), Is.True);
    }

    [Test]
    public void DrawWithoutReplacement_ContinuesSameCounter()
    {
        var stream = SeededStream.Cards(ZeroSeed, 1);
        var drawn = stream.DrawWithoutReplacement(new[] { "Chakra", "Garuda", "Suchi", "Makara", "Padma" }, 3);
        // Reference values computed independently in Python.
        Assert.That(drawn, Is.EqualTo(new[] { "Suchi", "Garuda", "Chakra" }));
        Assert.That(stream.Counter, Is.EqualTo(3u));
    }

    [Test]
    public void SeedCommitment_MatchesReference()
    {
        byte[] rulesHash = Enumerable.Repeat((byte)0x11, 32).ToArray();
        byte[] commitment = SeededStream.SeedCommitment(rulesHash, ZeroSeed, "00000000-0000-4000-8000-000000000000");
        Assert.That(Convert.ToHexString(commitment).ToLowerInvariant(),
            Is.EqualTo("43cb39d0c8f964b63ed504bd9af4bf26de8444a698fc54a0f9aca66aaccf354e"));
    }

    [Test]
    public void SeedCommitment_RejectsNonCanonicalUuid()
    {
        var hash = new byte[32];
        Assert.Throws<ArgumentException>(() => SeededStream.SeedCommitment(hash, ZeroSeed, "00000000-0000-4000-8000-00000000000A"));
        Assert.Throws<ArgumentException>(() => SeededStream.SeedCommitment(hash, ZeroSeed, "000000000000000000000000000000000000"));
    }

    [Test]
    public void RejectsWrongSeedLength()
    {
        Assert.Throws<ArgumentException>(() => SeededStream.Terrain(new byte[31], 1));
    }
}
