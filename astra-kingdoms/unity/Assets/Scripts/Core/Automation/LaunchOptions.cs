using System;
using System.Collections.Generic;
using System.Globalization;

namespace AstraKingdoms.Client.Automation
{
    /// <summary>
    /// Development-only launch options. Sources: the process command line (editor/desktop:
    /// <c>-autoplay 20261006 -autoplaySpeed 4 -quitAfterAutoplay</c>) and, on Android, intent
    /// extras (<c>adb shell am start ... -e forge_scenario pilot-smoke</c> or <c>-e autoplay 7</c>),
    /// which the Unity layer converts into the same argument list.
    /// </summary>
    public sealed class LaunchOptions
    {
        /// <summary>Scenario name used by the Forge device service.</summary>
        public const string PilotSmokeScenario = "pilot-smoke";
        /// <summary>Approved regression seeds used when a scenario names no seed (ticket 10: several seeds).</summary>
        public static readonly IReadOnlyList<ulong> ApprovedSeeds = new ulong[] { 1, 2, 20261006 };
        /// <summary>Default match-clock multiplier for the pilot-smoke scenario.</summary>
        public const double PilotSmokeSpeed = 8.0;

        public List<ulong> AutoplaySeeds { get; } = new List<ulong>();
        public double AutoplaySpeed { get; private set; } = 1.0;
        public bool QuitAfterAutoplay { get; private set; }
        public string ForgeScenario { get; private set; }
        public string ReplayPath { get; private set; }

        public bool AutoplayRequested => AutoplaySeeds.Count > 0;

        public static LaunchOptions Parse(IReadOnlyList<string> args)
        {
            var o = new LaunchOptions();
            if (args == null) return o;
            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i];
                string next = i + 1 < args.Count ? args[i + 1] : null;
                switch (Normalize(a))
                {
                    case "autoplay":
                        if (next != null && !next.StartsWith("-", StringComparison.Ordinal))
                        {
                            AddSeeds(o.AutoplaySeeds, next);
                            i++;
                        }
                        else
                        {
                            o.AutoplaySeeds.AddRange(ApprovedSeeds);
                        }
                        break;
                    case "autoplayspeed":
                        if (next != null && double.TryParse(next, NumberStyles.Float, CultureInfo.InvariantCulture, out double s) && s > 0 && s <= 100)
                            o.AutoplaySpeed = s;
                        i++;
                        break;
                    case "quitafterautoplay":
                        o.QuitAfterAutoplay = true;
                        break;
                    case "forge_scenario":
                    case "forgescenario":
                        o.ForgeScenario = next;
                        i++;
                        break;
                    case "replay":
                        o.ReplayPath = next;
                        i++;
                        break;
                }
            }
            if (o.ForgeScenario == PilotSmokeScenario && o.AutoplaySeeds.Count == 0)
            {
                o.AutoplaySeeds.AddRange(ApprovedSeeds);
                // Three seeds take about 240 s of match-clock time; at 8x this fits the device service's
                // 45 s wait. Every frame is still rendered and measured; only the match clock runs faster.
                if (o.AutoplaySpeed == 1.0) o.AutoplaySpeed = PilotSmokeSpeed;
            }
            return o;
        }

        private static string Normalize(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return string.Empty;
            return arg.TrimStart('-').ToLowerInvariant();
        }

        private static void AddSeeds(List<ulong> seeds, string text)
        {
            foreach (string part in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                if (ulong.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong v)) seeds.Add(v);
        }
    }
}
