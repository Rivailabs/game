using System;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;

namespace AstraKingdoms.Client.Land
{
    /// <summary>
    /// Converts positions on the displayed board image to board cells. The image shows the 256x256
    /// grid with x to the right and y downward; (u, vDown) are normalized image coordinates in [0,1]
    /// measured from the top-left corner. Snapping uses the rules' own helper
    /// (<see cref="CutPolygon.SnapPoint"/>: nearest cell centre, exact half ties to the lower index),
    /// so the client never invents its own rounding.
    /// </summary>
    public static class BoardMapping
    {
        /// <summary>Fixed-point denominator for pointer positions (1/4096 of a cell).</summary>
        public const long Denominator = 4096;

        /// <summary>Pointer at normalized image coordinates to a snapped cell-centre vertex.</summary>
        public static CellPoint Snap(double u, double vDown)
        {
            // Image corner origin -> cell-centre space: cell i spans [i, i+1) with its centre at i + 0.5.
            long xNum = (long)Math.Round((u * Board.Size - 0.5) * Denominator, MidpointRounding.AwayFromZero);
            long yNum = (long)Math.Round((vDown * Board.Size - 0.5) * Denominator, MidpointRounding.AwayFromZero);
            return CutPolygon.SnapPoint(xNum, yNum, Denominator);
        }

        /// <summary>Normalized image coordinates (top-left origin) of a cell centre.</summary>
        public static void CellCentre(int x, int y, out double u, out double vDown)
        {
            u = (x + 0.5) / Board.Size;
            vDown = (y + 0.5) / Board.Size;
        }

        /// <summary>Texture pixel index for a board cell: textures store rows bottom-up, the board is y-down.</summary>
        public static int TextureIndex(int x, int y) => (Board.Size - 1 - y) * Board.Size + x;

        /// <summary>Rotation index (0-15, clockwise on the y-down map) nearest to an angle in degrees.</summary>
        public static int RotationFromDegrees(double degreesClockwise)
        {
            double steps = degreesClockwise / (360.0 / RulesConstants.RotationSteps);
            int r = (int)Math.Round(steps, MidpointRounding.AwayFromZero) % RulesConstants.RotationSteps;
            return r < 0 ? r + RulesConstants.RotationSteps : r;
        }
    }
}
