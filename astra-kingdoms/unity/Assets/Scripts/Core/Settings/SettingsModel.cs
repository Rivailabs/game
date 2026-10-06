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
