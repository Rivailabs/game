using UnityEngine;

namespace AstraKingdoms.Client.Services
{
    /// <summary>Optional vibration, guarded by platform and the player's haptics setting.</summary>
    public static class Haptics
    {
        public static bool Enabled = true;

        public static void Pulse()
        {
            if (!Enabled) return;
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            Handheld.Vibrate();
#endif
        }
    }
}
