using System;
using System.Collections.Generic;
using AstraKingdoms.Client.Localization;
using AstraKingdoms.Client.Match;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Client.Presentation
{
    /// <summary>One player's HUD block.</summary>
    public sealed class HudPlayerLine
    {
        public string Name = "";
        /// <summary>"Ready"/"Choosing" during selection; empty otherwise. Never the choice itself.</summary>
        public string ReadyTag = "";
        public string HpText = "";
        /// <summary>HP bar fill 0..1 (bar plus number, so HP is readable without colour).</summary>
        public double HpFraction;
        public string LandText = "";
        /// <summary>Status labels, each prefixed with its glyph.</summary>
        public List<string> Statuses = new List<string>();
        /// <summary>Statuses joined for the layout chosen by <see cref="HudModel"/> (full or glyph-only compact).</summary>
        public string StatusLine = "";
    }

    /// <summary>Everything the duel HUD shows (ticket 35), as localized strings.</summary>
    public sealed class HudState
    {
        public string RoundText = "";
        public string TimerText = "";
        /// <summary>Few seconds left: the timer shows a "!" badge and (unless reduced motion) pulses.</summary>
        public bool TimerUrgent;
        public bool Compact;
        public HudPlayerLine A = new HudPlayerLine();
        public HudPlayerLine B = new HudPlayerLine();

        public HudPlayerLine this[PlayerSide side] => side == PlayerSide.A ? A : B;
    }

    /// <summary>
    /// Builds the duel HUD from the secret-free <see cref="PublicSnapshot"/>: round and volley, both HP
    /// values (number and bar), visible statuses with glyphs, land, time remaining with an urgency
    /// badge, and each player's ready flag. At large text scales the status line switches to a
    /// compact glyph form when the estimated width would overflow the player box, so the layout
    /// stays readable at every supported text scale.
    /// </summary>
    public static class HudModel
    {
        public const double UrgentSeconds = 3.0;
        /// <summary>Player box width in reference canvas pixels (1920 x 1080 reference; 32% of the width).</summary>
        public const double PlayerBoxWidth = 1920 * 0.32;
        /// <summary>Body font size before scaling (matches UiFactory.SizeSmall for statuses).</summary>
        public const double StatusFontSize = 26;
        /// <summary>Conservative average glyph advance as a fraction of the font size (wide Indic scripts included).</summary>
        public const double AverageAdvance = 0.62;

        public static readonly string[] StatusGlyphs = { "▲", "↯", "#", "≋", "▮" };

        public static HudState Build(PublicSnapshot s, Localizer loc, Func<PlayerSide, string> names, double textScale, bool timed)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            var hud = new HudState
            {
                RoundText = loc.Format("hud.round", Math.Max(1, s.Round), Math.Max(1, s.Volley)),
                TimerText = timed ? loc.Format("hud.time", (int)Math.Ceiling(Math.Max(0, s.StageSecondsRemaining))) : string.Empty,
                TimerUrgent = timed && s.StageSecondsRemaining <= UrgentSeconds && s.StageSecondsRemaining > 0,
            };
            foreach (PlayerSide side in new[] { PlayerSide.A, PlayerSide.B })
            {
                HudPlayerLine line = hud[side];
                line.Name = names(side);
                bool locked = side == PlayerSide.A ? s.LockedA : s.LockedB;
                line.ReadyTag = s.Phase == MatchPhase.Selection ? loc.Get(locked ? "hud.ready" : "hud.waiting") : string.Empty;
                int hp = s.Hp(side);
                line.HpText = loc.Format("hud.hp", Hp.Format(hp));
                line.HpFraction = Math.Max(0, Math.Min(1, (double)hp / RulesConstants.StartHpUnits));
                line.LandText = loc.Format("hud.cells", s.Cells(side));
                PlayerStatus st = s.Status(side);
                if (st != null)
                {
                    if (st.BurnDue) line.Statuses.Add(StatusGlyphs[0] + " " + loc.Get("status.burn"));
                    if (st.ShockDue) line.Statuses.Add(StatusGlyphs[1] + " " + loc.Get("status.shock"));
                    if (st.NetDue) line.Statuses.Add(StatusGlyphs[2] + " " + loc.Get("status.net"));
                    if (st.QuakeDue) line.Statuses.Add(StatusGlyphs[3] + " " + loc.Get("status.quake"));
                    if (st.IronWallActive) line.Statuses.Add(StatusGlyphs[4] + " " + loc.Get("status.ironWall"));
                }
            }
            hud.Compact = !Fits(hud.A, textScale) || !Fits(hud.B, textScale);
            foreach (PlayerSide side in new[] { PlayerSide.A, PlayerSide.B })
            {
                HudPlayerLine line = hud[side];
                if (line.Statuses.Count == 0) line.StatusLine = loc.Get("hud.statusNone");
                else if (!hud.Compact) line.StatusLine = string.Join("  ", line.Statuses);
                else line.StatusLine = CompactLine(line.Statuses);
            }
            return hud;
        }

        /// <summary>Estimated rendered width of a status line in reference pixels.</summary>
        public static double EstimateWidth(string text, double textScale) => (text?.Length ?? 0) * StatusFontSize * textScale * AverageAdvance;

        private static bool Fits(HudPlayerLine line, double textScale) =>
            EstimateWidth(string.Join("  ", line.Statuses), textScale) <= PlayerBoxWidth;

        /// <summary>Glyph-only status line ("▲ ↯"); the full names stay in the help view.</summary>
        private static string CompactLine(List<string> statuses)
        {
            var glyphs = new List<string>(statuses.Count);
            foreach (string s in statuses)
            {
                int space = s.IndexOf(' ');
                glyphs.Add(space > 0 ? s.Substring(0, space) : s);
            }
            return string.Join(" ", glyphs);
        }
    }
}
