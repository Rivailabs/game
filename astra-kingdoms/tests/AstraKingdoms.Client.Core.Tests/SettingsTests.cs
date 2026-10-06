using AstraKingdoms.Client.Settings;

namespace AstraKingdoms.Client.Tests;

public sealed class SettingsTests
{
    private sealed class MemoryStore : IKeyValueStore
    {
        public readonly Dictionary<string, object> Values = new();
        public int Saves;
        public bool HasKey(string key) => Values.ContainsKey(key);
        public float GetFloat(string key, float fallback) => Values.TryGetValue(key, out object v) ? (float)v : fallback;
        public int GetInt(string key, int fallback) => Values.TryGetValue(key, out object v) ? (int)v : fallback;
        public string GetString(string key, string fallback) => Values.TryGetValue(key, out object v) ? (string)v : fallback;
        public void SetFloat(string key, float value) => Values[key] = value;
        public void SetInt(string key, int value) => Values[key] = value;
        public void SetString(string key, string value) => Values[key] = value;
        public void Save() => Saves++;
    }

    [Test]
    public void SettingsPersistAndReload()
    {
        var store = new MemoryStore();
        var s = new SettingsModel { MusicVolume = 0.2f, EffectsVolume = 0.4f, ReducedCameraShake = true, Haptics = false, TextScale = 1.3f, Language = "kn", FirstRunComplete = true };
        s.Save(store);
        var t = new SettingsModel();
        t.Load(store);
        Assert.That(t.MusicVolume, Is.EqualTo(0.2f));
        Assert.That(t.EffectsVolume, Is.EqualTo(0.4f));
        Assert.That(t.ReducedCameraShake, Is.True);
        Assert.That(t.Haptics, Is.False);
        Assert.That(t.TextScale, Is.EqualTo(1.3f));
        Assert.That(t.Language, Is.EqualTo("kn"));
        Assert.That(t.FirstRunComplete, Is.True);
        Assert.That(store.Saves, Is.EqualTo(1));
    }

    [Test]
    public void InvalidValuesAreSanitized()
    {
        var s = new SettingsModel { MusicVolume = 7f, EffectsVolume = -1f, TextScale = 9f, Language = "fr" };
        s.Sanitize();
        Assert.That(s.MusicVolume, Is.EqualTo(1f));
        Assert.That(s.EffectsVolume, Is.EqualTo(0f));
        Assert.That(s.TextScale, Is.EqualTo(1.3f));
        Assert.That(s.Language, Is.EqualTo("en"));
    }

    [Test]
    public void TextScaleCycles()
    {
        var s = new SettingsModel { TextScale = 1.3f };
        Assert.That(s.NextTextScale(), Is.EqualTo(0.9f));
    }
}
