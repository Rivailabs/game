using AstraKingdoms.Modes.FourPlayer;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;

namespace AstraKingdoms.Modes.Tests.FourPlayer;

/// <summary>Shared helpers for four-player tests.</summary>
internal static class FourKit
{
    public static readonly string[] Entrants = { "alice", "bob", "carol", "dave" };
    public static readonly int[] DuelKit = { 1, 10, 19 };
    public static readonly Kingdom[] All = { Kingdom.A, Kingdom.B, Kingdom.C, Kingdom.D };

    /// <summary>A shot that misses a standing target (Ember Arrow, steep and wide).</summary>
    public static readonly VolleyInput Miss = new(1, 260, 32, 100, Dodge.None);

    public static byte[] Seed(int n)
    {
        BotMatchRunner.SeedFor(0x4B1D, n, out byte[] seed, out _);
        return seed;
    }

    public static string MatchId(int n) => "22222222-3333-4444-8555-" + n.ToString("x12");

    /// <summary>A Thunder Crown core hit from duel seat <paramref name="side"/> against a standing target.</summary>
    public static VolleyInput Hit(PlayerSide side)
    {
        AimSolution aim = AimSolver.Solve(19, side, Fixed.Zero, Fixed.Zero);
        Assert.That(aim.FullContacts, Is.GreaterThan(0));
        return new VolleyInput(19, aim.PitchQdeg, aim.YawQdeg, 100, Dodge.None);
    }

    public static FourPlayerMatch NewFull(int n = 1) =>
        FourPlayerMatch.Create(FourPlayerConfig.Full, Entrants, Seed(n), MatchId(n));

    public static void SubmitLoadouts(FourPlayerMatch m)
    {
        foreach (Kingdom k in All)
            if (m.IsAlive(k) && m.GetView(k).OwnLoadout == null)
                Assert.That(m.Submit(new SubmitLoadout4P(k, DuelKit)).Accepted, Is.True);
    }

    /// <summary>
    /// Plays the current duel of <paramref name="k"/>'s pair to its end. <paramref name="winner"/>
    /// hits every volley while the other misses; null means both miss (a draw).
    /// </summary>
    public static void PlayDuel(FourPlayerMatch m, Kingdom k, Kingdom? winner)
    {
        for (int guard = 0; guard < 3; guard++)
        {
            FourPlayerParticipantView v = m.GetView(k);
            if (!v.InDuel || v.Stage != PairStage.Selection) return;
            Kingdom o = v.Opponent!.Value;
            FourPlayerParticipantView ov = m.GetView(o);
            VolleyInput mine = winner == k ? Hit(v.DuelSide) : Miss;
            VolleyInput theirs = winner == o ? Hit(ov.DuelSide) : Miss;
            Assert.That(m.Submit(new Lock4P(k, v.Wave, v.Volley, mine)).Accepted, Is.True);
            Assert.That(m.Submit(new Lock4P(o, v.Wave, v.Volley, theirs)).Accepted, Is.True);
        }
    }

    /// <summary>The winner's Auto Cut with its largest-quota card, centred on <paramref name="anchor"/>.</summary>
    public static SubmitCut4P AutoCut(FourPlayerMatch m, Kingdom winner, int anchor, int rotation = 0)
    {
        FourPlayerParticipantView v = m.GetView(winner);
        Assert.That(v.IsCutTurn, Is.True, winner + " should have a cut");
        int best = 0;
        for (int i = 1; i < v.OfferedQuotas.Count; i++)
            if (v.OfferedQuotas[i] > v.OfferedQuotas[best]) best = i;
        CardId card = v.OfferedCards[best];
        int scale = FourPlayerCutRules.LargestFittingScale(card, rotation, v.OfferedQuotas[best]);
        var pose = new CardPose(Board.X(anchor), Board.Y(anchor), scale, rotation);
        return new SubmitCut4P(winner, v.Wave, card, pose, CellPoint.FromCellId(anchor));
    }

    /// <summary>A cell deep inside a kingdom's starting quarter.</summary>
    public static int Interior(Kingdom k)
    {
        int x = k == Kingdom.A || k == Kingdom.D ? 64 : 191;
        int y = k == Kingdom.A || k == Kingdom.B ? 64 : 191;
        return Board.CellId(x, y);
    }

    public static void AssertConserved(FourPlayerMatch m)
    {
        FourOwnerTerritory t = m.CloneTerritory();
        Assert.That(t.FindInvariantViolation(), Is.Null);
        int sum = t.NeutralCellCount;
        foreach (Kingdom k in All) sum += t.CellCount(k);
        Assert.That(sum, Is.EqualTo(RulesConstants.ActiveCells));
    }
}
