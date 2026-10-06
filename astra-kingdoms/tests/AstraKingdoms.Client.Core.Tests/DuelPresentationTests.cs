using AstraKingdoms.Client.Combat;
using AstraKingdoms.Client.Localization;
using AstraKingdoms.Client.Match;
using AstraKingdoms.Client.Presentation;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Client.Tests;

/// <summary>Duel presentation tickets 26-36: archer contract, string, budgets, effects, cues, aim, reveal, HUD, arenas.</summary>
public sealed class DuelPresentationTests
{
    private static readonly Lazy<List<VolleyResult>> BotVolleys = new(() =>
    {
        var all = new List<VolleyResult>();
        for (ulong n = 0; n < 4; n++)
        {
            BotMatchRunner.SeedFor(4242, (long)n, out byte[] seed, out string id);
            MatchEngine e = BotMatchRunner.Run(MatchConfig.V1Full(MatchMode.Online), seed, id,
                BotPlayer.Create(PlayerSide.A, BotDifficulty.Hard, seed), BotPlayer.Create(PlayerSide.B, BotDifficulty.Normal, seed));
            foreach (RoundRecord r in e.Rounds)
                foreach (VolleyRecord v in r.Volleys)
                    all.Add(e.GetVolleyResult(r.Round, v.Volley));
        }
        return all;
    });

    private static VolleyResult Duel1(int[] a, int[] b, VolleyInput ia, VolleyInput ib, TerrainType terrain = TerrainType.Plain) =>
        Duel.Start(1, terrain, PlayerSide.B, Loadout.Create(CatalogPreset.Full, a), Loadout.Create(CatalogPreset.Full, b)).Resolve(ia, ib);

    private static LocalizationTable En() => LocalizationTable.Parse("en", File.ReadAllText(Path.Combine(Paths.Localization, "en.txt")));

    // ------------------------------------------------------------------ 26-28 archer

    [Test]
    public void AttachmentContract_ReportsMissingPoints()
    {
        Assert.That(ArcherAttachments.Missing(new[] { "hand_l", "hand_r", "bow_grip", "string_nock", "arrow_spawn", "spine" }), Is.Empty);
        Assert.That(ArcherAttachments.Missing(new[] { "hand_l", "bow_grip" }), Is.EquivalentTo(new[] { "hand_r", "string_nock", "arrow_spawn" }));
    }

    [Test]
    public void ClipSet_IsCompleteWithLoopsMarkersAndTerminals()
    {
        var names = ArcherPoseLibrary.All.Select(s => s.Name).ToList();
        Assert.That(names, Is.EquivalentTo(new[] { "Idle", "Draw", "Hold", "Release", "Recover", "Hit", "DodgeLeft", "DodgeRight", "Jump", "Defeat", "Victory" }));
        foreach (ArcherClipSpec s in ArcherPoseLibrary.All)
        {
            Assert.That(s.InPlace, Is.True, s.Name + " root motion comes from the rules' pose");
            Assert.That(s.Keys[0].Key, Is.EqualTo(0));
            Assert.That(s.Keys[^1].Key, Is.EqualTo(1));
            if (s.Loop) Assert.That(ArcherPose.MaxDelta(s.Start, s.End), Is.LessThan(1e-9), s.Name + " loops seamlessly");
        }
        Assert.That(ArcherPoseLibrary.Get(ArcherClip.Idle).Loop && ArcherPoseLibrary.Get(ArcherClip.Hold).Loop, Is.True);
        Assert.That(ArcherPoseLibrary.Get(ArcherClip.Release).Events.Single().FunctionName, Is.EqualTo(ClipEvent.ReleaseArrow));
        Assert.That(ArcherPoseLibrary.Get(ArcherClip.Defeat).Terminal && ArcherPoseLibrary.Get(ArcherClip.Victory).Terminal, Is.True);
        // Automatic transitions join identical poses (no discontinuity without a cross-fade).
        foreach (ArcherClip c in new[] { ArcherClip.Draw, ArcherClip.Release, ArcherClip.Recover, ArcherClip.Hit, ArcherClip.DodgeLeft, ArcherClip.DodgeRight, ArcherClip.Jump })
            Assert.That(ArcherPose.MaxDelta(ArcherPoseLibrary.Get(c).End, ArcherPoseLibrary.Get(ArcherPoseLibrary.Next(c)).Start), Is.LessThan(1e-9), c.ToString());
    }

