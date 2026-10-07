using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AstraKingdoms.Client.Arena;
using AstraKingdoms.Client.Presentation;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AstraKingdoms.EditorTools
{
    /// <summary>
    /// Ticket 26 tooling: measures meshes, skins, materials, textures and lights in the editor and
    /// checks them against the plan's ceilings with <see cref="AssetBudgetValidator"/> (archer 8k
    /// triangles / 50 deforming bones / 4 weights / 2 materials, bow 1.5k, arrow 200, arena 40k and 16
    /// opaque batches, 70k scene triangles, 60 draw calls, textures 1,024 px, 12 emitters x 64
    /// particles). The physical-phone frame-time and memory gates remain the acceptance authority.
    /// <para>Menus: Astra Kingdoms/Validate Asset Budgets (Selection) and (Active Scene). Batch:
    /// <c>-executeMethod AstraKingdoms.EditorTools.AssetBudgetValidatorMenu.ValidateSceneFromCommandLine</c>
    /// writes <c>Builds/asset-budget-report.txt</c> and exits 1 on any violation.</para>
    /// </summary>
    public static class AssetBudgetValidatorMenu
    {
        public const string ReportPath = "Builds/asset-budget-report.txt";

        [MenuItem("Astra Kingdoms/Validate Asset Budgets (Selection)")]
        public static void ValidateSelection()
        {
            GameObject go = Selection.activeGameObject;
            if (go == null)
            {
                Debug.LogWarning("[Budget] Select an archer, bow, arrow or arena root first.");
                return;
            }
            AssetMetrics m = Measure(go, Categorize(go));
            Log(AssetBudgetValidator.ValidateAsset(m), go.name);
        }

        [MenuItem("Astra Kingdoms/Validate Asset Budgets (Active Scene)")]
        public static void ValidateActiveScene() => Log(AssetBudgetValidator.ValidateScene(MeasureScene(SceneManager.GetActiveScene())), "active scene");

        public static void ValidateSceneFromCommandLine()
        {
            try
            {
                List<BudgetViolation> v = AssetBudgetValidator.ValidateScene(MeasureScene(SceneManager.GetActiveScene()));
                Directory.CreateDirectory("Builds");
                File.WriteAllText(ReportPath, Report(v, "active scene"));
                EditorApplication.Exit(v.Count == 0 ? 0 : 1);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorApplication.Exit(1);
            }
        }

        /// <summary>One metrics row per scene root, plus the editor's draw-call counter (named).</summary>
        public static SceneMetrics MeasureScene(Scene scene)
        {
            var s = new SceneMetrics();
            foreach (GameObject root in scene.GetRootGameObjects()) s.Assets.Add(Measure(root, Categorize(root)));
            // UnityStats reflects the last rendered Game view frame in the editor only; device counters
            // come from the profiler capture on the reference phone (plan: name the counter).
            s.DrawCalls = UnityStats.drawCalls;
            s.DrawCallCounter = "UnityEditor.UnityStats.drawCalls (editor Game view; not a device measurement)";
            return s;
        }

        public static AssetCategory Categorize(GameObject go)
        {
            if (go.GetComponentInChildren<ArcherRig>() != null) return AssetCategory.Archer;
            string n = go.name.ToLowerInvariant();
            if (n.Contains("arena") || n.Contains(ArenaVariantBuilder.PropsRootName.ToLowerInvariant())) return AssetCategory.Arena;
            if (n.Contains("bow")) return AssetCategory.Bow;
            if (n.Contains("arrow")) return AssetCategory.Arrow;
            return AssetCategory.Other;
        }

        public static AssetMetrics Measure(GameObject go, AssetCategory category)
        {
            var m = new AssetMetrics { Name = go.name, Category = category };
            var materials = new HashSet<Material>();
            var bones = new HashSet<Transform>();
            foreach (MeshFilter f in go.GetComponentsInChildren<MeshFilter>(true))
                if (f.sharedMesh != null) m.Triangles += f.sharedMesh.triangles.Length / 3;
            foreach (SkinnedMeshRenderer sk in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Mesh mesh = sk.sharedMesh;
                if (mesh == null) continue;
                m.Triangles += mesh.triangles.Length / 3;
                // BoneWeight holds at most four influences; Unity's import "Skin Weights" setting must
                // also be limited to 4 (checked in the model importer review, not here).
                Transform[] skinBones = sk.bones ?? Array.Empty<Transform>();
                foreach (BoneWeight w in mesh.boneWeights)
                {
                    int n = 0;
                    if (w.weight0 > 0f) { n++; AddBone(bones, skinBones, w.boneIndex0); }
                    if (w.weight1 > 0f) { n++; AddBone(bones, skinBones, w.boneIndex1); }
                    if (w.weight2 > 0f) { n++; AddBone(bones, skinBones, w.boneIndex2); }
                    if (w.weight3 > 0f) { n++; AddBone(bones, skinBones, w.boneIndex3); }
                    m.MaxBoneWeightsPerVertex = Mathf.Max(m.MaxBoneWeightsPerVertex, n);
                }
            }
            m.DeformingBones = bones.Count;
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r is LineRenderer) continue;
                foreach (Material mat in r.sharedMaterials)
                {
                    if (mat == null || !materials.Add(mat)) continue;
                    Texture t = mat.mainTexture;
                    if (t != null) m.MaxTextureDimension = Mathf.Max(m.MaxTextureDimension, Mathf.Max(t.width, t.height));
                }
            }
            m.Materials = materials.Count;
            m.OpaqueMaterialBatches = materials.Count;
            foreach (Light l in go.GetComponentsInChildren<Light>(true))
                if (l.shadows != LightShadows.None) m.RealtimeShadowLights++;
            var names = new List<string>();
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true)) names.Add(t.name);
            m.TransformNames = names;
            return m;
        }

        private static void AddBone(HashSet<Transform> bones, Transform[] skinBones, int index)
        {
            if (index >= 0 && index < skinBones.Length && skinBones[index] != null) bones.Add(skinBones[index]);
        }

        private static void Log(List<BudgetViolation> v, string what)
        {
            string report = Report(v, what);
            if (v.Count == 0) Debug.Log(report);
            else Debug.LogWarning(report);
        }

        public static string Report(List<BudgetViolation> v, string what)
        {
            var sb = new StringBuilder();
            sb.Append("[Budget] ").Append(what).Append(": ").Append(v.Count == 0 ? "within the proposed ceilings" : v.Count + " violation(s)").Append('\n');
            foreach (BudgetViolation x in v) sb.Append("  ").Append(x).Append('\n');
            sb.Append("Note: ceilings are proposed; the reference-phone frame-time and memory gates decide acceptance.\n");
            return sb.ToString();
        }
    }
}
