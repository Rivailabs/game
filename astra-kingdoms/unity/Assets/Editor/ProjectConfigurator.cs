using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace AstraKingdoms.EditorTools
{
    /// <summary>
    /// Project and Android player settings applied from code, so the large ProjectSettings YAML files
    /// never need hand edits: package id, IL2CPP, ARM64 only, minimum SDK, landscape, the Activity
    /// entry point the Forge device service launches, and a URP pipeline asset for the grey box.
    /// Values marked PROPOSED must be confirmed during preflight against the reference phone.
    /// </summary>
    public static class ProjectConfigurator
    {
        public const string CompanyName = "Rivai Labs";
        public const string ProductName = "Astra Kingdoms";
        public const string ApplicationId = "com.rivailabs.astrakingdoms";
        public const string BundleVersion = "0.1.0-pilot";
        /// <summary>PROPOSED: Unity 6.0's documented minimum; confirm against the 2 GB reference phone.</summary>
        public const AndroidSdkVersions MinSdk = AndroidSdkVersions.AndroidApiLevel23;
        public const string UrpAssetPath = "Assets/Settings/AstraURP.asset";
        public const string UrpRendererPath = "Assets/Settings/AstraURP_Renderer.asset";

        [MenuItem("Astra Kingdoms/Configure Project (Android)")]
        public static void ConfigureProject()
        {
            PlayerSettings.companyName = CompanyName;
            PlayerSettings.productName = ProductName;
            PlayerSettings.bundleVersion = BundleVersion;
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, ApplicationId);
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = MinSdk;
            // The Forge device service launches com.unity3d.player.UnityPlayerActivity.
            PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.Activity;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            EnsureUrpAsset();
            AssetDatabase.SaveAssets();
            Debug.Log("[Configure] " + ApplicationId + " IL2CPP ARM64 minSdk " + MinSdk);
        }

        /// <summary>
        /// Creates and assigns a Universal Render Pipeline asset when none is assigned. Failure is
        /// logged, not fatal: the grey box still renders with the built-in pipeline.
        /// </summary>
        public static void EnsureUrpAsset()
        {
            try
            {
                if (GraphicsSettings.defaultRenderPipeline != null) return;
                GreyBoxSceneBuilder.EnsureFolder("Assets/Settings");
                var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(UrpAssetPath);
                if (asset == null)
                {
                    var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                    AssetDatabase.CreateAsset(renderer, UrpRendererPath);
                    asset = UniversalRenderPipelineAsset.Create(renderer);
                    AssetDatabase.CreateAsset(asset, UrpAssetPath);
                }
                GraphicsSettings.defaultRenderPipeline = asset;
                QualitySettings.renderPipeline = asset;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Configure] URP asset not created (" + ex.Message + "); using the built-in pipeline.");
            }
        }
    }
}
