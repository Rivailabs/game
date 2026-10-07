using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Rules.Tests.Match;

/// <summary>Scripted driver around a <see cref="MatchEngine"/> for match tests.</summary>
internal sealed class Harness
{
    private int _request;

    public MatchEngine E { get; }

    public Harness(MatchConfig config, int seedNumber = 1, string? matchId = null)
    {
        E = MatchEngine.Create(config, MatchKit.Seed(seedNumber), matchId ?? MatchKit.MatchId(seedNumber));
    }

    /// <summary>Full catalog on the plain template: no terrain effects disturb scripted HP arithmetic.</summary>
    public static Harness FullPlain(int seed = 1) =>
        new(new MatchConfig(MatchMode.Online, CatalogPreset.Full, CardOfferRule.V1, TerrainTemplates.PlainId), seed);

    public string Req() => "00000000-0000-4000-8000-" + (++_request).ToString("x12");

    public PlayerView View(PlayerSide side) => E.GetView(side);

    public CommandReceipt Loadout(PlayerSide side, params int[] weapons) =>
        E.Submit(side, new SubmitLoadoutCommand(View(side).NewHeader(Req()), weapons));

    public void Loadouts(int[] a, int[] b)
    {
        Assert.That(Loadout(PlayerSide.A, a).Accepted, Is.True);
        Assert.That(Loadout(PlayerSide.B, b).Accepted, Is.True);
    }

    public LockInputCommand LockCommand(PlayerSide side, VolleyInput choice) =>
        new(View(side).NewHeader(Req()), E.VolleyIndex, choice);

    public CommandReceipt Lock(PlayerSide side, VolleyInput choice) => E.Submit(side, LockCommand(side, choice));

    public CommandReceipt Advance() => E.Advance(E.CreateAdvance(Req()));

    /// <summary>Advances announcement/resolution phases until a selection is open (or the match ends).</summary>
    public void ToSelection()
    {
        while (E.Phase == MatchPhase.TerrainAnnounce || (E.Phase == MatchPhase.Resolution && !E.CurrentDuel.IsOver))
            Assert.That(Advance().Accepted, Is.True);
    }

    /// <summary>Plays one volley with the given choices (null = timeout Pass).</summary>
    public void Volley(VolleyInput? a, VolleyInput? b)
    {
        ToSelection();
        Assert.That(E.Phase, Is.EqualTo(MatchPhase.Selection));
        if (a != null) Assert.That(Lock(PlayerSide.A, a).Accepted, Is.True);
        if (b != null) Assert.That(Lock(PlayerSide.B, b).Accepted, Is.True);
        if (E.Phase == MatchPhase.Selection) Assert.That(Advance().Accepted, Is.True); // deadline
    }

    /// <summary>Ends the duel's resolution phase (moves to CardAndCut or the next round).</summary>
    public void FinishDuel()
    {
        Assert.That(E.Phase, Is.EqualTo(MatchPhase.Resolution));
        Assert.That(E.CurrentDuel.IsOver, Is.True);
        Assert.That(Advance().Accepted, Is.True);
    }

    /// <summary>Three miss/miss volleys: a 100-100 draw, no card.</summary>
    public void DrawRound()
    {
        for (int v = 0; v < 3; v++) Volley(MatchKit.Miss, MatchKit.Miss);
        FinishDuel();
    }

    /// <summary>The winner KOs the loser with two Thunder Crown core hits against Ember (Agni). Loadouts must allow it.</summary>
    public void WinRound(PlayerSide winner)
    {
        VolleyInput hit = MatchKit.ThunderHit(winner);
        for (int v = 0; v < 2; v++)
        {
            if (winner == PlayerSide.A) Volley(hit, MatchKit.Miss);
            else Volley(MatchKit.Miss, hit);
        }
        Assert.That(E.CurrentDuel.Winner, Is.EqualTo(winner));
        FinishDuel();
        Assert.That(E.Phase, Is.EqualTo(MatchPhase.CardAndCut));
    }

    /// <summary>Plans a large Auto Cut for the duel winner with the Hard planner.</summary>
    public SubmitCutCommand PlannedCut(PlayerSide winner)
    {
        PlayerView view = View(winner);
        var budget = new CutSearchBudget { Anchors = 2, RefineAnchors = 6, Rotations = 4, AllCards = true, CenterOffsetPercent = 60 };
        CutPlan plan = CutPlanner.Plan(view, budget, new BotRng(7))!;
        Assert.That(plan, Is.Not.Null);
        return new SubmitCutCommand(view.NewHeader(Req()), view.MapRevision, plan.Card, plan.AnchorCellId,
            plan.Pose.CenterX, plan.Pose.CenterY, plan.Pose.Rotation, plan.Pose.ScaleQuarters, CutMode.Auto);
    }
}

internal static class MatchKit
{
    public static byte[] Seed(int n)
    {
        var seed = new byte[32];
        seed[0] = (byte)n;
        seed[1] = (byte)(n >> 8);
        seed[31] = 0xA5;
        return seed;
    }

    public static string MatchId(int n) => "11111111-2222-4333-8444-" + n.ToString("x12");

    /// <summary>Ember Arrow lobbed at 65° with 8° yaw: lands far above and beside the target (verified by tests).</summary>
    public static readonly VolleyInput Miss = new(1, 260, 32, 100, Dodge.None);

    /// <summary>A Thunder Crown core hit from <paramref name="shooter"/> against a standing, baseline target.</summary>
    public static VolleyInput ThunderHit(PlayerSide shooter)
    {
        AimSolution aim = AimSolver.Solve(19, shooter, Fixed.Zero, Fixed.Zero);
        Assert.That(aim.FullContacts, Is.GreaterThan(0));
        return new VolleyInput(19, aim.PitchQdeg, aim.YawQdeg, 100, Dodge.None);
    }

    /// <summary>Loadouts that support <see cref="Harness.WinRound"/> and <see cref="Harness.DrawRound"/> for both players.</summary>
    public static readonly int[] DuelKit = { 1, 10, 19 };
}
