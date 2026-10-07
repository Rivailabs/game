using System;
using AstraKingdoms.Meta.Analytics;
using UnityEngine;

namespace AstraKingdoms.Client.Meta
{
    /// <summary>
    /// Starts a new analytics session when the app returns to the foreground after a long break
    /// (a "return" is a real foreground session) and flushes queued events when it goes to the background.
    /// </summary>
    public sealed class MetaLifecycle : MonoBehaviour
    {
        public static readonly TimeSpan NewSessionAfter = TimeSpan.FromMinutes(30);
        private DateTime _pausedAt;

        private void OnApplicationPause(bool paused)
        {
            MetaServices s = MetaModule.Services;
            if (s == null) return;
            if (paused)
            {
                _pausedAt = DateTime.UtcNow;
                s.Analytics.Flush();
                return;
            }
            if (_pausedAt != default && DateTime.UtcNow - _pausedAt >= NewSessionAfter)
            {
                s.Analytics.StartSession();
                s.Analytics.Track(AnalyticsEventType.ValidSession, AnalyticsEvents.ValidSession(Application.version, GameBootstrap.DevelopmentTools));
            }
        }
    }
}
