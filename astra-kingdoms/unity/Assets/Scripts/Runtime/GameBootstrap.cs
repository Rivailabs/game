using AstraKingdoms.Client.Arena;
using AstraKingdoms.Client.MatchFlow;
using AstraKingdoms.Client.Services;
using AstraKingdoms.Client.Settings;
using UnityEngine;

namespace AstraKingdoms.Client
{
    /// <summary>
    /// Entry point placed in the script-built Duel scene. Creates services (settings, localization,
    /// audio), binds the grey-box arena, the match controller and the UI flow, then shows the first
    /// screen - or, in development builds, starts the automation/replay interface when launch
    /// options ask for it.
    /// </summary>
    public sealed class GameBootstrap : MonoBehaviour
    {
        public const int TargetFrameRate = 30;

        public ClientContext Context { get; private set; }
        public GameFlow Flow { get; private set; }
        public ArenaView Arena { get; private set; }
        public MatchController Controller { get; private set; }

        /// <summary>True in development builds and the editor: automation, replay viewer and records are available.</summary>
        public static bool DevelopmentTools
        {
            get
            {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                return true;
#else
                return false;
#endif
            }
        }

        private void Awake()
        {
            Application.targetFrameRate = TargetFrameRate;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;

            var store = new PlayerPrefsStore();
            var settings = new SettingsModel();
            settings.Load(store);
            Context = new ClientContext
            {
                Settings = settings,
                Store = store,
                Loc = LocalizationLoader.Load(settings.Language),
            };
            var audioGo = new GameObject("Audio");
            audioGo.transform.SetParent(transform, false);
            Context.Audio = audioGo.AddComponent<AudioService>();
            Context.Audio.Init(settings.MusicVolume, settings.EffectsVolume);
            Haptics.Enabled = settings.Haptics;

            var arenaGo = new GameObject("ArenaView");
            arenaGo.transform.SetParent(transform, false);
            Arena = arenaGo.AddComponent<ArenaView>();
            Arena.Init(settings.ReducedCameraShake);
            Arena.Ctx = Context;

            Controller = gameObject.AddComponent<MatchController>();
            Flow = gameObject.AddComponent<GameFlow>();
            Flow.Init(Context, Arena, Controller, DevelopmentTools);
        }

        private void Start()
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            Automation.LaunchOptions options = Automation.LaunchOptions.Parse(Automation.LaunchArgs.Collect());
            if (options.AutoplayRequested)
            {
                gameObject.AddComponent<Automation.AutomationRunner>().Run(options, Flow);
                return;
            }
            if (!string.IsNullOrEmpty(options.ReplayPath))
            {
                Flow.OpenReplay(options.ReplayPath);
                return;
            }
#endif
            Flow.ShowStart();
        }
    }
}
