using System;
using System.Collections.Generic;
using AstraKingdoms.Client;
using AstraKingdoms.Client.Arena;
using AstraKingdoms.Client.Presentation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AstraKingdoms.EditorTools
{
    /// <summary>
    /// Ticket 36: builds one scene per grey-box arena treatment (courtyard, riverside) from the
    /// engine-independent layout, each with the same fighters, camera and bootstrap as the duel
    /// scene. They are measurement scenes for the "both arenas pass representative frame-time
    /// checks" acceptance; the shipped Duel scene switches treatments at run time
    /// (<see cref="ArenaView.SetVariant"/>), so the build settings keep the single Duel scene.
    /// <para>Menu: Astra Kingdoms/Build Arena Variant Scenes. Batch:
    /// <c>-executeMethod AstraKingdoms.EditorTools.ArenaVariantSceneBuilder.BuildFromCommandLine</c></para>
    /// </summary>
    public static class ArenaVariantSceneBuilder
    {
        public static string ScenePath(string variantId) => GreyBoxSceneBuilder.ScenesFolder + "/Arena_" + variantId + ".unity";

        [MenuItem("Astra Kingdoms/Build Arena Variant Scenes")]
        public static void BuildFromMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            foreach (string path in BuildAll()) Debug.Log("[Arena] Built " + path);
        }

        public static void BuildFromCommandLine()
        {
            try
            {
                BuildAll();
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorApplication.Exit(1);
            }
        }

        public static List<string> BuildAll()
        {
            GreyBoxSceneBuilder.EnsureFolder(GreyBoxSceneBuilder.ScenesFolder);
            var paths = new List<string>();
            foreach (ArenaVariant v in ArenaVariants.All)
            {
                IReadOnlyList<string> problems = ArenaVariants.Validate(v);
                if (problems.Count > 0) throw new InvalidOperationException(string.Join("; ", problems));
                Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Populate(v.Id);
                string path = ScenePath(v.Id);
                if (!EditorSceneManager.SaveScene(scene, path)) throw new InvalidOperationException("Could not save " + path);
                paths.Add(path);
            }
            AssetDatabase.SaveAssets();
            return paths;
        }

        /// <summary>Adds one treatment's arena, fighters, light, camera and bootstrap to the active scene.</summary>
        public static void Populate(string variantId)
        {
            ArenaBuilder.CreateArena(variantId);
            Camera cam = Camera.main;
            if (cam != null) cam.backgroundColor = ArenaVariantBuilder.Sky(variantId);
            var boot = new GameObject("GameBootstrap");
            boot.AddComponent<GameBootstrap>();
        }
    }
}
