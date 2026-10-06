using System;
using AstraKingdoms.Client.Localization;

namespace AstraKingdoms.Client.Settings
{
    /// <summary>Persistence seam: PlayerPrefs in Unity, a dictionary in tests.</summary>
    public interface IKeyValueStore
    {
        bool HasKey(string key);
        float GetFloat(string key, float fallback);
        int GetInt(string key, int fallback);
        string GetString(string key, string fallback);
        void SetFloat(string key, float value);
        void SetInt(string key, int value);
        void SetString(string key, string value);
        void Save();
    }

    /// <summary>
    /// Optional deletion seam for a key-value store (kept separate from <see cref="IKeyValueStore"/>
    /// so existing store implementations stay source compatible).
    /// </summary>
    public interface IKeyValueEraser
    {
        void DeleteKey(string key);
    }

    /// <summary>
    /// Player settings required by the plan's accessibility list: independent music and effects
    /// volume, reduced camera shake, haptics, readable text scaling and language, all persistent.
    /// </summary>
    public sealed class SettingsModel
    {
        public const string Prefix = "ak.settings.";
        public static readonly float[] TextScales = { 0.9f, 1.0f, 1.15f, 1.3f };

        public float MusicVolume = 0.6f;
        public float EffectsVolume = 0.9f;
        public bool ReducedCameraShake;
        public bool Haptics = true;
        public float TextScale = 1.0f;
        public string Language = Localizer.English;
        /// <summary>First launch asks for language and basic settings before anything else.</summary>
        public bool FirstRunComplete;
        /// <summary>Reduced motion: no camera shake, no pulsing timers or sweeping transfers (instant state changes instead).</summary>
        public bool ReducedMotion;
        /// <summary>Colour-independent cues: owner patterns on the land map and shape glyphs on effects (on by default).</summary>
        public bool ShowPatterns = true;
        /// <summary>The tutorial runs without deadlines (on by default; it is a practice match).</summary>
        public bool TutorialUntimed = true;
        /// <summary>The guided starter duel was offered after the first-run settings.</summary>
        public bool TutorialOffered;
        /// <summary>The guided starter duel was completed at least once.</summary>
        public bool TutorialCompleted;

        /// <summary>Camera shake is off when either reduced shake or reduced motion is chosen.</summary>
        public bool ShakeDisabled => ReducedCameraShake || ReducedMotion;

        /// <summary>Every persisted key (for the local data controls' delete action).</summary>
        public static readonly string[] Keys =
        {
            Prefix + "music", Prefix + "effects", Prefix + "reducedShake", Prefix + "haptics", Prefix + "textScale", Prefix + "language",
            Prefix + "firstRunComplete", Prefix + "reducedMotion", Prefix + "showPatterns", Prefix + "tutorialUntimed",
            Prefix + "tutorialOffered", Prefix + "tutorialCompleted",
        };

        public event Action Changed;

        public void Load(IKeyValueStore store)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            MusicVolume = store.GetFloat(Prefix + "music", MusicVolume);
            EffectsVolume = store.GetFloat(Prefix + "effects", EffectsVolume);
            ReducedCameraShake = store.GetInt(Prefix + "reducedShake", ReducedCameraShake ? 1 : 0) != 0;
            Haptics = store.GetInt(Prefix + "haptics", Haptics ? 1 : 0) != 0;
            TextScale = store.GetFloat(Prefix + "textScale", TextScale);
            Language = store.GetString(Prefix + "language", Language);
            FirstRunComplete = store.GetInt(Prefix + "firstRunComplete", 0) != 0;
            ReducedMotion = store.GetInt(Prefix + "reducedMotion", ReducedMotion ? 1 : 0) != 0;
            ShowPatterns = store.GetInt(Prefix + "showPatterns", ShowPatterns ? 1 : 0) != 0;
            TutorialUntimed = store.GetInt(Prefix + "tutorialUntimed", TutorialUntimed ? 1 : 0) != 0;
            TutorialOffered = store.GetInt(Prefix + "tutorialOffered", 0) != 0;
            TutorialCompleted = store.GetInt(Prefix + "tutorialCompleted", 0) != 0;
            Sanitize();
        }

        public void Save(IKeyValueStore store)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            Sanitize();
            store.SetFloat(Prefix + "music", MusicVolume);
            store.SetFloat(Prefix + "effects", EffectsVolume);
            store.SetInt(Prefix + "reducedShake", ReducedCameraShake ? 1 : 0);
            store.SetInt(Prefix + "haptics", Haptics ? 1 : 0);
            store.SetFloat(Prefix + "textScale", TextScale);
            store.SetString(Prefix + "language", Language);
            store.SetInt(Prefix + "firstRunComplete", FirstRunComplete ? 1 : 0);
            store.SetInt(Prefix + "reducedMotion", ReducedMotion ? 1 : 0);
            store.SetInt(Prefix + "showPatterns", ShowPatterns ? 1 : 0);
            store.SetInt(Prefix + "tutorialUntimed", TutorialUntimed ? 1 : 0);
            store.SetInt(Prefix + "tutorialOffered", TutorialOffered ? 1 : 0);
            store.SetInt(Prefix + "tutorialCompleted", TutorialCompleted ? 1 : 0);
            store.Save();
        }

        /// <summary>Notifies listeners after a change (volume, scale, language...).</summary>
        public void NotifyChanged()
        {
            Sanitize();
            Changed?.Invoke();
        }

        public void Sanitize()
        {
            MusicVolume = Clamp01(MusicVolume);
            EffectsVolume = Clamp01(EffectsVolume);
            TextScale = NearestScale(TextScale);
            bool known = false;
            foreach (string code in Localizer.SupportedLanguages) known |= code == Language;
            if (!known) Language = Localizer.English;
        }

        /// <summary>The next text scale step (wraps), for a single cycling button.</summary>
        public float NextTextScale()
        {
            int i = Array.IndexOf(TextScales, NearestScale(TextScale));
            return TextScales[(i + 1) % TextScales.Length];
        }

        private static float NearestScale(float s)
        {
            float best = TextScales[1];
            float bestDiff = float.MaxValue;
            foreach (float t in TextScales)
            {
                float d = Math.Abs(t - s);
                if (d < bestDiff)
                {
                    bestDiff = d;
                    best = t;
                }
            }
            return best;
        }

        private static float Clamp01(float v) => float.IsNaN(v) ? 0f : v < 0f ? 0f : v > 1f ? 1f : v;
    }
}
