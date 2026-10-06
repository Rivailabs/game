using System;
using System.Collections.Generic;
using AstraKingdoms.Client.Settings;

namespace AstraKingdoms.Client.Flow
{
    /// <summary>A category of data this device keeps, with its delete action.</summary>
    public sealed class LocalDataCategory
    {
        public string Id { get; }
        public string LabelKey { get; }
        /// <summary>Deletes the category; returns false when something could not be removed.</summary>
        public Func<bool> Delete { get; }

        public LocalDataCategory(string id, string labelKey, Func<bool> delete)
        {
            Id = id;
            LabelKey = labelKey;
            Delete = delete ?? throw new ArgumentNullException(nameof(delete));
        }
    }

    /// <summary>
    /// Local data controls (ticket 46): lists what this phone stores (settings, tutorial progress,
    /// practice statistics, development match records) and deletes it on request. Account data
    /// held by online services is deleted through the online account flow, which this screen links
    /// to when that service exists; nothing here pretends to delete server data.
    /// </summary>
    public sealed class LocalDataControls
    {
        private readonly List<LocalDataCategory> _categories = new List<LocalDataCategory>();

        public IReadOnlyList<LocalDataCategory> Categories => _categories;

        public void Register(LocalDataCategory category) => _categories.Add(category ?? throw new ArgumentNullException(nameof(category)));

        /// <summary>The settings and tutorial-progress category over a key-value store.</summary>
        public static LocalDataCategory SettingsCategory(IKeyValueStore store, IKeyValueEraser eraser, SettingsModel liveSettings) =>
            new LocalDataCategory("settings", "data.settings", () =>
            {
                foreach (string key in SettingsModel.Keys) eraser.DeleteKey(key);
                store.Save();
                // Reset the live model to defaults so the UI matches what is stored.
                var defaults = new SettingsModel();
                liveSettings.MusicVolume = defaults.MusicVolume;
                liveSettings.EffectsVolume = defaults.EffectsVolume;
                liveSettings.ReducedCameraShake = defaults.ReducedCameraShake;
                liveSettings.Haptics = defaults.Haptics;
                liveSettings.TextScale = defaults.TextScale;
                liveSettings.Language = defaults.Language;
                liveSettings.FirstRunComplete = false;
                liveSettings.ReducedMotion = defaults.ReducedMotion;
                liveSettings.ShowPatterns = defaults.ShowPatterns;
                liveSettings.TutorialUntimed = defaults.TutorialUntimed;
                liveSettings.TutorialOffered = false;
                liveSettings.TutorialCompleted = false;
                liveSettings.NotifyChanged();
                return true;
            });

        /// <summary>Deletes every category; returns the IDs that failed (empty on success).</summary>
        public List<string> DeleteAll()
        {
            var failed = new List<string>();
            foreach (LocalDataCategory c in _categories)
            {
                bool ok;
                try
                {
                    ok = c.Delete();
                }
                catch (Exception)
                {
                    ok = false;
                }
                if (!ok) failed.Add(c.Id);
            }
            return failed;
        }
    }
}
