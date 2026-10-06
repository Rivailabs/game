using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Rules.Combat
{
    /// <summary>
    /// The one authoritative volley resolution order (plan: "One authoritative volley resolution order"):
    /// <list type="number">
    /// <item>Snapshot inputs and state.</item>
    /// <item>Cleanse: a valid Tide selection removes its owner's due Burn and Shock.</item>
    /// <item>Due statuses: remaining Burn; Shock suppression; Quake pitch shift clamped to the central range.</item>
    /// <item>Defensive activation: unsuppressed Ash Shield and Iron Wall; terrain effects of this duel.</item>
    /// <item>Dodge: Storm Net forces None, else Cyclone reverses Left/Right; legal dodge; Forest charge.</item>
    /// <item>Launch and simulate (no health changes).</item>
    /// <item>Contact defence: Ash Shield first; else Flood cover removal, cover, damage.</item>
    /// <item>Hit effects: Burn, Push, Shock, Veil, Net, Quake, Ocean; Chain Bolt bonus.</item>
    /// <item>Simultaneous health update for both players from the snapshot.</item>
    /// <item>Settle: KO/draw/winner, otherwise queue statuses, expire due ones and advance.</item>
    /// </list>
    /// </summary>
    public static class VolleyResolver
    {
        private const int QuakeShiftQdeg = 5 * RulesConstants.QuarterDegreesPerDegree;

        /// <summary>Per-player working data for one volley.</summary>
        private sealed class Side
        {
            public PlayerSide Id;
            public VolleyInput Input;
            public WeaponDefinition Weapon; // null for Pass / Brahmastra
            public bool AbilityActive;      // weapon ability usable (intrinsic, or not suppressed)
            public int Pitch;
            public Dodge Dodge;
            public bool ForestActive;
            public bool ShieldAvailable;
            public bool BurnDue;
            public bool HitEffectsDone;
            public int Direct, Ocean, River;
            public PlayerVolleyReport Report;

            public bool Has(WeaponAbility ability) => Weapon != null && AbilityActive && Weapon.Ability == ability;
        }

        /// <summary>Resolves a volley with full flight simulation.</summary>
        public static VolleyResult Resolve(DuelState state, VolleyInput inputA, VolleyInput inputB) =>
            ResolveCore(state, inputA, inputB, null);

        /// <summary>
        /// Resolves a volley using caller-supplied geometric contacts instead of flight simulation.
        /// Every rule after step 6 runs unchanged. Intended for rules fixtures, tutorials and tooling;
        /// contacts must be consistent with the inputs (owner fired that projectile; grazes only
        /// against a legal dodge; bursts only from Boulder; Brahmastra only from its user).
        /// </summary>
        public static VolleyResult ResolveWithScriptedContacts(DuelState state, VolleyInput inputA, VolleyInput inputB,
            IReadOnlyList<GeometricContact> contacts)
        {
            if (contacts == null) throw new ArgumentNullException(nameof(contacts));
            return ResolveCore(state, inputA, inputB, contacts);
        }

        private static VolleyResult ResolveCore(DuelState state, VolleyInput inputA, VolleyInput inputB, IReadOnlyList<GeometricContact> scripted)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (inputA == null) throw new ArgumentNullException(nameof(inputA));
            if (inputB == null) throw new ArgumentNullException(nameof(inputB));
            if (state.IsOver) throw new RulesViolationException(InputValidator.DuelOver, "The duel has already ended.");
            InputValidator.ValidateChoice(state, PlayerSide.A, inputA);
            InputValidator.ValidateChoice(state, PlayerSide.B, inputB);

            // ---- 1. Snapshot ----
            DuelState next = state.Clone();
            int n = state.VolleyIndex;
            int round = state.RoundIndex;
            var log = new CombatEventLog();
            var a = NewSide(PlayerSide.A, inputA, state);
            var b = NewSide(PlayerSide.B, inputB, state);
            Side[] sides = { a, b };
            Side Other(Side s) => s == a ? b : a;

            // ---- 2. Cleanse ----
            foreach (var s in sides)
            {
                if (s.Weapon == null || s.Weapon.Ability != WeaponAbility.Cleanse) continue;
                PlayerDuelState p = next[s.Id];
                s.Report.CleansedBurn = p.BurnDueVolley == n;
                s.Report.CleansedShock = p.ShockDueVolley == n;
                if (s.Report.CleansedBurn) p.BurnDueVolley = 0;
                if (s.Report.CleansedShock) p.ShockDueVolley = 0;
                if (s.Report.CleansedBurn || s.Report.CleansedShock)
                    log.Add(Pre(CombatEventType.Cleansed, s.Id, amount: (s.Report.CleansedBurn ? 1 : 0) | (s.Report.CleansedShock ? 2 : 0)));
            }

            // ---- 3. Due statuses ----
            foreach (var s in sides)
            {
                PlayerDuelState p = next[s.Id];
                s.BurnDue = p.BurnDueVolley == n;
                s.Report.Shocked = p.ShockDueVolley == n;
                if (s.Weapon != null)
                {
                    s.AbilityActive = s.Weapon.AbilityIsIntrinsic || !s.Report.Shocked;
                    s.Report.AbilitySuppressed = !s.AbilityActive;
                    if (s.Report.AbilitySuppressed) log.Add(Pre(CombatEventType.AbilitySuppressed, s.Id, status: StatusKind.Shock, amount: s.Weapon.Id));
                    if (p.QuakeDueVolley == n)
                    {
                        s.Pitch = LaunchProfiles.CentralPitchRange(s.Weapon).Clamp(s.Input.PitchQdeg + QuakeShiftQdeg);
                        s.Report.QuakeApplied = true;
                        log.Add(Pre(CombatEventType.QuakePitchShift, s.Id, status: StatusKind.Quake, amount: s.Pitch));
                    }
                }
                s.Report.EffectivePitchQdeg = s.Pitch;
            }

            // ---- 4. Defensive activation (terrain effects are read from the duel state) ----
            foreach (var s in sides)
            {
                if (s.Has(WeaponAbility.AshShield))
                {
                    s.ShieldAvailable = true;
                    s.Report.AshShieldRaised = true;
                    log.Add(Pre(CombatEventType.AshShieldRaised, s.Id));
                }
                if (s.Has(WeaponAbility.IronWall))
                {
                    PlayerDuelState p = next[s.Id];
                    p.IronWallFromVolley = n;
                    p.IronWallUntilVolley = n + 1;
                    s.Report.IronWallRaised = true;
                    log.Add(Pre(CombatEventType.IronWallRaised, s.Id, status: StatusKind.IronWall, amount: n + 1));
                }
                if (s.Id == state.Defender && state.Terrain == TerrainType.River) s.River = DamageCalculator.RiverHealUnits;
            }

            // ---- 5. Dodge resolution ----
            foreach (var s in sides)
            {
                Dodge d = s.Weapon == null ? Dodge.None : s.Input.Dodge;
                if (d != Dodge.None && next[s.Id].NetDueVolley == n)
                {
                    d = Dodge.None;
                    s.Report.DodgeForcedByNet = true;
                    log.Add(Pre(CombatEventType.DodgeForcedByNet, s.Id, status: StatusKind.Net));
                }
                else if ((d == Dodge.Left || d == Dodge.Right) && Other(s).Has(WeaponAbility.ReverseDodge))
                {
                    d = d == Dodge.Left ? Dodge.Right : Dodge.Left;
                    s.Report.DodgeReversedByCyclone = true;
                    log.Add(Pre(CombatEventType.DodgeReversedByCyclone, s.Id, amount: (int)d));
                }
                s.Dodge = d;
                s.Report.EffectiveDodge = d;
                if ((d == Dodge.Left || d == Dodge.Right) && s.Id == state.Defender &&
                    state.Terrain == TerrainType.Forest && next.ForestChargeAvailable)
                {
                    next.ForestChargeAvailable = false;
                    s.ForestActive = true;
                    s.Report.ForestActive = true;
                    log.Add(Pre(CombatEventType.ForestChargeSpent, s.Id));
                }
            }

            // ---- 6. Launch and simulate ----
            var specs = new List<ProjectileSpec>();
            var strikes = new List<ScheduledStrike>();
            foreach (var s in sides)
            {
                if (s.Input.IsBrahmastra)
                {
                    next[s.Id].BrahmastraAvailable = false;
                    strikes.Add(new ScheduledStrike(new ProjectileId(round, n, s.Id, 0), ScheduledStrike.BrahmastraTick));
                    log.Add(Pre(CombatEventType.BrahmastraSpent, s.Id));
                }
                if (s.Weapon == null) continue;
                specs.AddRange(LaunchProfiles.BuildPattern(round, n, s.Id, s.Weapon, s.Pitch, s.Input.YawQdeg, s.Input.PowerPercent,
                    state[s.Id].BaselineOffsetRight, jumpPierceActive: s.Has(WeaponAbility.JumpPierce)));
            }
            var poseA = new TargetPose(PlayerSide.A, state.A.BaselineOffsetRight, a.Dodge);
            var poseB = new TargetPose(PlayerSide.B, state.B.BaselineOffsetRight, b.Dodge);

            SimulationResult sim = null;
            IReadOnlyList<GeometricContact> contacts;
            if (scripted == null)
            {
                sim = FlightSimulator.Simulate(specs, poseA, poseB, strikes, log);
                contacts = sim.Contacts;
                foreach (var t in sim.Tracks) (t.Spec.Owner == PlayerSide.A ? a : b).Report.AddProjectile(t);
            }
            else
            {
                contacts = CanonicalScripted(scripted, sides, round, n);
            }

            // ---- 7 & 8. Contact defence and hit effects (no health changes yet) ----
            var queued = new List<Tuple<StatusKind, PlayerSide>>(); // status, player it lands on
            foreach (var c in contacts)
            {
                Side atk = c.Attacker == PlayerSide.A ? a : b;
                Side tgt = Other(atk);
                var report = new ContactReport { Projectile = c.Projectile, Kind = c.Kind, Tick = c.Tick, SubTick = c.SubTick };
                tgt.Report.AddIncoming(report);

                if (tgt.ShieldAvailable)
                {
                    tgt.ShieldAvailable = false;
                    tgt.Report.AshShieldConsumed = true;
                    report.Blocked = true;
                    log.Add(Res(CombatEventType.ShieldBlock, tgt.Id, c));
                    continue;
                }

                if (c.Kind == ContactKind.Brahmastra)
                {
                    // Neutral, no graze, bypasses dodge, cover and Forest; no other effects.
                    report.DamageUnits = ScheduledStrike.BrahmastraDamageUnits;
                    tgt.Direct += report.DamageUnits;
                    atk.Report.LandedHit = true;
                    log.Add(Res(CombatEventType.Damage, tgt.Id, c, amount: report.DamageUnits));
                    continue;
                }

                WeaponDefinition w = atk.Weapon;
                PlayerDuelState tp = next[tgt.Id];
                if (atk.Has(WeaponAbility.RemoveCover))
                {
                    bool removed = false;
                    if (tgt.Id == next.Defender && next.FortCoverActive)
                    {
                        next.FortCoverRemoved = true;
                        removed = true;
                    }
                    if (tp.IronWallActiveIn(n))
                    {
                        tp.IronWallFromVolley = tp.IronWallUntilVolley = 0;
                        removed = true;
                    }
                    if (removed)
                    {
                        report.CoverRemovedByFlood = true;
                        log.Add(Res(CombatEventType.CoverRemoved, tgt.Id, c));
                    }
                }

                bool covered = (tgt.Id == next.Defender && next.FortCoverActive) || tp.IronWallActiveIn(n);
                report.TargetCovered = covered;
                report.CoverIgnored = covered && atk.Has(WeaponAbility.IgnoreCover);
                report.CoverFactor = DamageCalculator.CoverFactor(covered && !report.CoverIgnored);
                report.ElementFactor = DamageCalculator.ElementFactor(w.Element, tgt.Input.DefensiveElement, atk.Has(WeaponAbility.ThunderAdvantage));
                report.DodgeFactor = DamageCalculator.DodgeFactor(c.Kind, tgt.ForestActive);
                report.ForestApplied = tgt.ForestActive;
                report.DamageUnits = DamageCalculator.ContactDamage(w.DamagePerProjectileUnits, report.ElementFactor, report.DodgeFactor, report.CoverFactor);
                tgt.Direct += report.DamageUnits;
                log.Add(Res(CombatEventType.Damage, tgt.Id, c, amount: report.DamageUnits));
                if (!report.IsHit) continue;

                bool bodyHit = c.Kind == ContactKind.Core || c.Kind == ContactKind.Graze;
                if (bodyHit && covered && atk.Has(WeaponAbility.ChainBonus))
                {
                    report.ChainBonusUnits = DamageCalculator.ChainBonusUnits;
                    tgt.Direct += report.ChainBonusUnits;
                    log.Add(Res(CombatEventType.ChainBonus, tgt.Id, c, amount: report.ChainBonusUnits));
                }

                atk.Report.LandedHit = true;
                if (atk.HitEffectsDone) continue; // once-on-hit abilities trigger at most once per owner per volley
                atk.HitEffectsDone = true;
                if (!atk.AbilityActive) continue;
                switch (w.Ability)
                {
                    case WeaponAbility.Burn: queued.Add(Tuple.Create(StatusKind.Burn, tgt.Id)); break;
                    case WeaponAbility.Push: queued.Add(Tuple.Create(StatusKind.Push, tgt.Id)); break;
                    case WeaponAbility.Shock: queued.Add(Tuple.Create(StatusKind.Shock, tgt.Id)); break;
                    case WeaponAbility.Veil: queued.Add(Tuple.Create(StatusKind.Veil, atk.Id)); break;
                    case WeaponAbility.Net: queued.Add(Tuple.Create(StatusKind.Net, tgt.Id)); break;
                    case WeaponAbility.Quake: queued.Add(Tuple.Create(StatusKind.Quake, tgt.Id)); break;
                    case WeaponAbility.OceanHeal: atk.Ocean = DamageCalculator.OceanHealUnits; break;
                }
            }

            // ---- 9. Simultaneous health update from the snapshot ----
            foreach (var s in sides)
            {
                PlayerDuelState p = next[s.Id];
                int burn = s.BurnDue ? DamageCalculator.BurnUnits : 0;
                int before = state[s.Id].HpUnits;
                p.HpUnits = DamageCalculator.ApplyHealthBatch(before, s.Direct, burn, s.Ocean, s.River);
                if (burn > 0) log.Add(Post(CombatEventType.BurnDamage, s.Id, status: StatusKind.Burn, amount: burn));
                if (s.Ocean > 0) log.Add(Post(CombatEventType.Heal, s.Id, heal: HealSource.Ocean, amount: s.Ocean));
                if (s.River > 0) log.Add(Post(CombatEventType.Heal, s.Id, heal: HealSource.River, amount: s.River));
                log.Add(Post(CombatEventType.HealthUpdate, s.Id, amount: p.HpUnits));

                PlayerVolleyReport r = s.Report;
                r.HpBeforeUnits = before;
                r.DirectDamageUnits = s.Direct;
                r.BurnDamageUnits = burn;
                r.OceanHealUnits = s.Ocean;
                r.RiverHealUnits = s.River;
                r.HpAfterUnits = p.HpUnits;
            }

            // ---- 10. Settle ----
            next.Result = Decide(next.A.HpUnits, next.B.HpUnits, n);
            if (next.Result == DuelResult.InProgress)
            {
                int v = n + 1;
                foreach (var q in queued)
                {
                    PlayerDuelState p = next[q.Item2];
                    switch (q.Item1)
                    {
                        case StatusKind.Burn: p.BurnDueVolley = v; break;
                        case StatusKind.Shock: p.ShockDueVolley = v; break;
                        case StatusKind.Net: p.NetDueVolley = v; break;
                        case StatusKind.Quake: p.QuakeDueVolley = v; break;
                        case StatusKind.Veil: p.VeilVolley = v; break;
                        case StatusKind.Push:
                            p.BaselineOffsetRight = Fixed.Clamp(p.BaselineOffsetRight + CombatGeometry.PushStep,
                                -CombatGeometry.MaxBaselineOffset, CombatGeometry.MaxBaselineOffset);
                            log.Add(Post(CombatEventType.BaselinePushed, q.Item2, status: StatusKind.Push,
                                position: new FixedVector3(Fixed.Zero, Fixed.Zero, p.BaselineOffsetRight)));
                            break;
                    }
                    (q.Item2 == PlayerSide.A ? a : b).Report.AddQueued(q.Item1);
                    if (q.Item1 != StatusKind.Push) log.Add(Post(CombatEventType.StatusQueued, q.Item2, status: q.Item1, amount: v));
                }
                foreach (var s in sides) ExpireDue(next[s.Id], n, log);
                next.VolleyIndex = v;
            }
            else
            {
                // Nothing scheduled for a later volley carries into the next duel.
                next.A.ClearStatuses();
                next.B.ClearStatuses();
                log.Add(Post(CombatEventType.DuelEnded, next.Result == DuelResult.BWins ? PlayerSide.B : PlayerSide.A, amount: (int)next.Result));
            }

            var explanation = new VolleyExplanation { Round = round, Volley = n, A = a.Report, B = b.Report, ResultAfter = next.Result };
            return new VolleyResult { NewState = next, Log = log, Simulation = sim, Explanation = explanation };
        }

        /// <summary>Sole survivor wins; both at zero is a draw; after volley 3 higher HP wins, equal is a draw.</summary>
        public static DuelResult Decide(int hpA, int hpB, int volley)
        {
            if (hpA == 0 && hpB == 0) return DuelResult.Draw;
            if (hpA == 0) return DuelResult.BWins;
            if (hpB == 0) return DuelResult.AWins;
            if (volley < RulesConstants.MaxVolleys) return DuelResult.InProgress;
            if (hpA == hpB) return DuelResult.Draw;
            return hpA > hpB ? DuelResult.AWins : DuelResult.BWins;
        }

        private static Side NewSide(PlayerSide id, VolleyInput input, DuelState state)
        {
            WeaponDefinition w = input.IsRegular ? WeaponCatalog.Get(input.WeaponId) : null;
            var report = new PlayerVolleyReport
            {
                Side = id,
                WeaponId = input.WeaponId,
                WeaponName = w != null ? w.Name : input.IsBrahmastra ? "Brahmastra" : "Pass",
                DefensiveElement = input.DefensiveElement,
                IsPass = input.IsPass,
                IsBrahmastra = input.IsBrahmastra,
                SubmittedPitchQdeg = input.PitchQdeg,
                SubmittedDodge = input.Dodge,
                ConcealedFromOpponent = state[id].VeilVolley == state.VolleyIndex,
            };
            return new Side { Id = id, Input = input, Weapon = w, Pitch = input.PitchQdeg, Report = report };
        }

        private static void ExpireDue(PlayerDuelState p, int n, CombatEventLog log)
        {
            void Expire(StatusKind kind, int due, Action clear)
            {
                if (due != n) return;
                clear();
                log.Add(Post(CombatEventType.StatusExpired, p.Side, status: kind, amount: n));
            }
            Expire(StatusKind.Burn, p.BurnDueVolley, () => p.BurnDueVolley = 0);
            Expire(StatusKind.Shock, p.ShockDueVolley, () => p.ShockDueVolley = 0);
            Expire(StatusKind.Net, p.NetDueVolley, () => p.NetDueVolley = 0);
            Expire(StatusKind.Quake, p.QuakeDueVolley, () => p.QuakeDueVolley = 0);
            Expire(StatusKind.Veil, p.VeilVolley, () => p.VeilVolley = 0);
            if (p.IronWallFromVolley != 0)
                Expire(StatusKind.IronWall, p.IronWallUntilVolley, () => p.IronWallFromVolley = p.IronWallUntilVolley = 0);
        }

        /// <summary>Validates scripted contacts and sorts them into authoritative order.</summary>
        private static IReadOnlyList<GeometricContact> CanonicalScripted(IReadOnlyList<GeometricContact> scripted, Side[] sides, int round, int n)
        {
            var list = new List<GeometricContact>(scripted);
            var seen = new HashSet<ProjectileId>();
            foreach (var c in list)
            {
                if (c.Projectile.Round != round || c.Projectile.Volley != n)
                    throw new ArgumentException("Scripted contact " + c + " belongs to another round or volley.");
                if (!seen.Add(c.Projectile)) throw new ArgumentException("Projectile " + c.Projectile + " contacts twice.");
                Side atk = sides[(int)c.Attacker];
                Side tgt = sides[1 - (int)c.Attacker];
                if (c.Kind == ContactKind.Brahmastra)
                {
                    if (!atk.Input.IsBrahmastra || c.Projectile.Index != 0) throw new ArgumentException("Brahmastra contact without Brahmastra: " + c);
                    continue;
                }
                if (atk.Weapon == null || c.Projectile.Index < 0 || c.Projectile.Index >= atk.Weapon.ProjectileCount)
                    throw new ArgumentException("Scripted contact " + c + " was not fired.");
                bool burst = c.Kind == ContactKind.BurstFull || c.Kind == ContactKind.BurstGraze;
                if (burst != (atk.Weapon.Ability == WeaponAbility.GroundBurst))
                    throw new ArgumentException("Burst contacts come only from Boulder Shot: " + c);
                if ((c.Kind == ContactKind.Graze || c.Kind == ContactKind.BurstGraze) && tgt.Dodge == Dodge.None)
                    throw new ArgumentException("A graze requires a legal dodge: " + c);
            }
            // Time, then body before ground (bursts are ground contacts), then projectile id.
            list.Sort((x, y) =>
            {
                int t = x.TimeSubTicks.CompareTo(y.TimeSubTicks);
                if (t != 0) return t;
                int px = IsGround(x.Kind) ? 1 : 0, py = IsGround(y.Kind) ? 1 : 0;
                return px != py ? px.CompareTo(py) : x.Projectile.CompareTo(y.Projectile);
            });
            return list;
        }

        private static bool IsGround(ContactKind k) => k == ContactKind.BurstFull || k == ContactKind.BurstGraze;

        private static CombatEvent Pre(CombatEventType type, PlayerSide subject, StatusKind status = StatusKind.None, int amount = 0) =>
            new CombatEvent(type, subject, false, default, 0, 0, status: status, amount: amount);

        private static CombatEvent Res(CombatEventType type, PlayerSide target, GeometricContact c, int amount = 0) =>
            new CombatEvent(type, target, true, c.Projectile, c.Tick, c.SubTick, c.Kind, position: c.Position, amount: amount, distance: c.Distance);

        private static CombatEvent Post(CombatEventType type, PlayerSide subject, StatusKind status = StatusKind.None,
            HealSource heal = HealSource.None, int amount = 0, FixedVector3 position = default) =>
            new CombatEvent(type, subject, false, default, 0, 0, status: status, healSource: heal, amount: amount, position: position);
    }
}
