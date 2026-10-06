using UnityEngine;

namespace AstraKingdoms.Client.Services
{
    public enum Sfx
    {
        Click = 0,
        Lock = 1,
        Release = 2,
        Hit = 3,
        Miss = 4,
        Clash = 5,
        Land = 6,
        Victory = 7,
    }

    /// <summary>
    /// Independent music and effects channels. Pilot clips are generated tones (no third-party audio,
    /// nothing to license); the licensed library replaces them in V1 (asset ledger required). Every
    /// sound duplicates information that is also shown on screen, so play works muted.
    /// </summary>
    public sealed class AudioService : MonoBehaviour
    {
        private AudioSource _music;
        private AudioSource _effects;
        private AudioClip[] _clips;
        private float _effectsVolume = 1f;

        public void Init(float musicVolume, float effectsVolume)
        {
            _music = gameObject.AddComponent<AudioSource>();
            _music.loop = true;
            _music.playOnAwake = false;
            _effects = gameObject.AddComponent<AudioSource>();
            _effects.playOnAwake = false;
            _clips = new[]
            {
                Tone("click", 880f, 0.05f, 0f),
                Tone("lock", 520f, 0.12f, 260f),
                Tone("release", 300f, 0.10f, 600f),
                Tone("hit", 160f, 0.18f, -60f),
                Tone("miss", 420f, 0.12f, -200f),
                Tone("clash", 1200f, 0.08f, -400f),
                Tone("land", 220f, 0.30f, 220f),
                Tone("victory", 660f, 0.45f, 330f),
            };
            _music.clip = Tone("drone", 110f, 4f, 0f, 0.08f);
            SetVolumes(musicVolume, effectsVolume);
        }

        public void SetVolumes(float music, float effects)
        {
            _effectsVolume = Mathf.Clamp01(effects);
            if (_music == null) return;
            _music.volume = Mathf.Clamp01(music) * 0.5f;
            if (_music.volume > 0f && !_music.isPlaying) _music.Play();
            if (_music.volume <= 0f && _music.isPlaying) _music.Stop();
        }

        public void Play(Sfx sfx)
        {
            if (_effects == null || _effectsVolume <= 0f) return;
            _effects.PlayOneShot(_clips[(int)sfx], _effectsVolume);
        }

        private static AudioClip Tone(string name, float hz, float seconds, float sweepHz, float gain = 0.35f)
        {
            const int rate = 22050;
            int n = Mathf.Max(1, Mathf.RoundToInt(seconds * rate));
            var data = new float[n];
            float phase = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / n;
                float f = hz + sweepHz * t;
                phase += 2f * Mathf.PI * f / rate;
                float envelope = Mathf.Min(1f, t * 20f) * (1f - t);
                data[i] = Mathf.Sin(phase) * envelope * gain;
            }
            AudioClip clip = AudioClip.Create(name, n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
