using System;
using System.Collections.Generic;

namespace AstraKingdoms.Client.Presentation
{
    /// <summary>Budget category of an asset (plan: "Initial asset budgets for the Astra template").</summary>
    public enum AssetCategory : byte
    {
        Other = 0,
        Archer = 1,
        Bow = 2,
        Arrow = 3,
        /// <summary>The visible arena set (ground, architecture, props) as one measured group.</summary>
        Arena = 4,
        Effect = 5,
        /// <summary>A shared UI atlas: may exceed 1,024 px only with a recorded measured approval.</summary>
        UiAtlas = 6,
    }

    /// <summary>Measured facts about one asset (gathered by the editor validator from meshes, rigs and materials).</summary>
    public sealed class AssetMetrics
    {
        public string Name = "";
        public AssetCategory Category;
        public int Triangles;
        /// <summary>Bones that actually deform vertices (non-zero weights).</summary>
        public int DeformingBones;
        public int MaxBoneWeightsPerVertex;
        public int Materials;
        /// <summary>Distinct opaque material batches (arena only).</summary>
        public int OpaqueMaterialBatches;
        public int MaxTextureDimension;
        /// <summary>Recorded measured approval for a texture above 1,024 px (UI atlases only).</summary>
        public bool LargeTextureApproved;
        public int RealtimeShadowLights;
        /// <summary>Transform names of the asset (for the archer attachment contract).</summary>
        public IReadOnlyList<string> TransformNames = Array.Empty<string>();
    }

    /// <summary>Scene-level measurements of the declared representative combat scene.</summary>
    public sealed class SceneMetrics
    {
        public List<AssetMetrics> Assets = new List<AssetMetrics>();
        /// <summary>Engine draw-call counter value, if measured (null = not measured yet; never estimated).</summary>
        public int? DrawCalls;
        /// <summary>Which engine counter produced <see cref="DrawCalls"/> (plan: name the counter).</summary>
        public string DrawCallCounter;
        public int ActiveEmitters;
        public int MaxLiveParticlesPerEmitter;
    }

    /// <summary>One broken ceiling.</summary>
    public sealed class BudgetViolation
    {
        public string Rule { get; }
        public string Asset { get; }
        public long Actual { get; }
        public long Limit { get; }

        public BudgetViolation(string rule, string asset, long actual, long limit)
        {
            Rule = rule;
            Asset = asset;
            Actual = actual;
            Limit = limit;
        }

        public override string ToString() => Rule + " - " + Asset + ": " + Actual + " (limit " + Limit + ")";
    }

    /// <summary>
    /// The proposed production ceilings (plan: "Initial asset budgets for the Astra template"). They
    /// make briefs concrete; the physical-phone frame-time and memory gates remain the acceptance
    /// authority, so passing here never means "performs well".
    /// </summary>
    public static class AssetBudgets
    {
        public const int ArcherTriangles = 8000;
        public const int ArcherDeformingBones = 50;
        public const int ArcherWeightsPerVertex = 4;
        public const int ArcherMaterials = 2;
        public const int BowTriangles = 1500;
        public const int ArrowTriangles = 200;
        public const int ArenaTriangles = 40000;
        public const int ArenaOpaqueBatches = 16;
        public const int SceneTriangles = 70000;
        public const int SceneDrawCalls = 60;
        public const int TextureDimension = 1024;
        public const int ActiveEmitters = 12;
        public const int ParticlesPerEmitter = 64;
    }

    /// <summary>Checks measured assets and scenes against <see cref="AssetBudgets"/> (ticket 26 tooling).</summary>
    public static class AssetBudgetValidator
    {
        public static List<BudgetViolation> ValidateAsset(AssetMetrics m)
        {
            if (m == null) throw new ArgumentNullException(nameof(m));
            var v = new List<BudgetViolation>();
            void Max(string rule, long actual, long limit)
            {
                if (actual > limit) v.Add(new BudgetViolation(rule, m.Name, actual, limit));
            }

            switch (m.Category)
            {
                case AssetCategory.Archer:
                    Max("archer.triangles", m.Triangles, AssetBudgets.ArcherTriangles);
                    Max("archer.deformingBones", m.DeformingBones, AssetBudgets.ArcherDeformingBones);
                    Max("archer.weightsPerVertex", m.MaxBoneWeightsPerVertex, AssetBudgets.ArcherWeightsPerVertex);
                    Max("archer.materials", m.Materials, AssetBudgets.ArcherMaterials);
                    foreach (string missing in ArcherAttachments.Missing(m.TransformNames))
                        v.Add(new BudgetViolation("archer.attachment." + missing, m.Name, 0, 1));
                    break;
                case AssetCategory.Bow:
                    Max("bow.triangles", m.Triangles, AssetBudgets.BowTriangles);
                    break;
                case AssetCategory.Arrow:
                    Max("arrow.triangles", m.Triangles, AssetBudgets.ArrowTriangles);
                    break;
                case AssetCategory.Arena:
                    Max("arena.triangles", m.Triangles, AssetBudgets.ArenaTriangles);
                    Max("arena.opaqueBatches", m.OpaqueMaterialBatches, AssetBudgets.ArenaOpaqueBatches);
                    Max("arena.realtimeShadowLights", m.RealtimeShadowLights, 0);
                    break;
            }
            if (m.Category != AssetCategory.UiAtlas || !m.LargeTextureApproved)
                Max("texture.dimension", m.MaxTextureDimension, AssetBudgets.TextureDimension);
            return v;
        }

        public static List<BudgetViolation> ValidateScene(SceneMetrics s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            var v = new List<BudgetViolation>();
            long triangles = 0;
            foreach (AssetMetrics a in s.Assets)
            {
                v.AddRange(ValidateAsset(a));
                triangles += a.Triangles;
            }
            if (triangles > AssetBudgets.SceneTriangles) v.Add(new BudgetViolation("scene.triangles", "scene", triangles, AssetBudgets.SceneTriangles));
            if (s.DrawCalls.HasValue)
            {
                if (string.IsNullOrEmpty(s.DrawCallCounter)) v.Add(new BudgetViolation("scene.drawCallCounterUnnamed", "scene", s.DrawCalls.Value, 0));
                if (s.DrawCalls.Value > AssetBudgets.SceneDrawCalls) v.Add(new BudgetViolation("scene.drawCalls", "scene", s.DrawCalls.Value, AssetBudgets.SceneDrawCalls));
            }
            if (s.ActiveEmitters > AssetBudgets.ActiveEmitters) v.Add(new BudgetViolation("effects.activeEmitters", "scene", s.ActiveEmitters, AssetBudgets.ActiveEmitters));
            if (s.MaxLiveParticlesPerEmitter > AssetBudgets.ParticlesPerEmitter)
                v.Add(new BudgetViolation("effects.particlesPerEmitter", "scene", s.MaxLiveParticlesPerEmitter, AssetBudgets.ParticlesPerEmitter));
            return v;
        }

        /// <summary>
        /// Triangle counts of Unity's built-in primitive meshes, for grey-box estimates before a scene
        /// exists (from knowledge of the meshes; the editor validator counts the real meshes).
        /// </summary>
        public static int PrimitiveTriangles(string primitive)
        {
            switch (primitive)
            {
                case "Cube": return 12;
                case "Quad": return 2;
                case "Plane": return 200;
                case "Cylinder": return 80;
                case "Sphere": return 768;
                case "Capsule": return 832;
                default: throw new ArgumentOutOfRangeException(nameof(primitive));
            }
        }
    }
}
