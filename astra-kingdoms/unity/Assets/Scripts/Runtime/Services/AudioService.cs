using System.Collections.Generic;
using AstraKingdoms.Client.Audio;
using UnityEngine;

namespace AstraKingdoms.Client.Services
{
    /// <summary>Pilot sound names (kept for existing callers; mapped onto <see cref="AudioCue"/>).</summary>
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
    /// Independent music and effects channels driven by the <see cref="AudioCueRegistry"/>. No licensed
    /// audio exists yet: cues resolve to generated pilot tones (asset ledger "generated in code") or
    /// stay silent. When a licensed clip is registered under a ledger ID, load it into
    /// <see cref="RegisterClip"/> and the cue plays it instead. Every sound duplicates information
    /// that is also shown on screen, so play works muted.
    /// </summary>
    public sealed class AudioService : MonoBehaviour
    {
        private AudioSource _music;
        private AudioSource _effects;
        private readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();
        private float _musicVolume = 1f;
        private float _effectsVolume = 1f;

        public void Init(float musicVolume, float effectsVolume)
        {
            _music = gameObject.AddComponent<AudioSource>();
            _music.loop = true;
            _music.playOnAwake = false;
            _effects = gameObject.AddComponent<AudioSource>();
            _effects.playOnAwake = false;
            RegisterClip("audio.placeholder.click", Tone("click", 880f, 0.05f, 0f));
            RegisterClip("audio.placeholder.lock", Tone("lock", 520f, 0.12f, 260f));
            RegisterClip("audio.placeholder.release", Tone("release", 300f, 0.10f, 600f));
            RegisterClip("audio.placeholder.hit", Tone("hit", 160f, 0.18f, -60f));
            RegisterClip("audio.placeholder.miss", Tone("miss", 420f, 0.12f, -200f));
            RegisterClip("audio.placeholder.clash", Tone("clash", 1200f, 0.08f, -400f));
            RegisterClip("audio.placeholder.land", Tone("land", 220f, 0.30f, 220f));
            RegisterClip("audio.placeholder.victory", Tone("victory", 660f, 0.45f, 330f));
            RegisterClip("audio.placeholder.drone", Tone("drone", 110f, 4f, 0f, 0.08f));
            _music.clip = Clip(AudioCueRegistry.ResolveLedgerId(AudioCue.MusicMenu));
            SetVolumes(musicVolume, effectsVolume);
        }

        /// <summary>Makes a clip available under its asset-ledger ID.</summary>
        public void RegisterClip(string ledgerId, AudioClip clip)
        {
            if (ledgerId != null && clip != null) _clips[ledgerId] = clip;
        }

        public void SetVolumes(float music, float effects)
        {
            _musicVolume = AudioMix.Volume(AudioChannel.Music, music, effects);
            _effectsVolume = AudioMix.Volume(AudioChannel.Effects, music, effects);
            if (_music == null) return;
            _music.volume = _musicVolume * 0.5f;
            if (_music.volume > 0f && !_music.isPlaying && _music.clip != null) _music.Play();
            if (_music.volume <= 0f && _music.isPlaying) _music.Stop();
        }

        public void Play(AudioCue cue)
        {
            if (_effects == null) return;
            AudioCueSpec spec = AudioCueRegistry.Get(cue);
            if (spec.Channel == AudioChannel.Music) return; // music is started by SetVolumes / scene flow
            float volume = spec.Channel == AudioChannel.Music ? _musicVolume : _effectsVolume;
            AudioClip clip = Clip(AudioCueRegistry.ResolveLedgerId(cue));
            if (clip == null || volume <= 0f) return; // silent placeholder or muted
            _effects.PlayOneShot(clip, volume);
        }

        public void Play(Sfx sfx)
        {
            switch (sfx)
            {
                case Sfx.Click: Play(AudioCue.UiClick); break;
                case Sfx.Lock: Play(AudioCue.Lock); break;
                case Sfx.Release: Play(AudioCue.Release); break;
                case Sfx.Hit: Play(AudioCue.Hit); break;
                case Sfx.Miss: Play(AudioCue.Miss); break;
                case Sfx.Clash: Play(AudioCue.Clash); break;
                case Sfx.Land: Play(AudioCue.LandTransfer); break;
                default: Play(AudioCue.Victory); break;
            }
        }

        private AudioClip Clip(string ledgerId) => ledgerId != null && _clips.TryGetValue(ledgerId, out AudioClip c) ? c : null;

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
