using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;

namespace AstraKingdoms.Rules.Match
{
    /// <summary>One weapon row as it enters the rules hash.</summary>
    public sealed class WeaponRow
    {
        public int Id;
        public string Name;
        public int Element;
        public int DamageUnits;
        public int ProjectileCount;
        public int Speed;
        public int Trajectory;
        public int Mass;
        public int RadiusMm;
        public int Ability;
        public int UnlockLevel;
        public int PitchMinQdeg, PitchMaxQdeg, YawMinQdeg, YawMaxQdeg;
        public long BaseSpeedRaw;

        public static WeaponRow From(WeaponDefinition w)
        {
            QdegRange pitch = LaunchProfiles.CentralPitchRange(w);
            QdegRange yaw = LaunchProfiles.CentralYawRange(w);
            return new WeaponRow
            {
                Id = w.Id, Name = w.Name, Element = (int)w.Element, DamageUnits = w.DamagePerProjectileUnits,
                ProjectileCount = w.ProjectileCount, Speed = (int)w.Speed, Trajectory = (int)w.Trajectory,
                Mass = w.MassPerProjectile, RadiusMm = w.RadiusMm, Ability = (int)w.Ability, UnlockLevel = w.UnlockLevel,
                PitchMinQdeg = pitch.Min, PitchMaxQdeg = pitch.Max, YawMinQdeg = yaw.Min, YawMaxQdeg = yaw.Max,
                BaseSpeedRaw = LaunchProfiles.BaseSpeed(w.Speed).Raw,
            };
        }

        internal void WriteTo(CanonicalWriter w) =>
            w.I32(Id).Ascii(Name).U8(Element).I32(DamageUnits).I32(ProjectileCount).U8(Speed).U8(Trajectory)
             .I32(Mass).I32(RadiusMm).U8(Ability).I32(UnlockLevel)
             .I32(PitchMinQdeg).I32(PitchMaxQdeg).I32(YawMinQdeg).I32(YawMaxQdeg).I64(BaseSpeedRaw);
    }

    /// <summary>
    /// The complete input of the rules hash as plain editable data. <see cref="Current"/> captures
    /// the compiled AK-TR-1 rules; tests and tooling may alter a copy to prove that any change to a
    /// constant, weapon value, card cap, trig table or terrain template changes the hash.
    /// </summary>
    public sealed class RulesBundleContents
    {
        /// <summary>Format tag of the canonical encoding itself.</summary>
        public const string EncodingTag = "AK-RULES-BUNDLE/1";

        public string RulesVersion;
        /// <summary>Named integer constants in a fixed order (names are part of the encoding).</summary>
        public List<KeyValuePair<string, long>> Constants = new List<KeyValuePair<string, long>>();
        public List<WeaponRow> Weapons = new List<WeaponRow>();
        /// <summary>(card wire ID, cap percent) in ascending card ID.</summary>
        public List<KeyValuePair<int, int>> CardCaps = new List<KeyValuePair<int, int>>();
        public string TrigTableVersion;
        public byte[] TrigTableBytes;
        public string TrigTableSha256Hex;
        public string EnvelopeTrigTableVersion;
        public List<long> EnvelopeCos = new List<long>();
        public List<long> EnvelopeSin = new List<long>();
        /// <summary>(template ID, SHA-256 of its 65,536 terrain bytes) for every shipped template.</summary>
        public List<KeyValuePair<string, byte[]>> TerrainTemplates = new List<KeyValuePair<string, byte[]>>();
        public List<string> StreamLabels = new List<string>();

