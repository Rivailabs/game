using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using AstraKingdoms.EditorTools;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Forge
{
    /// <summary>
    /// Build entry points invoked by Game Forge's Unity adapter
    /// (<c>-executeMethod Forge.Build.BuildAndroid</c>, see game-forge/forge/checks/unity.py). Every
    /// build regenerates the grey-box scene, applies the Android configuration, builds
    /// <c>Builds/Android/astra.apk</c> and writes <c>Builds/Android/build-manifest.json</c> (commit,
    /// Unity version, timestamp, configuration, rules hash, artifact hash). Development builds are
    /// the default (they contain the automation interface); pass <c>-releaseBuild</c> (or set
    /// AK_RELEASE_BUILD=1) for a release-configuration measurement build.
    /// </summary>
    public static class Build
    {
        public const string OutputDirectory = "Builds/Android";
        public const string ApkName = "astra.apk";
        public const string ManifestName = "build-manifest.json";

        [MenuItem("Astra Kingdoms/Build Android (development)")]
        public static void BuildAndroidFromMenu() => Run(development: true, exitWhenDone: false);

        [MenuItem("Astra Kingdoms/Build Android (release configuration)")]
        public static void BuildAndroidReleaseFromMenu() => Run(development: false, exitWhenDone: false);

        /// <summary>Batch-mode entry point. Exits 0 when the APK and manifest were produced, 1 otherwise.</summary>
        public static void BuildAndroid()
        {
            bool release = HasArg("-releaseBuild") || Environment.GetEnvironmentVariable("AK_RELEASE_BUILD") == "1";
            Run(!release, exitWhenDone: Application.isBatchMode);
        }

        private static void Run(bool development, bool exitWhenDone)
        {
            int code = 1;
            try
            {
                code = BuildPlayer(development) ? 0 : 1;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            if (exitWhenDone) EditorApplication.Exit(code);
        }

        public static bool BuildPlayer(bool development)
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            ProjectConfigurator.ConfigureProject();
            string scene = GreyBoxSceneBuilder.BuildAndSave();
            EditorUserBuildSettings.buildAppBundle = false;

            Directory.CreateDirectory(OutputDirectory);
            string apk = Path.Combine(OutputDirectory, ApkName);
            var options = new BuildPlayerOptions
            {
                scenes = new[] { scene },
                locationPathName = apk,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = development ? BuildOptions.Development : BuildOptions.None,
            };
            DateTime started = DateTime.UtcNow;
            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;
            bool ok = summary.result == BuildResult.Succeeded && File.Exists(apk);
            WriteManifest(Path.Combine(OutputDirectory, ManifestName), apk, development, started, summary, ok);
            Debug.Log("[ForgeBuild] " + (ok ? "Succeeded" : "Failed") + ": " + summary.result + " errors=" + summary.totalErrors + " size=" + summary.totalSize);
            return ok;
        }

        private static void WriteManifest(string path, string apk, bool development, DateTime started, BuildSummary summary, bool ok)
        {
            var m = JsonNode.Object();
            m.Add("format", "AK-BUILD-MANIFEST/1");
            m.Add("succeeded", ok);
            m.Add("result", summary.result.ToString());
            m.Add("commit", Git("rev-parse HEAD"));
            m.Add("commit_dirty", Git("status --porcelain").Length > 0);
            m.Add("unity_version", Application.unityVersion);
            m.Add("timestamp_utc", started.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));
            m.Add("duration_s", (long)Math.Round((DateTime.UtcNow - started).TotalSeconds));
            m.Add("development_build", development);
            m.Add("application_id", ProjectConfigurator.ApplicationId);
            m.Add("bundle_version", PlayerSettings.bundleVersion);
            m.Add("scripting_backend", "IL2CPP");
            m.Add("architectures", "ARM64");
            m.Add("min_sdk", ProjectConfigurator.MinSdk.ToString());
            m.Add("rules_version", AstraKingdoms.Rules.Core.RulesConstants.RulesVersion);
            m.Add("rules_hash", RulesBundle.HashHex);
            m.Add("artifact", apk.Replace('\\', '/'));
            m.Add("artifact_bytes", File.Exists(apk) ? new FileInfo(apk).Length : 0);
            m.Add("artifact_sha256", File.Exists(apk) ? Sha256(apk) : string.Empty);
            m.Add("errors", summary.totalErrors);
            File.WriteAllText(path, m.ToCanonicalString());
        }

        private static string Sha256(string file)
        {
            using (var sha = SHA256.Create())
            using (FileStream s = File.OpenRead(file))
                return Hex.Encode(sha.ComputeHash(s));
        }

        /// <summary>Runs git in the project folder; returns "" when git is unavailable (recorded as unknown).</summary>
        private static string Git(string args)
        {
            string env = Environment.GetEnvironmentVariable("FORGE_COMMIT");
            if (args.StartsWith("rev-parse", StringComparison.Ordinal) && !string.IsNullOrEmpty(env)) return env;
            try
            {
                var psi = new ProcessStartInfo("git", args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Directory.GetCurrentDirectory(),
                };
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(10000);
                    return p.ExitCode == 0 ? output : string.Empty;
                }
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static bool HasArg(string name)
        {
            foreach (string a in Environment.GetCommandLineArgs())
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