    [Test]
    public void ReleaseMarker_FiresExactlyOncePerShot_EvenWithLargeSteps()
    {
        var anim = new ArcherAnimator();
        Assert.That(anim.Fire(ArcherTrigger.Release), Is.False, "cannot release from idle");
        Assert.That(anim.Fire(ArcherTrigger.Draw), Is.True);
        Assert.That(anim.Fire(ArcherTrigger.Release), Is.True, "queued until the draw completes");
        int releases = 0;
        for (int i = 0; i < 60; i++) releases += anim.Advance(1 / 30.0).ReleaseMarker ? 1 : 0;
        Assert.That(releases, Is.EqualTo(1));
        Assert.That(anim.State, Is.EqualTo(ArcherClip.Idle));

        Assert.That(anim.Fire(ArcherTrigger.Draw), Is.True);
        Assert.That(anim.Advance(1.0).ReleaseMarker, Is.False);
        Assert.That(anim.State, Is.EqualTo(ArcherClip.Hold));
        Assert.That(anim.Fire(ArcherTrigger.Release), Is.True);
        Assert.That(anim.Advance(5.0).ReleaseMarker, Is.True, "a long frame still reports the marker once");
        Assert.That(anim.Advance(5.0).ReleaseMarker, Is.False);
    }

    [Test]
    public void Animator_PosesAreContinuousAcrossEveryTransition()
    {
        var anim = new ArcherAnimator();
        ArcherPose prev = anim.CurrentPose();
        double worst = 0;
        void Run(double seconds)
        {
            for (double t = 0; t < seconds; t += 1 / 60.0)
            {
                anim.Advance(1 / 60.0);
                ArcherPose now = anim.CurrentPose();
                worst = Math.Max(worst, ArcherPose.MaxBodyDelta(prev, now));
                if (anim.State != ArcherClip.Release) Assert.That(Math.Abs(now.Draw - prev.Draw), Is.LessThan(0.1), "the string only snaps on release");
                prev = now;
            }
        }
        void Fire(ArcherTrigger t)
        {
            anim.Fire(t);
            ArcherPose now = anim.CurrentPose();
            Assert.That(ArcherPose.MaxDelta(prev, now), Is.LessThan(1e-6), "no pose jump at the moment of " + t);
            prev = now;
        }
        Run(0.5); Fire(ArcherTrigger.Draw); Run(0.2); Fire(ArcherTrigger.Release); Run(0.6);
        Fire(ArcherTrigger.DodgeLeft); Run(0.1); Fire(ArcherTrigger.Hit); Run(0.6);
        Fire(ArcherTrigger.Draw); Run(0.5); Fire(ArcherTrigger.Release); Run(0.1); Fire(ArcherTrigger.Jump); Run(0.7);
        Fire(ArcherTrigger.Victory); Run(1.0);
        Assert.That(worst, Is.LessThan(12.0), "largest per-frame change at 60 fps (degrees)");
        Assert.That(anim.IsTerminal, Is.True);
        Assert.That(anim.Fire(ArcherTrigger.Hit), Is.False, "terminal states accept only Reset");
        Assert.That(anim.Fire(ArcherTrigger.Reset), Is.True);
        Assert.That(anim.State, Is.EqualTo(ArcherClip.Idle));
    }

    [Test]
    public void Animator_NeverCancelsAShotBeforeTheArrowLeaves()
    {
        var anim = new ArcherAnimator();
        anim.Fire(ArcherTrigger.Draw);
        Assert.That(anim.Fire(ArcherTrigger.DodgeLeft), Is.False);
        anim.Advance(0.4);
        Assert.That(anim.Fire(ArcherTrigger.Jump), Is.False, "holding a drawn shot");
        anim.Fire(ArcherTrigger.Release);
        Assert.That(anim.Fire(ArcherTrigger.DodgeRight), Is.False, "before the release marker");
        Assert.That(anim.Advance(0.05).ReleaseMarker, Is.True);
        Assert.That(anim.Fire(ArcherTrigger.DodgeRight), Is.True);
    }

