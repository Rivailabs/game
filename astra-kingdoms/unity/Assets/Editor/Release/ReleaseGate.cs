using System;
using System.Collections.Generic;
using System.IO;
using AstraKingdoms.Client.Assets;
using AstraKingdoms.Client.Presentation;
using AstraKingdoms.Release.Gates;
using AstraKingdoms.Rules.Replay;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AstraKingdoms.EditorTools.Release
{
    /// <summary>
    /// Editor release gate (tickets 68-71, 74 and the plan's "Release" gate row): runs the checks
    /// that need the Unity editor and writes one <c>AK-GATE-RESULT/1</c> file per run, which the
    /// release tool's <c>release-record</c> command collects with the other evidence.
    /// <list type="number">
    /// <item>Asset ledger: the runtime ledger and the placeholder ledger validate
    /// (<see cref="AssetLedger.Validate"/>); in store-release mode a remaining placeholder fails.</item>
    /// <item>Asset budgets (existing ticket 26 tooling): both arena treatments are built into scratch
    /// scenes and measured with <see cref="AssetBudgetValidatorMenu.MeasureScene"/> and
    /// <see cref="ArenaVariants.Validate"/>.</item>
    /// <item>Import presets: <see cref="ArtImportPolicy.Audit"/> reports textures and clips under
    /// Assets/Art that differ from the mobile policy.</item>
    /// <item>Android player settings: ARM64 only.</item>
    /// </list>
    /// Batch: <c>-executeMethod AstraKingdoms.EditorTools.Release.ReleaseGate.RunFromCommandLine
    /// [-storeRelease]</c>; exit 0 pass/warn, 1 fail, 3 incomplete. Passing here never means the build
    /// performs well: the reference-phone gates decide that.
    /// </summary>
    public static class ReleaseGate
    {
        public const string ResultPath = "Builds/release-gate.json";
        public const string RuntimeLedgerPath = "Assets/Resources/Ledger/asset-ledger.json";
        /// <summary>The placeholder ledger lives outside the Unity project (astra-kingdoms/art/ledger).</summary>
        public const string PlaceholderLedgerPath = "../art/ledger/placeholder-assets.ledger.json";

        [MenuItem("Astra Kingdoms/Release/Run Release Gate")]
        public static void RunFromMenu()
        {
            // The budget check builds scratch scenes; let the user keep unsaved work first.
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            GateReport r = Run(storeRelease: false);
            Write(r, ResultPath);
            if (r.Overall == GateStatus.Fail) Debug.LogError(r.ToText());
            else Debug.Log(r.ToText());
        }

        public static void RunFromCommandLine()
        {
            int code = 1;
            try
            {
                GateReport r = Run(HasArg("-storeRelease"));
                Write(r, ResultPath);
                Debug.Log(r.ToText());
                code = r.ExitCode;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            EditorApplication.Exit(code);
        }

        public static GateReport Run(bool storeRelease)
        {
            var r = new GateReport(storeRelease ? "editor release gate (store release)" : "editor release gate");
            CheckLedgers(r, storeRelease);
            CheckBudgets(r);
            List<string> imports = ArtImportPolicy.Audit();
            if (imports.Count == 0) r.Add("imports", GateStatus.Pass, "Assets/Art importers follow the mobile import policy");
            foreach (string p in imports) r.Add("imports", GateStatus.Fail, p);
            r.Add("android.abi", PlayerSettings.Android.targetArchitectures == AndroidArchitecture.ARM64 ? GateStatus.Pass : GateStatus.Fail,
                "target architectures: " + PlayerSettings.Android.targetArchitectures);
            return r;
        }

        private static void CheckLedgers(GateReport r, bool storeRelease)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in new[] { RuntimeLedgerPath, PlaceholderLedgerPath })
            {
                if (!File.Exists(path))
                {
                    r.Add("ledger", GateStatus.Incomplete, path + " not found");
                    continue;
                }
                AssetLedger ledger;
                try
                {
                    ledger = AssetLedger.Parse(File.ReadAllText(path));
                }
                catch (FormatException ex)
                {
                    r.Add("ledger", GateStatus.Fail, path + ": " + ex.Message);
                    continue;
                }
                IReadOnlyList<string> problems = ledger.Validate();
                foreach (string p in problems) r.Add("ledger", GateStatus.Fail, path + ": " + p);
                int placeholders = 0;
                foreach (LedgerEntry e in ledger.Entries)
                {
                    if (e.Id != null && !ids.Add(e.Id)) r.Add("ledger", GateStatus.Fail, e.Id + ": id used in more than one ledger file");
                    if (e.Status == LedgerStatus.Placeholder) placeholders++;
                }
                if (problems.Count == 0) r.Add("ledger", GateStatus.Pass, path + ": " + ledger.Entries.Count + " entries valid");
                if (placeholders > 0)
                    r.Add("ledger.placeholders", storeRelease ? GateStatus.Fail : GateStatus.Warn,
                        path + ": " + placeholders + " placeholder(s) still listed" + (storeRelease ? " (a store release needs approved or removed assets)" : ""));
            }
        }

        private static void CheckBudgets(GateReport r)
        {
            foreach (ArenaVariant v in ArenaVariants.All)
            {
                foreach (string p in ArenaVariants.Validate(v)) r.Add("arena." + v.Id, GateStatus.Fail, p);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                ArenaVariantSceneBuilder.Populate(v.Id);
                SceneMetrics metrics = AssetBudgetValidatorMenu.MeasureScene(SceneManager.GetActiveScene());
                // The editor draw-call counter is not a device measurement (and is meaningless in
                // -nographics batch mode); drop it so it can neither pass nor fail this gate.
                metrics.DrawCalls = null;
                List<BudgetViolation> violations = AssetBudgetValidator.ValidateScene(metrics);
                if (violations.Count == 0) r.Add("budget." + v.Id, GateStatus.Pass, "within the proposed asset ceilings (draw calls measured on the device)");
                foreach (BudgetViolation x in violations) r.Add("budget." + v.Id, GateStatus.Fail, x.ToString());
            }
        }

        public static void Write(GateReport r, string path) => WriteJson(ToJson(r), path);

        public static JsonNode ToJson(GateReport r)
        {
            var findings = JsonNode.Array();
            foreach (GateFinding f in r.Findings)
                findings.Push(JsonNode.Object().Add("id", f.Id).Add("status", GateReport.Label(f.Status)).Add("message", f.Message));
            return JsonNode.Object().Add("format", "AK-GATE-RESULT/1").Add("gate", r.Gate).Add("status", GateReport.Label(r.Overall))
                .Add("unity_version", Application.unityVersion).Add("findings", findings);
        }

        public static void WriteJson(JsonNode node, string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, node.ToCanonicalString());
        }

        internal static bool HasArg(string name)
        {
            foreach (string a in Environment.GetCommandLineArgs())
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static string ArgValue(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }
    }
}
