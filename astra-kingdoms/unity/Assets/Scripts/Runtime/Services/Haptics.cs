using UnityEngine;

namespace AstraKingdoms.Client.Services
{
    /// <summary>Optional vibration, guarded by platform and the player's haptics setting.</summary>
    public static class Haptics
    {
        public static bool Enabled = true;

        /// <summary>A strong pulse for a hit (Unity's only built-in vibration).</summary>
        public static void Pulse()
        {
            if (!Enabled) return;
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            Handheld.Vibrate();
#endif
        }

        /// <summary>
        /// A light tick for aim steps. Unity has no light-haptic API (Handheld.Vibrate is a long
        /// buzz), so this is a deliberate no-op seam until an Android haptic-feedback plugin
        /// (View.performHapticFeedback on the UI thread) is added and checked on the reference phone.
        /// The same step is always shown visually (degree readout and limit marker).
        /// </summary>
        public static void Tick()
        {
        }
    }
}