    [TestCase(0.0)]
    [TestCase(0.37)]
    [TestCase(1.0)]
    public void Bowstring_KeepsGripNockAndArrowAlignedAtEveryAim(double draw)
    {
        var bow = new BowGeometry();
        foreach (double pitch in new[] { -10.0, 0, 25, 60, 80 })
            foreach (double yaw in new[] { -8.0, 0, 8 })
            {
                double p = pitch * Math.PI / 180, y = yaw * Math.PI / 180;
                var forward = new V3(Math.Cos(p) * Math.Cos(y), Math.Sin(p), Math.Cos(p) * Math.Sin(y));
                var grip = new V3(0.35, 1.35, 0);
                BowstringPoints s = BowstringSolver.Solve(grip, forward, V3.Up, draw, bow);
                Assert.That(V3.DistanceToLine(s.Nock, grip, grip + forward), Is.LessThan(1e-9), "nock on the aim line");
                Assert.That(V3.DistanceToLine(s.ArrowTip, grip, grip + forward), Is.LessThan(1e-9), "arrow on the aim line");
                Assert.That(V3.Distance(s.Nock, grip), Is.EqualTo(bow.BraceHeight + draw * bow.MaxDraw).Within(1e-9));
                Assert.That(V3.Distance(s.TopTip, s.Nock), Is.EqualTo(V3.Distance(s.BottomTip, s.Nock)).Within(1e-9), "symmetric limbs");
            }
        BowstringPoints rest = BowstringSolver.Solve(V3.Zero, V3.Right, V3.Up, 0, bow);
        BowstringPoints full = BowstringSolver.Solve(V3.Zero, V3.Right, V3.Up, 1, bow);
        Assert.That(BowstringSolver.StringLength(full), Is.GreaterThan(BowstringSolver.StringLength(rest)));
        Assert.That(BowstringSolver.Solve(V3.Zero, V3.Right, V3.Up, 7, bow).Nock, Is.EqualTo(full.Nock), "draw is clamped");
    }

    [Test]
    public void BudgetValidator_EnforcesThePlanCeilings()
    {
        var archer = new AssetMetrics
        {
            Name = "archer", Category = AssetCategory.Archer, Triangles = 8001, DeformingBones = 51, MaxBoneWeightsPerVertex = 5,
            Materials = 3, MaxTextureDimension = 2048, TransformNames = new[] { "hand_l" },
        };
        var rules = AssetBudgetValidator.ValidateAsset(archer).Select(v => v.Rule).ToList();
        Assert.That(rules, Is.SupersetOf(new[] { "archer.triangles", "archer.deformingBones", "archer.weightsPerVertex", "archer.materials", "texture.dimension", "archer.attachment.bow_grip" }));
        var ok = new AssetMetrics { Name = "ok", Category = AssetCategory.Archer, Triangles = 8000, DeformingBones = 50, MaxBoneWeightsPerVertex = 4, Materials = 2, MaxTextureDimension = 1024, TransformNames = ArcherAttachments.Required };
        Assert.That(AssetBudgetValidator.ValidateAsset(ok), Is.Empty);
        Assert.That(AssetBudgetValidator.ValidateAsset(new AssetMetrics { Category = AssetCategory.Bow, Triangles = 1501 }).Single().Rule, Is.EqualTo("bow.triangles"));
        Assert.That(AssetBudgetValidator.ValidateAsset(new AssetMetrics { Category = AssetCategory.Arrow, Triangles = 201 }).Single().Rule, Is.EqualTo("arrow.triangles"));
        Assert.That(AssetBudgetValidator.ValidateAsset(new AssetMetrics { Category = AssetCategory.Arena, Triangles = 40001, OpaqueMaterialBatches = 17, RealtimeShadowLights = 1 })
            .Select(v => v.Rule), Is.EquivalentTo(new[] { "arena.triangles", "arena.opaqueBatches", "arena.realtimeShadowLights" }));
        Assert.That(AssetBudgetValidator.ValidateAsset(new AssetMetrics { Category = AssetCategory.UiAtlas, MaxTextureDimension = 2048, LargeTextureApproved = true }), Is.Empty);

        var scene = new SceneMetrics { DrawCalls = 61, ActiveEmitters = 13, MaxLiveParticlesPerEmitter = 65 };
        for (int i = 0; i < 9; i++) scene.Assets.Add(new AssetMetrics { Name = "a" + i, Category = AssetCategory.Other, Triangles = 8000 });
        var sceneRules = AssetBudgetValidator.ValidateScene(scene).Select(v => v.Rule).ToList();
        Assert.That(sceneRules, Is.EquivalentTo(new[] { "scene.triangles", "scene.drawCallCounterUnnamed", "scene.drawCalls", "effects.activeEmitters", "effects.particlesPerEmitter" }));
    }

