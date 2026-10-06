using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using AstraKingdoms.Rules.Match;
using AstraKingdoms.Rules.Replay;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AstraKingdoms.EditorTools.Release
{
    /// <summary>
    /// Ticket 76: release-configuration Android build for a release candidate. Unlike
    /// <c>Forge.Build.BuildAndroid</c> (development APK for device checks), this produces the
    /// **release-configuration AAB** (no Development flag, so the automation interface is compiled
    /// out) into its own output folder with a manifest and the exported build report, so two runs
    /// can be compared byte by byte (<c>AstraKingdoms.Release repro-compare</c>).
    /// <para>The artifact is unsigned for store purposes: Unity signs it with the debug key unless a
    /// keystore is configured, and **no keystore is ever configured here**. Store signing happens only
    /// in Forge's separate signer after owner approval (<c>python -m forge.release.signer</c>).</para>
    /// <para>Batch: <c>-executeMethod AstraKingdoms.EditorTools.Release.ReleaseCandidateBuild.BuildFromCommandLine
    /// -releaseOutput Builds/Release/a [-versionCode 7] [-versionName 1.0.0] [-apk]</c>.</para>
    /// </summary>
    public static class ReleaseCandidateBuild
    {
        public const string DefaultOutput = "Builds/Release/candidate";
        public const string ManifestName = "release-build-manifest.json";

        [MenuItem("Astra Kingdoms/Release/Build Release Candidate (AAB)")]
        public static void BuildFromMenu() => Build(DefaultOutput, null, null, appBundle: true);

        public static void BuildFromCommandLine()
        {
            int code = 1;
            try
            {
                string versionCode = ReleaseGate.ArgValue("-versionCode");
                code = Build(ReleaseGate.ArgValue("-releaseOutput") ?? DefaultOutput,
                    versionCode != null ? int.Parse(versionCode, System.Globalization.CultureInfo.InvariantCulture) : (int?)null,
                    ReleaseGate.ArgValue("-versionName"), appBundle: !ReleaseGate.HasArg("-apk")) ? 0 : 1;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            EditorApplication.Exit(code);
        }

        public static bool Build(string outputDir, int? versionCode, string versionName, bool appBundle)
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            ProjectConfigurator.ConfigureProject();
            if (versionCode.HasValue) PlayerSettings.Android.bundleVersionCode = versionCode.Value;
            if (!string.IsNullOrEmpty(versionName)) PlayerSettings.bundleVersion = versionName;
            string scene = GreyBoxSceneBuilder.BuildAndSave();
            EditorUserBuildSettings.buildAppBundle = appBundle;

            Directory.CreateDirectory(outputDir);
            string artifact = Path.Combine(outputDir, appBundle ? "astra.aab" : "astra.apk");
            DateTime started = DateTime.UtcNow;
            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { scene },
                locationPathName = artifact,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,
            });
            bool ok = report.summary.result == BuildResult.Succeeded && File.Exists(artifact);
            BuildReportExporter.Write(report, Path.Combine(outputDir, "build-report.json"));
            var m = JsonNode.Object()
                .Add("format", "AK-RELEASE-BUILD/1")
                .Add("succeeded", ok)
                .Add("commit", Git("rev-parse HEAD"))
                .Add("commit_dirty", Git("status --porcelain").Length > 0)
                .Add("unity_version", Application.unityVersion)
                .Add("timestamp_utc", started.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture))
                .Add("configuration", "release")
                .Add("application_id", ProjectConfigurator.ApplicationId)
                .Add("version_name", PlayerSettings.bundleVersion)
                .Add("version_code", PlayerSettings.Android.bundleVersionCode)
                .Add("rules_version", AstraKingdoms.Rules.Core.RulesConstants.RulesVersion)
                .Add("rules_hash", RulesBundle.HashHex)
                .Add("artifact", artifact.Replace('\\', '/'))
                .Add("artifact_bytes", File.Exists(artifact) ? new FileInfo(artifact).Length : 0)
                .Add("artifact_sha256", File.Exists(artifact) ? Sha256(artifact) : string.Empty)
                .Add("signing", "debug key or none; store signing only in the separate Forge signer");
            ReleaseGate.WriteJson(m, Path.Combine(outputDir, ManifestName));
            Debug.Log("[Release] " + (ok ? "Built " : "FAILED ") + artifact);
            return ok;
        }

        private static string Sha256(string file)
        {
            using (var sha = SHA256.Create())
            using (FileStream s = File.OpenRead(file))
                return Hex.Encode(sha.ComputeHash(s));
        }

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
    }
}
