using System;
using System.Threading;
using System.Threading.Tasks;
using AstraKingdoms.Client.Services;
using AstraKingdoms.Client.UI.Screens;
using UnityEngine;

namespace AstraKingdoms.Client.Online.UI
{
    /// <summary>
    /// Plugs online play into the existing client without touching its flow: at load it registers
    /// <see cref="HomeScreen.OnlineHook"/>, so Home shows "Play online", which opens
    /// <see cref="OnlineFlow"/>. Removing this folder removes the button.
    /// </summary>
    public static class OnlineEntryPoint
    {
        /// <summary>Key (in the client's settings store) of a server address override for development.</summary>
        public const string ServerKey = "online.serverUri";
        /// <summary>Placeholder until the service is deployed (see server/RUNBOOK.md).</summary>
        public const string DefaultServer = "wss://play.astrakingdoms.invalid";

        /// <summary>
        /// Production identity: returns a fresh Firebase ID token. The Firebase Auth SDK adapter that
        /// sets this is not part of the repository yet (Firebase is not available here). When it is
        /// null, development builds fall back to a "dev:" token and release builds cannot sign in.
        /// </summary>
        public static Func<CancellationToken, Task<string>> IdentityTokenProvider;

        private static OnlineFlow _flow;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register() => HomeScreen.OnlineHook = Open;

        public static void Open(HomeScreen home, ClientContext ctx)
        {
            if (_flow == null)
            {
                var go = new GameObject("OnlineFlow");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _flow = go.AddComponent<OnlineFlow>();
            }
            _flow.Open(home, ctx);
        }

        /// <summary>The token source for this build, or null when online sign-in is unavailable.</summary>
        public static Func<CancellationToken, Task<string>> TokenSource(ClientContext ctx)
        {
            if (IdentityTokenProvider != null) return IdentityTokenProvider;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            string id = ctx.Store.GetString("online.devId", null);
            if (string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString("N").Substring(0, 16);
                ctx.Store.SetString("online.devId", id);
            }
            string token = "dev:" + id;
            return _ => Task.FromResult(token);
#else
            return null;
#endif
        }

        public static Uri ServerUri(ClientContext ctx) => new Uri(ctx.Store.GetString(ServerKey, DefaultServer));
    }
}