    // ------------------------------------------------------------------ 32 effects

    [Test]
    public void ElementEffects_AreIdentifiableWithoutColour()
    {
        var styles = ElementEffectStyle.All;
        Assert.That(styles.Select(s => s.Shape), Is.Unique);
        Assert.That(styles.Select(s => s.Motion), Is.Unique);
        for (int i = 0; i < styles.Count; i++)
            for (int j = i + 1; j < styles.Count; j++)
            {
                int differences = (styles[i].Shape != styles[j].Shape ? 1 : 0) + (styles[i].Motion != styles[j].Motion ? 1 : 0) +
                                  (styles[i].ImpactSpokes != styles[j].ImpactSpokes || styles[i].TrailSpacing != styles[j].TrailSpacing ? 1 : 0);
                Assert.That(differences, Is.GreaterThanOrEqualTo(2), styles[i].Element + " vs " + styles[j].Element);
            }
        foreach (EffectKind k in Enum.GetValues<EffectKind>())
            Assert.That(EffectParticles.For(k), Is.InRange(1, AssetBudgets.ParticlesPerEmitter));
    }

    [Test]
    public void EffectPool_RespectsTheConcurrentBudgetAndKeepsOutcomesVisible()
    {
        var pool = new EffectBudgetPool();
        var stolen = new List<double>();
        pool.SlotStolen += s => stolen.Add(s.StartedAt);
        for (int i = 0; i < 12; i++) Assert.That(pool.Acquire(EffectKind.ProjectileTrail, Element.Agni, i), Is.Not.Null);
        Assert.That(pool.ActiveCount, Is.EqualTo(12));
        EffectSlot impact = pool.Acquire(EffectKind.Impact, Element.Varuna, 20, particles: 500)!;
        Assert.That(impact, Is.Not.Null, "an impact steals the oldest trail");
        Assert.That(impact.Particles, Is.EqualTo(AssetBudgets.ParticlesPerEmitter), "particles are clamped");
        Assert.That(stolen.Single(), Is.EqualTo(0), "the oldest trail was reclaimed");
        for (int i = 0; i < 11; i++) pool.Acquire(EffectKind.Clash, Element.Vayu, 30 + i);
        Assert.That(pool.Acquire(EffectKind.ProjectileTrail, Element.Agni, 50), Is.Null, "decoration never displaces outcomes");
        Assert.That(pool.Refused, Is.EqualTo(1));
        Assert.That(pool.ActiveCount, Is.LessThanOrEqualTo(AssetBudgets.ActiveEmitters));
        Assert.That(pool.PeakActive, Is.EqualTo(12));
        Assert.That(pool.LiveParticles, Is.LessThanOrEqualTo(12 * AssetBudgets.ParticlesPerEmitter));
        int generation = impact.Generation;
        pool.Release(impact);
        Assert.That(pool.Acquire(EffectKind.Miss, Element.Prithvi, 60)!.Generation, Is.EqualTo(generation + 1), "slots are reused, not reallocated");
        Assert.Throws<ArgumentOutOfRangeException>(() => new EffectBudgetPool(13));
    }

    // ------------------------------------------------------------------ 31, 33, 34 cues

