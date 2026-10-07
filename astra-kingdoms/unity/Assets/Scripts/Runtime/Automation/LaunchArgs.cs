#if DEVELOPMENT_BUILD || UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace AstraKingdoms.Client.Automation
{
    /// <summary>
    /// Collects development launch arguments: the process command line and, on Android, intent
    /// extras set by the device service (<c>am start -n pkg/activity -e forge_scenario pilot-smoke</c>,
    /// <c>-e autoplay 1,2,3</c>, <c>-e autoplaySpeed 4</c>, <c>-e replay /path</c>, or Unity's own
    /// <c>-e unity "-autoplay 7"</c>). Compiled only into development builds and the editor.
    /// </summary>
    public static class LaunchArgs
    {
        private static readonly string[] Extras = { "forge_scenario", "autoplay", "autoplaySpeed", "replay", "quitAfterAutoplay" };

        public static List<string> Collect()
        {
            var args = new List<string>();
            try
            {
                args.AddRange(Environment.GetCommandLineArgs());
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Automation] Command line unavailable: " + ex.Message);
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var intent = activity.Call<AndroidJavaObject>("getIntent"))
                {
                    foreach (string key in Extras)
                    {
                        string value = intent.Call<string>("getStringExtra", key);
                        if (value == null) continue;
                        args.Add("-" + key);
                        if (value.Length > 0) args.Add(value);
                    }
                    string unityArgs = intent.Call<string>("getStringExtra", "unity");
                    if (!string.IsNullOrEmpty(unityArgs))
                        args.AddRange(unityArgs.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Automation] Could not read intent extras: " + ex.Message);
            }
#endif
            return args;
        }
    }
}
#endif
