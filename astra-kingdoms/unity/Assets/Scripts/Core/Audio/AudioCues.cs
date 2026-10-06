using System;
using System.Collections.Generic;

namespace AstraKingdoms.Client.Audio
{
    /// <summary>The sound cues the game can request (plan: "Art and sound direction").</summary>
    public enum AudioCue : byte
    {
        UiClick = 0,
        CardSelect = 1,
        Lock = 2,
        Draw = 3,
        Release = 4,
        Clash = 5,
        Hit = 6,
        Miss = 7,
        Dodge = 8,
        ShieldBlock = 9,
        LandTransfer = 10,
        Victory = 11,
        Defeat = 12,
        TimerWarning = 13,
        MusicMenu = 14,
        MusicMatch = 15,
    }

    /// <summary>Mixer channel. Music and effects have independent player volumes; UI follows effects.</summary>
    public enum AudioChannel : byte
    {
        Effects = 0,
        Ui = 1,
        Music = 2,
    }

    /// <summary>Registration of one cue: channel, ledger asset (or silent placeholder) and its visual twin.</summary>
    public sealed class AudioCueSpec
    {
        public AudioCue Cue { get; }
        public AudioChannel Channel { get; }
        /// <summary>Asset-ledger ID of the licensed clip; null while the cue is a silent placeholder.</summary>
        public string LedgerId { get; }
        /// <summary>Ledger ID of the generated pilot tone used until a licensed clip exists (null = silent).</summary>
        public string PlaceholderLedgerId { get; }
        /// <summary>
        /// What the player SEES at the same moment, so nothing important depends on sound
        /// (plan: "Important timing and combat information must remain understandable with sound disabled").
        /// </summary>
        public string VisualCounterpart { get; }
        public bool Loops => Channel == AudioChannel.Music;

        public AudioCueSpec(AudioCue cue, AudioChannel channel, string ledgerId, string placeholderLedgerId, string visualCounterpart)
        {
            Cue = cue;
            Channel = channel;
            LedgerId = ledgerId;
            PlaceholderLedgerId = placeholderLedgerId;
            VisualCounterpart = visualCounterpart;
        }

        public bool IsSilentPlaceholder => LedgerId == null && PlaceholderLedgerId == null;
    }

    /// <summary>
    /// The audio cue registry. No licensed audio exists yet, so every cue resolves to a generated pilot
    /// tone (recorded in the asset ledger as "generated in code") or to silence. When the licensed
    /// library arrives, set <see cref="AudioCueSpec.LedgerId"/> to its ledger entry; the runtime then
    /// loads that clip instead. The registry is data only; <c>AudioService</c> plays it.
    /// </summary>
    public static class AudioCueRegistry
    {
        private static readonly AudioCueSpec[] Specs =
        {
            new AudioCueSpec(AudioCue.UiClick, AudioChannel.Ui, null, "audio.placeholder.click", "Button highlight and pressed state."),
            new AudioCueSpec(AudioCue.CardSelect, AudioChannel.Ui, null, "audio.placeholder.click", "Selected card gets a check mark and outline."),
            new AudioCueSpec(AudioCue.Lock, AudioChannel.Ui, null, "audio.placeholder.lock", "Lock button changes to 'Locked' and the HUD shows 'Ready'."),
            new AudioCueSpec(AudioCue.Draw, AudioChannel.Effects, null, null, "Archer draw pose; bowstring pulls back."),
            new AudioCueSpec(AudioCue.Release, AudioChannel.Effects, null, "audio.placeholder.release", "Bowstring snaps forward; the arrow appears at the bow."),
            new AudioCueSpec(AudioCue.Clash, AudioChannel.Effects, null, "audio.placeholder.clash", "Starburst clash marker; cancelled arrows show a broken-arrow icon."),
            new AudioCueSpec(AudioCue.Hit, AudioChannel.Effects, null, "audio.placeholder.hit", "Element impact shape, damage number and the HP bar drops."),
            new AudioCueSpec(AudioCue.Miss, AudioChannel.Effects, null, "audio.placeholder.miss", "Dust ring where the arrow lands and a 'Miss' tag."),
            new AudioCueSpec(AudioCue.Dodge, AudioChannel.Effects, null, null, "Dodge pose with a direction arrow under the archer."),
            new AudioCueSpec(AudioCue.ShieldBlock, AudioChannel.Effects, null, "audio.placeholder.clash", "Shield ring flashes and a 'Blocked' tag."),
            new AudioCueSpec(AudioCue.LandTransfer, AudioChannel.Effects, null, "audio.placeholder.land", "Transferred cells sweep to the new owner's pattern; totals count."),
            new AudioCueSpec(AudioCue.Victory, AudioChannel.Effects, null, "audio.placeholder.victory", "Result screen title and the winner's crown icon."),
            new AudioCueSpec(AudioCue.Defeat, AudioChannel.Effects, null, null, "Result screen title and reason text."),
            new AudioCueSpec(AudioCue.TimerWarning, AudioChannel.Ui, null, null, "Timer text gains a '!' badge and pulses (reduced motion: no pulse)."),
            new AudioCueSpec(AudioCue.MusicMenu, AudioChannel.Music, null, "audio.placeholder.drone", "None needed (atmosphere only)."),
            new AudioCueSpec(AudioCue.MusicMatch, AudioChannel.Music, null, null, "None needed (atmosphere only)."),
        };

        public static IReadOnlyList<AudioCueSpec> All => Specs;

        public static AudioCueSpec Get(AudioCue cue)
        {
            foreach (AudioCueSpec s in Specs)
                if (s.Cue == cue) return s;
            throw new ArgumentOutOfRangeException(nameof(cue));
        }

        /// <summary>The ledger entry to load for a cue: licensed clip, else placeholder tone, else null (silent).</summary>
        public static string ResolveLedgerId(AudioCue cue)
        {
            AudioCueSpec s = Get(cue);
            return s.LedgerId ?? s.PlaceholderLedgerId;
        }
    }

    /// <summary>Independent music and effects volumes (UI follows the effects volume).</summary>
    public static class AudioMix
    {
        public static float Volume(AudioChannel channel, float musicVolume, float effectsVolume)
        {
            float v = channel == AudioChannel.Music ? musicVolume : effectsVolume;
            if (float.IsNaN(v) || v < 0f) return 0f;
            return v > 1f ? 1f : v;
        }
    }
}
