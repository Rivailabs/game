using UnityEngine;

namespace AstraKingdoms.Client.UI
{
    /// <summary>
    /// Pilot flat-colour palette. Players are distinguished by luminance as well as hue (dark blue vs
    /// light sand), and every state that matters also has a text label or shape.
    /// </summary>
    public static class UiTheme
    {
        public static readonly Color Background = new Color(0.08f, 0.09f, 0.12f, 0.92f);
        public static readonly Color Opaque = new Color(0.05f, 0.05f, 0.07f, 1f);
        public static readonly Color Panel = new Color(0.14f, 0.15f, 0.2f, 0.95f);
        public static readonly Color Button = new Color(0.25f, 0.28f, 0.38f, 1f);
        public static readonly Color ButtonSelected = new Color(0.95f, 0.75f, 0.25f, 1f);
        public static readonly Color ButtonPrimary = new Color(0.2f, 0.55f, 0.35f, 1f);
        public static readonly Color ButtonDanger = new Color(0.6f, 0.22f, 0.2f, 1f);
        public static readonly Color Text = new Color(0.96f, 0.96f, 0.94f, 1f);
        public static readonly Color TextDark = new Color(0.08f, 0.08f, 0.1f, 1f);
        public static readonly Color TextMuted = new Color(0.75f, 0.77f, 0.82f, 1f);
        public static readonly Color Warning = new Color(1f, 0.72f, 0.3f, 1f);

        public static readonly Color PlayerA = new Color(0.16f, 0.32f, 0.72f, 1f);
        public static readonly Color PlayerB = new Color(0.93f, 0.8f, 0.55f, 1f);

        // Board paints (Color32 so the land texture is exact and testable).
        public static readonly Color32 BoardOutside = new Color32(20, 22, 28, 255);
        public static readonly Color32 BoardA = new Color32(41, 82, 184, 255);
        public static readonly Color32 BoardB = new Color32(237, 204, 140, 255);
        public static readonly Color32 BoardEnvelopeA = new Color32(86, 128, 222, 255);
        public static readonly Color32 BoardEnvelopeB = new Color32(250, 232, 190, 255);
        public static readonly Color32 BoardPreview = new Color32(40, 200, 90, 255);
        public static readonly Color32 BoardDiscarded = new Color32(140, 140, 140, 255);
        public static readonly Color32 BoardAnchor = new Color32(230, 40, 40, 255);
        public static readonly Color32 BoardStroke = new Color32(255, 255, 255, 255);
        public static readonly Color32 BoardRejected = new Color32(200, 60, 160, 255);
    }
}
