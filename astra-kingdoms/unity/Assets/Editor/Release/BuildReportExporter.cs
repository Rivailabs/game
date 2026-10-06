using System;
using System.Collections.Generic;
using System.IO;
using AstraKingdoms.Rules.Replay;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace AstraKingdoms.EditorTools.Release
{
    /// <summary>
    /// Ticket 74: exports every player build's <see cref="BuildReport"/> as
    /// <c>Builds/build-report.json</c> (format <c>AK-BUILD-REPORT/1</c>) so download-size control can
    /// run outside the editor (<c>AstraKingdoms.Release size-check --build-report ...</c>). It runs as
    /// a post-build callback, so it also covers <c>Forge.Build.BuildAndroid</c> without changing it.
    /// <para>The report lists output files with their roles, the largest packed source assets and the
    /// packed bytes per asset type. Unity's packed sizes are uncompressed contributions inside the
    /// build; the store download is measured separately from the AAB (bundletool), never inferred
    /// from this file.</para>
    /// </summary>
    public sealed class BuildReportExporter : IPostprocessBuildWithReport
    {
        public const string Format = "AK-BUILD-REPORT/1";
        public const string DefaultPath = "Builds/build-report.json";
        public const int TopAssets = 100;

        public int callbackOrder => 1000;

        public void OnPostprocessBuild(BuildReport report)
        {
            try
            {
                Write(report, DefaultPath);
            }
            catch (Exception ex)
            {
                // Never fail a build because the evidence export failed; the size gate reports the
                // missing file as INCOMPLETE instead.
                Debug.LogWarning("[Release] Build report export failed: " + ex.Message);
            }
        }

        public static void Write(BuildReport report, string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, ToJson(report).ToCanonicalString());
            Debug.Log("[Release] Wrote " + path);
        }

        public static JsonNode ToJson(BuildReport report)
        {
            BuildSummary s = report.summary;
            var root = JsonNode.Object();
            root.Add("format", Format);
            root.Add("result", s.result.ToString());
            root.Add("platform", EditorUserBuildSettings.activeBuildTarget.ToString());
            root.Add("app_bundle", EditorUserBuildSettings.buildAppBundle);
            root.Add("output_path", (s.outputPath ?? string.Empty).Replace('\\', '/'));
            root.Add("total_size", (long)s.totalSize);
            root.Add("total_errors", s.totalErrors);
            root.Add("total_warnings", s.totalWarnings);
            root.Add("build_seconds", (long)Math.Round(s.totalTime.TotalSeconds));
            root.Add("unity_version", Application.unityVersion);

            var files = JsonNode.Array();
            foreach (BuildFile f in report.GetFiles() ?? Array.Empty<BuildFile>())
                files.Push(JsonNode.Object().Add("path", (f.path ?? string.Empty).Replace('\\', '/')).Add("role", f.role ?? string.Empty).Add("size", (long)f.size));
            root.Add("files", files);

            var assets = new List<KeyValuePair<string, long>>();
            var byType = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (PackedAssets pa in report.packedAssets ?? Array.Empty<PackedAssets>())
            {
                if (pa == null) continue;
                var perAsset = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (PackedAssetInfo info in pa.contents ?? Array.Empty<PackedAssetInfo>())
                {
                    string src = string.IsNullOrEmpty(info.sourceAssetPath) ? "(built-in or generated)" : info.sourceAssetPath;
                    perAsset.TryGetValue(src, out long have);
                    perAsset[src] = have + (long)info.packedSize;
                    string type = info.type != null ? info.type.Name : "Unknown";
                    byType.TryGetValue(type, out long t);
                    byType[type] = t + (long)info.packedSize;
                }
                foreach (KeyValuePair<string, long> kv in perAsset) assets.Add(kv);
            }
            assets.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key, b.Key));
            var top = JsonNode.Array();
            for (int i = 0; i < assets.Count && i < TopAssets; i++)
                top.Push(JsonNode.Object().Add("path", assets[i].Key).Add("packed_size", assets[i].Value));
            root.Add("top_assets", top);
            var types = JsonNode.Array();
            foreach (KeyValuePair<string, long> kv in byType) types.Push(JsonNode.Object().Add("type", kv.Key).Add("packed_size", kv.Value));
            root.Add("packed_by_type", types);
            return root;
        }
    }
}
