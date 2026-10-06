using System.Collections.Generic;
using AstraKingdoms.Client.Settings;

namespace AstraKingdoms.Client.Flow
{
    /// <summary>Ways to play offered on the home screen.</summary>
    public enum PlayModeId : byte
    {
        /// <summary>Online match (queue; an explicitly labelled bot is offered only after waiting).</summary>
        PlayOnline = 0,
        /// <summary>Practice against a labelled bot on this phone.</summary>
        Practice = 1,
        /// <summary>Private friend room (online).</summary>
        FriendRoom = 2,
        /// <summary>Two people on one phone (fully offline).</summary>
        SharedPhone = 3,
        /// <summary>Guided starter duel.</summary>
        Tutorial = 4,
    }

    /// <summary>Network reachability as the client sees it.</summary>
    public enum Connectivity : byte
    {
        Unknown = 0,
        Offline = 1,
        Online = 2,
    }

    /// <summary>One home-screen entry and whether it can start now.</summary>
    public sealed class ModeEntry
    {
        public PlayModeId Mode { get; internal set; }
        public string TitleKey { get; internal set; }
        public string HintKey { get; internal set; }
        public bool Available { get; internal set; }
        /// <summary>Why it is unavailable (null when available); shown instead of hiding the entry.</summary>
        public string UnavailableKey { get; internal set; }
        public bool RequiresNetwork { get; internal set; }
        /// <summary>Primary entries are the plan's home priorities: Play, Practice, Play with a Friend.</summary>
        public bool Primary { get; internal set; }
    }

    /// <summary>
    /// Bootstrap and mode selection (ticket 43). Shared-phone play, practice and the tutorial work
    /// fully offline (their documented offline conditions: no account, no network, local records
    /// only, no rewards that become server currency). Online modes are listed but disabled with a
    /// specific reason when the network or the online service is unavailable.
    /// </summary>
    public static class ModeCatalog
    {
        public static List<ModeEntry> Build(Connectivity connectivity, bool onlineServiceConfigured)
        {
            bool online = connectivity == Connectivity.Online && onlineServiceConfigured;
            string reason = !onlineServiceConfigured ? "mode.unavailable.notInThisBuild"
                : connectivity == Connectivity.Online ? null : "mode.unavailable.offline";
            return new List<ModeEntry>
            {
                new ModeEntry { Mode = PlayModeId.PlayOnline, TitleKey = "mode.online", HintKey = "mode.online.hint", Available = online,
                    UnavailableKey = online ? null : reason, RequiresNetwork = true, Primary = true },
                new ModeEntry { Mode = PlayModeId.Practice, TitleKey = "home.practice", HintKey = "home.practiceHint", Available = true, Primary = true },
                new ModeEntry { Mode = PlayModeId.FriendRoom, TitleKey = "mode.friend", HintKey = "mode.friend.hint", Available = online,
                    UnavailableKey = online ? null : reason, RequiresNetwork = true, Primary = true },
                new ModeEntry { Mode = PlayModeId.SharedPhone, TitleKey = "home.play", HintKey = "home.playHint", Available = true },
                new ModeEntry { Mode = PlayModeId.Tutorial, TitleKey = "mode.tutorial", HintKey = "mode.tutorial.hint", Available = true },
            };
        }
    }

    /// <summary>The first screens a launch shows (plan: "Player journey").</summary>
    public enum LaunchScreen : byte
    {
        /// <summary>First launch: language and basic settings.</summary>
        FirstRunSettings = 0,
        /// <summary>Right after first-run settings: the guided starter duel is offered.</summary>
        TutorialOffer = 1,
        Home = 2,
    }

    /// <summary>Launch routing: first launch asks for language and settings, then immediately offers the tutorial.</summary>
    public static class LaunchFlow
    {
        public static LaunchScreen First(SettingsModel settings)
        {
            if (!settings.FirstRunComplete) return LaunchScreen.FirstRunSettings;
            if (!settings.TutorialOffered) return LaunchScreen.TutorialOffer;
            return LaunchScreen.Home;
        }
    }
}