    [Test]
    public void Cues_AgreeWithTheAuthoritativeRecord()
    {
        LocalizationTable en = En();
        var seen = new HashSet<CueKind>();
        foreach (VolleyResult r in BotVolleys.Value)
        {
            List<VolleyCue> cues = VolleyCueBuilder.Build(r);
            foreach (VolleyCue c in cues)
            {
                seen.Add(c.Kind);
                Assert.That(c.Glyph, Is.Not.Empty);
                Assert.That(en.Contains(c.CaptionKey), Is.True, c.CaptionKey);
            }
            Assert.That(cues.Select(c => c.TimeSubTicks), Is.Ordered);
            foreach (PlayerSide target in new[] { PlayerSide.A, PlayerSide.B })
            {
                int expected = r.Explanation[target].Incoming.Where(c => !c.Blocked).Sum(c => c.DamageUnits + c.ChainBonusUnits);
                Assert.That(cues.Where(c => c.Kind == CueKind.Impact && c.Side == target).Sum(c => c.Amount), Is.EqualTo(expected), "damage numbers match the record");
                Assert.That(cues.Count(c => c.Kind == CueKind.Blocked && c.Side == target), Is.EqualTo(r.Explanation[target].Incoming.Count(c => c.Blocked)));
                PlayerVolleyReport rep = r.Explanation[target];
                Assert.That(cues.Count(c => c.Kind == CueKind.Dodge && c.Side == target), Is.EqualTo(rep.EffectiveDodge == Dodge.None ? 0 : 1));
                VolleyCue hp = cues.Single(c => c.Kind == CueKind.HealthChange && c.Side == target);
                Assert.That((hp.AmountBefore, hp.Amount), Is.EqualTo((rep.HpBeforeUnits, rep.HpAfterUnits)));
            }
            IReadOnlyList<ProjectileTrack> tracks = r.Simulation?.Tracks ?? Array.Empty<ProjectileTrack>();
            Assert.That(cues.Count(c => c.Kind == CueKind.ClashCancelled), Is.EqualTo(tracks.Count(t => t.Termination == TerminationReason.ClashDestroyed)));
            int terminal = cues.Count(c => c.HasProjectile && (c.Kind == CueKind.ClashCancelled || c.Kind == CueKind.Miss ||
                ((c.Kind == CueKind.Impact || c.Kind == CueKind.Blocked) && c.Contact != ContactKind.Brahmastra)));
            Assert.That(terminal, Is.EqualTo(tracks.Count), "one terminal cue per projectile");
            var plan = new VolleyPresentationPlan(r);
            Assert.That(plan.TotalSeconds, Is.LessThanOrEqualTo(RulesConstants.ResolutionReplayMaxMs / 1000.0 + 1e-9));
            foreach (VolleyCue c in cues) Assert.That(plan.ShowAt(c), Is.InRange(0, plan.LeadInSeconds + plan.Flight.FlightSeconds + 1e-9));
        }
        Assert.That(seen, Is.SupersetOf(new[] { CueKind.Release, CueKind.Impact, CueKind.Miss, CueKind.Dodge, CueKind.HealthChange }));
    }

    [Test]
    public void Cues_ShowClashesAsCancelledArrows()
    {
        // Two Sun Lances fired level at each other meet head-on.
        VolleyResult r = Duel1(new[] { 16 }, new[] { 16 }, new VolleyInput(16, 0, 0, 100, Dodge.None), new VolleyInput(16, 0, 0, 100, Dodge.None));
        List<VolleyCue> cues = VolleyCueBuilder.Build(r);
        Assert.That(cues.Count(c => c.Kind == CueKind.ClashCancelled), Is.EqualTo(r.Simulation.Tracks.Count(t => t.Termination == TerminationReason.ClashDestroyed)));
        Assert.That(cues.Any(c => c.Kind is CueKind.ClashCancelled or CueKind.ClashSurvived), Is.True, "level lances clash");
        Assert.That(cues.Where(c => c.Kind is CueKind.ClashCancelled).All(c => c.Glyph == "✕"), Is.True);
    }

    [Test]
    public void Cues_DodgePoseIsVisibleFromTheStartOfTheWindow()
    {
        VolleyResult r = Duel1(new[] { 1 }, new[] { 2 }, new VolleyInput(1, 80, 0, 100, Dodge.Jump), new VolleyInput(2, 20, 0, 100, Dodge.Left));
        var plan = new VolleyPresentationPlan(r);
        foreach (VolleyCue d in plan.Cues.Where(c => c.Kind == CueKind.Dodge))
        {
            Assert.That(plan.ShowAt(d), Is.EqualTo(0), "the protection is visible for the whole window, as resolved");
            Assert.That(d.Dodge, Is.EqualTo(r.Explanation[d.Side].EffectiveDodge));
        }
        Assert.That(plan.Cues.Count(c => c.Kind == CueKind.Dodge), Is.EqualTo(2));
        Assert.That(plan.Released(0.1), Is.False);
        Assert.That(plan.Released(plan.LeadInSeconds), Is.True);
        Assert.That(plan.SimTimeAt(0), Is.EqualTo(0));
    }

    // ------------------------------------------------------------------ 29 aim

