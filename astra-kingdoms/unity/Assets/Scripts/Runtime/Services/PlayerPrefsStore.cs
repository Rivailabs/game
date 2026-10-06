using AstraKingdoms.Client.Settings;
using UnityEngine;

namespace AstraKingdoms.Client.Services
{
    /// <summary>Settings persistence through Unity PlayerPrefs.</summary>
    public sealed class PlayerPrefsStore : IKeyValueStore
    {
        public bool HasKey(string key) => PlayerPrefs.HasKey(key);
        public float GetFloat(string key, float fallback) => PlayerPrefs.GetFloat(key, fallback);
        public int GetInt(string key, int fallback) => PlayerPrefs.GetInt(key, fallback);
        public string GetString(string key, string fallback) => PlayerPrefs.GetString(key, fallback);
        public void SetFloat(string key, float value) => PlayerPrefs.SetFloat(key, value);
        public void SetInt(string key, int value) => PlayerPrefs.SetInt(key, value);
        public void SetString(string key, string value) => PlayerPrefs.SetString(key, value);
        public void Save() => PlayerPrefs.Save();
    }
}
