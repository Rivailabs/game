using AstraKingdoms.Client.Land;
using AstraKingdoms.Rules.Land;
using UnityEngine;

namespace AstraKingdoms.Client.UI
{
    /// <summary>
    /// The 256x256 ownership map as a point-filtered texture: one pixel per logical cell, painted from
    /// <see cref="BoardRaster"/>. Contours are never used to measure area.
    /// </summary>
    public sealed class BoardTexture
    {
        private readonly Color32[] _pixels = new Color32[BoardRaster.PixelCount];

        public BoardRaster Raster { get; } = new BoardRaster();
        public Texture2D Texture { get; }

        public BoardTexture()
        {
            Texture = new Texture2D(BoardRaster.Size, BoardRaster.Size, TextureFormat.RGBA32, false);
            Texture.name = "BoardOwnership";
            Texture.filterMode = FilterMode.Point;
            Texture.wrapMode = TextureWrapMode.Clamp;
        }

        public void ShowOwnership(Territory territory)
        {
            Raster.PaintOwnership(territory);
            Upload();
        }

        /// <summary>Copies the raster's paints into the texture.</summary>
        public void Upload()
        {
            byte[] paint = Raster.Paint;
            for (int i = 0; i < _pixels.Length; i++) _pixels[i] = ColorOf((CellPaint)paint[i]);
            Texture.SetPixels32(_pixels);
            Texture.Apply(false, false);
        }

        public static Color32 ColorOf(CellPaint p)
        {
            switch (p)
            {
                case CellPaint.OwnerA: return UiTheme.BoardA;
                case CellPaint.OwnerB: return UiTheme.BoardB;
                case CellPaint.EnvelopeA: return UiTheme.BoardEnvelopeA;
                case CellPaint.EnvelopeB: return UiTheme.BoardEnvelopeB;
                case CellPaint.Preview: return UiTheme.BoardPreview;
                case CellPaint.Discarded: return UiTheme.BoardDiscarded;
                case CellPaint.Anchor: return UiTheme.BoardAnchor;
                case CellPaint.Stroke: return UiTheme.BoardStroke;
                case CellPaint.Rejected: return UiTheme.BoardRejected;
                default: return UiTheme.BoardOutside;
            }
        }

        public void Destroy()
        {
            if (Texture != null) Object.Destroy(Texture);
        }
    }
}