    [Test]
    public void Aim_PreviewAndLockUseTheSameClampedValues()
    {
        var aim = new AimController();
        aim.Reset(3); // Stone Arrow: High trajectory
        QdegRange pr = aim.PitchRange;
        Assert.That(aim.PitchQdeg, Is.EqualTo(pr.Clamp(AimController.DefaultPitchQdeg)));
        AimFeedback f = aim.Nudge(10_000, 0);
        Assert.That(f.PitchAtLimit, Is.True);
        Assert.That(aim.PitchQdeg, Is.EqualTo(pr.Max));
        f = aim.Drag(0, -3); // -1.5 qdeg: accumulates
        Assert.That(aim.PitchQdeg, Is.EqualTo(pr.Max - 1));
        aim.SetPower(10);
        Assert.That(aim.PowerPercent, Is.EqualTo(RulesConstants.MinPowerPercent));
        aim.SetDodge(Dodge.Right);
        var arcs = aim.Preview(PlayerSide.A, 0, 0);
        var direct = TrajectoryPreview.Compute(PlayerSide.A, 3, aim.PitchQdeg, aim.YawQdeg, aim.PowerPercent, 0, 0);
        Assert.That(arcs[0].Select(p => (p.X, p.Y, p.Z)), Is.EqualTo(direct[0].Select(p => (p.X, p.Y, p.Z))));
        VolleyInput locked = aim.Lock()!;
        Assert.That((locked.WeaponId, locked.PitchQdeg, locked.YawQdeg, locked.PowerPercent, locked.Dodge),
            Is.EqualTo((3, aim.PitchQdeg, aim.YawQdeg, aim.PowerPercent, Dodge.Right)));
        Assert.That(aim.Nudge(-4, 0).Changed, Is.False, "locked choices cannot change");
        Assert.That(aim.Lock(), Is.Null, "lock is once");
        DuelState state = DuelState.Start(1, TerrainType.Plain, PlayerSide.B, Loadout.Create(CatalogPreset.Starter, new[] { 3 }), Loadout.Create(CatalogPreset.Starter, new[] { 3 }));
        Assert.DoesNotThrow(() => InputValidator.ValidateChoice(state, PlayerSide.A, locked), "the rules accept exactly what was previewed");
    }

    [Test]
    public void Aim_TicksOncePerWholeDegree()
    {
        var aim = new AimController();
        aim.Reset(1);
        int ticks = 0;
        for (int i = 0; i < 8; i++) ticks += aim.Nudge(1, 0).DegreeTick ? 1 : 0;
        Assert.That(ticks, Is.EqualTo(2), "8 quarter-degrees = 2 degrees");
    }

    // ------------------------------------------------------------------ 30 reveal

    [Test]
    public void Reveal_SecretChoicesAppearOnlyInThePermittedPhase()
    {
        foreach (HostStage stage in Enum.GetValues<HostStage>())
            foreach (PlayerSide owner in new[] { PlayerSide.A, PlayerSide.B })
                foreach (PlayerSide? stageSide in new PlayerSide?[] { null, PlayerSide.A, PlayerSide.B })
                {
                    // Two people on one phone: before resolution only the entrant may see their own choice.
                    ChoiceVisibility v = RevealPolicy.For(owner, stage, stageSide, SeatKind.Human, SeatKind.Human, ownerLocked: false);
                    bool preReveal = stage is HostStage.EntryReady or HostStage.Entry or HostStage.Handover or HostStage.LoadoutReady or HostStage.LoadoutEntry;
                    if (preReveal && RevealPolicy.ExposesChoice(v))
                        Assert.That(stageSide == owner && (stage == HostStage.Entry || stage == HostStage.LoadoutEntry), Is.True, owner + " exposed at " + stage + " for " + stageSide);
                    if (stage is HostStage.EntryReady or HostStage.Handover) Assert.That(v, Is.EqualTo(ChoiceVisibility.Hidden));
                    // A bot's choice is never shown before the reveal.
                    ChoiceVisibility bot = RevealPolicy.For(owner, stage, stageSide, SeatKind.Bot, SeatKind.Human, ownerLocked: true);
                    if (preReveal) Assert.That(RevealPolicy.ExposesChoice(bot), Is.False);
                }
        Assert.That(RevealPolicy.For(PlayerSide.A, HostStage.Resolution, null, SeatKind.Human, SeatKind.Human, true, veiledFromViewer: true), Is.EqualTo(ChoiceVisibility.RevealedVeiled));
        Assert.That(RevealPolicy.For(PlayerSide.A, HostStage.Entry, PlayerSide.B, SeatKind.Human, SeatKind.Bot, true), Is.EqualTo(ChoiceVisibility.OwnLocked), "alone against a bot");
    }