        /// <summary>Snapshot of the compiled rules.</summary>
        public static RulesBundleContents Current()
        {
            var c = new RulesBundleContents { RulesVersion = RulesConstants.RulesVersion };
            void K(string name, long value) => c.Constants.Add(new KeyValuePair<string, long>(name, value));

            // Board, match and duel.
            K("BoardSize", RulesConstants.BoardSize);
            K("ActiveCells", RulesConstants.ActiveCells);
            K("InitialCellsPerPlayer", RulesConstants.InitialCellsPerPlayer);
            K("VictoryCells", RulesConstants.VictoryCells);
            K("MaxRounds", RulesConstants.MaxRounds);
            K("MaxVolleys", RulesConstants.MaxVolleys);
            K("HpUnitsPerHp", RulesConstants.HpUnitsPerHp);
            K("StartHpUnits", RulesConstants.StartHpUnits);
            // Loadouts and inputs.
            K("StarterMaxSlots", RulesConstants.StarterMaxSlots);
            K("FullMaxSlots", RulesConstants.FullMaxSlots);
            K("MinSlots", RulesConstants.MinSlots);
            K("RegularWeaponCount", RulesConstants.RegularWeaponCount);
            K("StarterWeaponCount", RulesConstants.StarterWeaponCount);
            K("BrahmastraWeaponId", RulesConstants.BrahmastraWeaponId);
            K("PassWeaponId", RulesConstants.PassWeaponId);
            K("QuarterDegreesPerDegree", RulesConstants.QuarterDegreesPerDegree);
            K("MinYawQdeg", RulesConstants.MinYawQdeg);
            K("MaxYawQdeg", RulesConstants.MaxYawQdeg);
            K("MinPowerPercent", RulesConstants.MinPowerPercent);
            K("MaxPowerPercent", RulesConstants.MaxPowerPercent);
            // Land.
            K("QuotaFloorPpm", RulesConstants.QuotaFloorPpm);
            K("QuotaDenominator", RulesConstants.QuotaDenominator);
            K("VajraMinExclusiveDiffUnits", RulesConstants.VajraMinExclusiveDiffUnits);
            K("MaxCutVertices", RulesConstants.MaxCutVertices);
            K("RotationSteps", RulesConstants.RotationSteps);
            K("MinScaleQuarters", RulesConstants.MinScaleQuarters);
            K("MaxScaleQuarters", RulesConstants.MaxScaleQuarters);
            K("V1OfferSize", CardOffers.V1OfferSize);
            // Timing.
            K("TerrainAnnouncementMs", RulesConstants.TerrainAnnouncementMs);
            K("ChoiceDeadlineMs", RulesConstants.ChoiceDeadlineMs);
            K("SharedPhoneHandoverMs", RulesConstants.SharedPhoneHandoverMs);
            K("ResolutionReplayMaxMs", RulesConstants.ResolutionReplayMaxMs);
            K("OnlineCutWindowMs", RulesConstants.OnlineCutWindowMs);
            K("SharedPhoneCutWindowMs", RulesConstants.SharedPhoneCutWindowMs);
            K("ConsecutiveTimeoutsToForfeit", RulesConstants.ConsecutiveTimeoutsToForfeit);
            // Physics and damage.
            K("TicksPerSecond", RulesConstants.TicksPerSecond);
            K("MaxTicksPerVolley", RulesConstants.MaxTicksPerVolley);
            K("SubTicksPerTick", RulesConstants.SubTicksPerTick);
            K("FixedFractionBits", Fixed.FractionBits);
            K("GravityRaw", LaunchProfiles.Gravity.Raw);
            K("TwinCurveAccelRightRaw", LaunchProfiles.TwinCurveAccelRight.Raw);
            K("TwinCurveLastTick", LaunchProfiles.TwinCurveLastTick);
            K("TwinYawOffsetQdeg", LaunchProfiles.TwinYawOffsetQdeg);
            K("QuakeShiftQdeg", 5 * RulesConstants.QuarterDegreesPerDegree);
            K("PlaneXARaw", CombatGeometry.PlaneXA.Raw);
            K("PlaneXBRaw", CombatGeometry.PlaneXB.Raw);
            K("LaunchForwardRaw", CombatGeometry.LaunchForward.Raw);
            K("LaunchHeightRaw", CombatGeometry.LaunchHeight.Raw);
            K("CapsuleBottomRaw", CombatGeometry.CapsuleBottom.Raw);
            K("CapsuleTopRaw", CombatGeometry.CapsuleTop.Raw);
            K("CoreRadiusRaw", CombatGeometry.CoreRadius.Raw);
            K("GrazeRadiusRaw", CombatGeometry.GrazeRadius.Raw);
            K("SideDodgeShiftRaw", CombatGeometry.SideDodgeShift.Raw);
            K("JumpRaiseRaw", CombatGeometry.JumpRaise.Raw);
            K("BurstFullRadiusRaw", CombatGeometry.BurstFullRadius.Raw);
            K("BurstGrazeRadiusRaw", CombatGeometry.BurstGrazeRadius.Raw);
            K("PushStepRaw", CombatGeometry.PushStep.Raw);
            K("MaxBaselineOffsetRaw", CombatGeometry.MaxBaselineOffset.Raw);
            K("BoundsMinXRaw", CombatGeometry.MinX.Raw);
            K("BoundsMaxXRaw", CombatGeometry.MaxX.Raw);
            K("BoundsMaxYRaw", CombatGeometry.MaxY.Raw);
            K("BoundsMinZRaw", CombatGeometry.MinZ.Raw);
            K("BoundsMaxZRaw", CombatGeometry.MaxZ.Raw);
            K("ChainBonusUnits", DamageCalculator.ChainBonusUnits);
            K("BurnUnits", DamageCalculator.BurnUnits);
            K("OceanHealUnits", DamageCalculator.OceanHealUnits);
            K("RiverHealUnits", DamageCalculator.RiverHealUnits);
            K("BrahmastraTick", ScheduledStrike.BrahmastraTick);
            K("BrahmastraDamageUnits", ScheduledStrike.BrahmastraDamageUnits);

            foreach (WeaponDefinition w in WeaponCatalog.All) c.Weapons.Add(WeaponRow.From(w));
            foreach (CardId card in Cards.All) c.CardCaps.Add(new KeyValuePair<int, int>((int)card, Cards.CapPercent(card)));

            c.TrigTableVersion = TrigTable.Version;
            c.TrigTableBytes = TrigTable.GetTableBytes();
            c.TrigTableSha256Hex = TrigTable.Sha256Hex;
            c.EnvelopeTrigTableVersion = CardEnvelope.TrigTableVersion;
            c.EnvelopeCos.AddRange(CardEnvelope.CosTable);
            c.EnvelopeSin.AddRange(CardEnvelope.SinTable);

            c.TerrainTemplates.Add(new KeyValuePair<string, byte[]>(Land.TerrainTemplates.PlainId, TemplateHash(Land.TerrainTemplates.PlainOnly)));
            c.TerrainTemplates.Add(new KeyValuePair<string, byte[]>(Land.TerrainTemplates.FullId, TemplateHash(Land.TerrainTemplates.FullMirrored)));

            c.StreamLabels.Add(Core.StreamLabels.Initiative);
            c.StreamLabels.Add(Core.StreamLabels.Map);
            c.StreamLabels.Add(Core.StreamLabels.Terrain);
            c.StreamLabels.Add(Core.StreamLabels.Cards);
            c.StreamLabels.Add(Core.StreamLabels.SeedCommitment);
            return c;
        }

