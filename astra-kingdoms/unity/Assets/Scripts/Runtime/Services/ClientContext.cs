using System;
using AstraKingdoms.Client.Localization;
using AstraKingdoms.Client.Settings;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Core;

namespace AstraKingdoms.Client.Services
{
    /// <summary>Shared client services handed to screens (no static singletons for game state).</summary>
    public sealed class ClientContext
    {
        public SettingsModel Settings;
        public IKeyValueStore Store;
        public Localizer Loc;
        public AudioService Audio;

        /// <summary>
        /// Account level for Owned/Loaned labels and drills. Defaults to 1 until the progression
        /// service (ticket 57, outside this client layer) provides the saved level.
        /// </summary>
        public Func<int> AccountLevel = () => 1;

        /// <summary>Display names for the two seats of the current match.</summary>
        public Func<PlayerSide, string> PlayerName = side => side == PlayerSide.A ? "A" : "B";

        public string T(string key) => Loc.Get(key);
        public string TF(string key, params object[] args) => Loc.Format(key, args);

        public string DifficultyName(BotDifficulty d) => Loc.Get("difficulty." + d.ToString().ToLowerInvariant());

        public void SaveSettings()
        {
            Settings.Save(Store);
            Settings.NotifyChanged();
        }
    }
}
