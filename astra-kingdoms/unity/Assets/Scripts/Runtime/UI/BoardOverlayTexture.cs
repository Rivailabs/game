using AstraKingdoms.Client.Land;
using AstraKingdoms.Rules.Land;
using UnityEngine;

namespace AstraKingdoms.Client.UI
{
    /// <summary>
    /// The 1,024 x 1,024 land overlay (tickets 37 and 41): owner patterns (A hatch, B dots), the
    /// rendered ownership contour and terrain markers, drawn over the per-cell ownership texture. It
    /// is regenerated only when ownership changes, reads a copy of the territory, and never measures
    /// area (totals always come from exact cell counts).
    /// </summary>
    public sealed class BoardOverlayTexture
    {
        private static readonly Color32 None = new Color32(0, 0, 0, 0);
        private static readonly Color32 PatternInk = new Color32(0, 0, 0, 80);
        private static readonly Color32 ContourInk = new Color32(255, 255, 255, 255);
        private static readonly Color32 TerrainInk = new Color32(20, 20, 20, 230);

        private readonly Color32[] _pixels = new Color32[OwnershipOverlay.Size * OwnershipOverlay.Size];

        public OwnershipOverlay Overlay { get; } = new OwnershipOverlay();
        public Texture2D Texture { get; }

        public BoardOverlayTexture()
        {
            Texture = new Texture2D(OwnershipOverlay.Size, OwnershipOverlay.Size, TextureFormat.RGBA32, false);
            Texture.name = "BoardOverlay";
            Texture.filterMode = FilterMode.Bilinear;
            Texture.wrapMode = TextureWrapMode.Clamp;
        }

        /// <summary>Repaints patterns, contour and terrain markers from a territory copy.</summary>
        public void Show(Territory territory, bool patterns)
        {
            byte[] owners = OwnershipContours.Snapshot(territory);
            Overlay.Paint(owners, patterns, OwnershipContours.Build(owners));
            foreach (TerrainLabel l in BoardLabels.TerrainRegions(territory))
                Overlay.MarkTerrain(Board.X(l.AnchorCellId), Board.Y(l.AnchorCellId));
            Upload();
        }

        /// <summary>Repaints from an ownership snapshot (the transfer animation frames).</summary>
        public void Show(byte[] owners, bool patterns)
        {
            Overlay.Paint(owners, patterns, OwnershipContours.Build(owners));
            Upload();
        }

        private void Upload()
        {
            byte[] ink = Overlay.Ink;
            for (int i = 0; i < _pixels.Length; i++)
            {
                switch ((OverlayInk)ink[i])
                {
                    case OverlayInk.Pattern: _pixels[i] = PatternInk; break;
                    case OverlayInk.Contour: _pixels[i] = ContourInk; break;
                    case OverlayInk.Terrain: _pixels[i] = TerrainInk; break;
                    default: _pixels[i] = None; break;
                }
            }
            Texture.SetPixels32(_pixels);
            Texture.Apply(false, false);
        }
    }
}