        /// <summary>SHA-256 over a template's 65,536 terrain IDs in cell-ID order.</summary>
        public static byte[] TemplateHash(TerrainTemplate template)
        {
            TerrainType[] cells = template.ToArray();
            var bytes = new byte[cells.Length];
            for (int i = 0; i < cells.Length; i++) bytes[i] = (byte)cells[i];
            return Hashing.Sha256(bytes);
        }

        /// <summary>The canonical byte encoding hashed into the rules hash.</summary>
        public byte[] Encode()
        {
            var w = new CanonicalWriter();
            w.Ascii(EncodingTag).Ascii(RulesVersion);
            w.U32((uint)Constants.Count);
            foreach (var k in Constants) w.Named(k.Key, k.Value);
            w.U32((uint)Weapons.Count);
            foreach (var row in Weapons) row.WriteTo(w);
            w.U32((uint)CardCaps.Count);
            foreach (var cap in CardCaps) w.I32(cap.Key).I32(cap.Value);
            w.Ascii(TrigTableVersion).Block(TrigTableBytes).Ascii(TrigTableSha256Hex);
            w.Ascii(EnvelopeTrigTableVersion).U32((uint)EnvelopeCos.Count);
            foreach (long v in EnvelopeCos) w.I64(v);
            w.U32((uint)EnvelopeSin.Count);
            foreach (long v in EnvelopeSin) w.I64(v);
            w.U32((uint)TerrainTemplates.Count);
            foreach (var t in TerrainTemplates) w.Ascii(t.Key).Block(t.Value);
            w.U32((uint)StreamLabels.Count);
            foreach (string label in StreamLabels) w.Ascii(label);
            return w.ToArray();
        }

        public byte[] ComputeHash() => Hashing.Sha256(Encode());
    }

    /// <summary>
    /// The versioned rules bundle (ticket 20): <see cref="Hash"/> is SHA-256 over the canonical
    /// encoding of the rules version, every numeric constant, the weapon table, card caps, the
    /// combat trig table bytes, the envelope rotation table and the shipped terrain templates.
    /// Commands carry this 32-byte hash; records pin it; replays with another hash are rejected.
    /// </summary>
    public static class RulesBundle
    {
        private static readonly Lazy<byte[]> HashLazy = new Lazy<byte[]>(() => RulesBundleContents.Current().ComputeHash());

        public const string Version = RulesConstants.RulesVersion;

        /// <summary>The 32-byte rules hash (a fresh copy on every read).</summary>
        public static byte[] Hash => (byte[])HashLazy.Value.Clone();

        public static string HashHex => Hex.Encode(HashLazy.Value);

        /// <summary>The canonical encoding of the compiled rules.</summary>
        public static byte[] CanonicalEncoding() => RulesBundleContents.Current().Encode();

        /// <summary>True when <paramref name="hash"/> equals this build's rules hash.</summary>
        public static bool Matches(byte[] hash) => Hashing.Equal(hash, HashLazy.Value);
    }
}