    // ------------------------------------------------------------------ 35 HUD

    [Test]
    public void Hud_IsReadableAtEveryTextScaleAndLanguage()
    {
        var snap = new PublicSnapshot
        {
            Phase = MatchPhase.Selection, Stage = HostStage.Entry, Round = 3, Volley = 2, HpA = 7450, HpB = 10000, CellsA = 26000, CellsB = 25040,
            LockedA = true, StageSecondsRemaining = 2.4,
            StatusA = StatusWithEverything(), StatusB = null!,
        };
        foreach (string lang in Localizer.SupportedLanguages)
        {
            var loc = new Localizer(En());
            loc.AddLanguage(LocalizationTable.Parse(lang, File.ReadAllText(Path.Combine(Paths.Localization, lang + ".txt"))));
            loc.Language = lang;
            foreach (float scale in Settings.SettingsModel.TextScales)
            {
                HudState hud = HudModel.Build(snap, loc, s => s == PlayerSide.A ? "Asha" : "Bala", scale, timed: true);
                Assert.That(hud.TimerUrgent, Is.True);
                Assert.That(hud.A.HpFraction, Is.EqualTo(0.745).Within(1e-9));
                Assert.That(hud.A.Statuses, Has.Count.EqualTo(5));
                Assert.That(hud.A.ReadyTag, Is.EqualTo(loc.Get("hud.ready")));
                Assert.That(hud.B.ReadyTag, Is.EqualTo(loc.Get("hud.waiting")), "only the ready flag, never the choice");
                double width = HudModel.EstimateWidth(hud.A.StatusLine, scale);
                Assert.That(width, Is.LessThanOrEqualTo(HudModel.PlayerBoxWidth), lang + " at " + scale + " fits (compact when needed)");
                foreach (string g in HudModel.StatusGlyphs)
                    Assert.That(g.All(ch => FontCoverage.Plan.Any(r => r.Contains(ch))), Is.True, "glyph " + g + " is in a planned font");
            }
        }
    }

    private static PlayerStatus StatusWithEverything()
    {
        var s = new PlayerStatus();
        foreach (string p in new[] { "BurnDue", "ShockDue", "NetDue", "QuakeDue", "IronWallActive" })
            typeof(PlayerStatus).GetProperty(p)!.SetValue(s, true);
        return s;
    }

    // ------------------------------------------------------------------ 36 camera and arenas

    [Test]
    public void Arenas_HaveNoMisleadingCoverAndFitTheBudget()
    {
        Assert.That(ArenaVariants.All.Select(v => v.Id), Is.EquivalentTo(new[] { ArenaVariants.Courtyard, ArenaVariants.Riverside }));
        foreach (ArenaVariant v in ArenaVariants.All)
        {
            Assert.That(ArenaVariants.Validate(v), Is.Empty);
            Assert.That(En().Contains(v.NameKey), Is.True);
        }
        var bad = new ArenaVariant("bad", "x", V3.Zero, new[] { V3.Zero }, new[] { new ArenaProp("Crate", "Cube", new V3(4, 0.5, 0), V3.Zero + new V3(1, 1, 1), 0) });
        Assert.That(ArenaVariants.Validate(bad).Single(), Does.Contain("flight volume"));
        var front = new ArenaVariant("front", "x", V3.Zero, new[] { V3.Zero }, new[] { new ArenaProp("Tree", "Cylinder", new V3(4, 1, -6), new V3(1, 1, 1), 0) });
        Assert.That(ArenaVariants.Validate(front).Single(), Does.Contain("between the camera"));
    }

    [Test]
    public void CameraShake_IsOffWithReducedMotion()
    {
        var shake = new CameraShake();
        shake.Kick();
        V3 o = shake.Update(1 / 30.0);
        Assert.That(o.Length, Is.GreaterThan(0).And.LessThanOrEqualTo(CameraShake.MaxOffsetMetres));
        for (int i = 0; i < 30; i++) o = shake.Update(1 / 30.0);
        Assert.That(o, Is.EqualTo(V3.Zero), "the shake settles");
        shake.Reduced = true;
        shake.Kick();
        Assert.That(shake.Update(1 / 30.0), Is.EqualTo(V3.Zero));
    }
}
