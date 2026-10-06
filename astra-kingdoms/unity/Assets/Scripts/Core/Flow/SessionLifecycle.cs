namespace AstraKingdoms.Client.Flow
{
    /// <summary>What kind of match (if any) is in progress.</summary>
    public enum SessionMatchKind : byte
    {
        None = 0,
        SharedPhone = 1,
        Practice = 2,
        Tutorial = 3,
        Online = 4,
    }

    /// <summary>Online connection as reported by the online layer.</summary>
    public enum ConnectionState : byte
    {
        Connected = 0,
        Reconnecting = 1,
        Lost = 2,
    }

    /// <summary>The overlay the pause/background/connection UI shows (ticket 47).</summary>
    public enum SessionOverlay : byte
    {
        None = 0,
        /// <summary>Local match: an opaque cover after returning from the background; tap to continue.</summary>
        ResumeCover = 1,
        /// <summary>Practice/tutorial pause menu (resume, settings, leave).</summary>
        PauseMenu = 2,
        /// <summary>Online: reconnecting; the server keeps the authoritative clock.</summary>
        Reconnecting = 3,
        /// <summary>Online: connection lost; choices are keep waiting or leave.</summary>
        ConnectionLost = 4,
        /// <summary>Online: back, showing the authoritative phase after recovery.</summary>
        Recovered = 5,
    }

    /// <summary>
    /// Pause, background and connection-state model (ticket 47), driven by Unity's
    /// <c>OnApplicationPause</c>/<c>OnApplicationFocus</c> and the online layer's connection events.
    /// <list type="bullet">
    /// <item><b>Local matches</b> (shared phone, practice, tutorial): the host clock stops while the
    /// app is in the background (a phone call is not play time), the frame that resumes is dropped,
    /// and an opaque resume cover hides any private entry screen until someone taps. This is an
    /// interruption, not the player-chosen pause, which remains practice-only.</item>
    /// <item><b>Online matches</b>: the server owns the clock and keeps running; the client shows
    /// reconnecting/lost states and, once recovered, the authoritative phase reported by the server
    /// with its recovery choices. The client never resumes from its own cached phase.</item>
    /// </list>
    /// </summary>
    public sealed class SessionLifecycle
    {
        public SessionMatchKind Match { get; private set; }
        public bool Backgrounded { get; private set; }
        public bool PlayerPaused { get; private set; }
        public ConnectionState Connection { get; private set; } = ConnectionState.Connected;
        public SessionOverlay Overlay { get; private set; }
        /// <summary>Localization key of the server's authoritative phase after an online recovery.</summary>
        public string RecoveredPhaseKey { get; private set; }

        private bool _dropNextFrame;

        public bool IsLocal => Match == SessionMatchKind.SharedPhone || Match == SessionMatchKind.Practice || Match == SessionMatchKind.Tutorial;

        /// <summary>The player-chosen pause exists only in local practice (and the practice-based tutorial).</summary>
        public bool PauseAllowed => Match == SessionMatchKind.Practice || Match == SessionMatchKind.Tutorial;

        public void MatchStarted(SessionMatchKind kind)
        {
            Match = kind;
            PlayerPaused = false;
            Overlay = SessionOverlay.None;
            RecoveredPhaseKey = null;
            Connection = ConnectionState.Connected;
        }

        public void MatchEnded()
        {
            Match = SessionMatchKind.None;
            PlayerPaused = false;
            Overlay = SessionOverlay.None;
        }

        /// <summary>Unity OnApplicationPause(paused).</summary>
        public void ApplicationPaused(bool paused)
        {
            if (paused)
            {
                Backgrounded = true;
                if (IsLocal) Overlay = SessionOverlay.ResumeCover;
                return;
            }
            if (!Backgrounded) return;
            Backgrounded = false;
            _dropNextFrame = true;
            if (Match == SessionMatchKind.Online && Connection != ConnectionState.Connected) Overlay = SessionOverlay.Reconnecting;
        }

        /// <summary>The player tapped the resume cover or the pause menu's Resume.</summary>
        public void ResumeTapped()
        {
            if (Overlay == SessionOverlay.ResumeCover || Overlay == SessionOverlay.PauseMenu || Overlay == SessionOverlay.Recovered)
            {
                Overlay = SessionOverlay.None;
                PlayerPaused = false;
            }
        }

        /// <summary>The HUD pause button; returns false when pausing is not allowed in this mode.</summary>
        public bool TogglePause()
        {
            if (!PauseAllowed) return false;
            PlayerPaused = !PlayerPaused;
            Overlay = PlayerPaused ? SessionOverlay.PauseMenu : SessionOverlay.None;
            return true;
        }

        public void ConnectionChanged(ConnectionState state)
        {
            if (Match != SessionMatchKind.Online) return;
            Connection = state;
            if (state == ConnectionState.Reconnecting) Overlay = SessionOverlay.Reconnecting;
            else if (state == ConnectionState.Lost) Overlay = SessionOverlay.ConnectionLost;
        }

        /// <summary>The online layer restored a canonical snapshot; show the server's phase, not a cached one.</summary>
        public void AuthoritativePhaseRestored(string phaseKey)
        {
            if (Match != SessionMatchKind.Online) return;
            Connection = ConnectionState.Connected;
            RecoveredPhaseKey = phaseKey;
            Overlay = SessionOverlay.Recovered;
        }

        /// <summary>
        /// Seconds the local host clock should advance this frame: zero while backgrounded, covered,
        /// paused or online (the server's clock rules there), and zero for the first frame after
        /// returning, whose delta includes the time spent in the background.
        /// </summary>
        public double HostDelta(double frameSeconds)
        {
            if (_dropNextFrame)
            {
                _dropNextFrame = false;
                return 0;
            }
            if (!IsLocal || Backgrounded || PlayerPaused || Overlay == SessionOverlay.ResumeCover) return 0;
            return frameSeconds < 0 ? 0 : frameSeconds;
        }

        /// <summary>Private entry screens must be hidden under these overlays.</summary>
        public bool HidesPrivateScreens => Overlay == SessionOverlay.ResumeCover || Backgrounded;
    }
}
