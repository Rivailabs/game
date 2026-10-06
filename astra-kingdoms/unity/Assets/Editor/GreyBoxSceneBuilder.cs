using System;
using AstraKingdoms.Client;
using AstraKingdoms.Client.Arena;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AstraKingdoms.EditorTools
{
    /// <summary>
    /// Ticket 4: builds the grey-box duel scene entirely from script (no hand-edited scene YAML):
    /// ground plane, capsule fighters A at x = 0 m and B at x = 8 m (the rules' body capsule),
    /// a side-on camera, a directional light and the GameBootstrap entry object. The scene is
    /// regenerated on every Android build so the build always matches this code.
    /// <para>Menu: Astra Kingdoms/Build Grey-box Scene.
    /// Batch: <c>Unity -batchmode -quit -projectPath astra-kingdoms/unity -executeMethod AstraKingdoms.EditorTools.GreyBoxSceneBuilder.BuildFromCommandLine</c></para>
    /// </summary>
    public static class GreyBoxSceneBuilder
    {
        public const string ScenesFolder = "Assets/Scenes";
        public const string DuelScenePath = ScenesFolder + "/Duel.unity";

        [MenuItem("Astra Kingdoms/Build Grey-box Scene")]
        public static void BuildFromMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            string path = BuildAndSave();
            Debug.Log("[GreyBox] Built " + path);
        }

        /// <summary>-executeMethod entry point: exits 0 on success, 1 on failure.</summary>
        public static void BuildFromCommandLine()
        {
            try
            {
                ProjectConfigurator.ConfigureProject();
                BuildAndSave();
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorApplication.Exit(1);
            }
        }

        /// <summary>Creates a new Duel scene, populates it, saves it and registers it in the build settings.</summary>
        public static string BuildAndSave()
        {
            EnsureFolder(ScenesFolder);
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Populate();
            if (!EditorSceneManager.SaveScene(scene, DuelScenePath))
                throw new InvalidOperationException("Could not save " + DuelScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(DuelScenePath, true) };
            AssetDatabase.SaveAssets();
            return DuelScenePath;
        }

        /// <summary>Adds the grey-box objects to the active scene (also used by Edit Mode tests).</summary>
        public static void Populate()
        {
            ArenaBuilder.CreateArena();
            var boot = new GameObject("GameBootstrap");
            boot.AddComponent<GameBootstrap>();
        }

        public static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            int slash = path.LastIndexOf('/');
            string parent = path.Substring(0, slash);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, path.Substring(slash + 1));
        }
    }
}
