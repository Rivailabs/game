using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Rules.Tests.Match;

public class RulesHashTests
{
    /// <summary>
    /// Golden AK-TR-1 rules hash. If this changes, an outcome-relevant constant, table or template
    /// changed: that requires a new rules version, never a silent update of this value.
    /// </summary>
    private const string GoldenHash = "e33d84b914ae8ec900c60448be7c7f9e63e08db3657bb2b235580d29917013eb";

    [Test]
    public void RulesHash_IsStable()
    {
        Assert.That(RulesBundle.HashHex, Is.EqualTo(GoldenHash));
        Assert.That(RulesBundle.Hash, Has.Length.EqualTo(32));
        Assert.That(RulesBundleContents.Current().ComputeHash(), Is.EqualTo(RulesBundle.Hash));
        byte[] first = RulesBundle.CanonicalEncoding();
        Assert.That(RulesBundle.CanonicalEncoding(), Is.EqualTo(first), "the encoding is deterministic");
    }

    [Test]
    public void RulesHash_CoversConstantsTablesAndTemplates()
    {
        string baseline = Hex.Encode(RulesBundleContents.Current().ComputeHash());

        string With(Action<RulesBundleContents> change)
        {
            RulesBundleContents c = RulesBundleContents.Current();
            change(c);
            return Hex.Encode(c.ComputeHash());
        }

        var changes = new Dictionary<string, Action<RulesBundleContents>>
        {
            ["victory constant"] = c =>
            {
                int i = c.Constants.FindIndex(k => k.Key == "VictoryCells");
                c.Constants[i] = new KeyValuePair<string, long>("VictoryCells", c.Constants[i].Value + 1);
            },
            ["burn constant"] = c =>
            {
                int i = c.Constants.FindIndex(k => k.Key == "BurnUnits");
                c.Constants[i] = new KeyValuePair<string, long>("BurnUnits", 600);
            },
            ["weapon damage"] = c => c.Weapons[18].DamageUnits += 100,
            ["weapon pitch range"] = c => c.Weapons[5].PitchMaxQdeg -= 1,
            ["card cap"] = c => c.CardCaps[5] = new KeyValuePair<int, int>(6, 21),
            ["trig table byte"] = c => c.TrigTableBytes[100] ^= 1,
            ["envelope table"] = c => c.EnvelopeCos[1] += 1,
            ["terrain template"] = c => c.TerrainTemplates[1] = new KeyValuePair<string, byte[]>(c.TerrainTemplates[1].Key, new byte[32]),
            ["rules version"] = c => c.RulesVersion = "AK-TR-2",
        };
        var seen = new HashSet<string> { baseline };
        foreach (var change in changes)
            Assert.That(seen.Add(With(change.Value)), Is.True, change.Key + " must change the rules hash");
    }

    [Test]
    public void ConstantNamesArePartOfTheEncoding()
    {
        RulesBundleContents c = RulesBundleContents.Current();
        Assert.That(c.Constants.Select(k => k.Key), Is.Unique);
        Assert.That(c.Weapons, Has.Count.EqualTo(20));
        Assert.That(c.TerrainTemplates.Select(t => t.Key), Is.EquivalentTo(new[] { "AK-TR-1/plain", "AK-TR-1/full-01" }));
    }
}
