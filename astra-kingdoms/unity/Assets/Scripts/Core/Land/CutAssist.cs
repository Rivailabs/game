using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Bots;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;

namespace AstraKingdoms.Client.Land
{
    /// <summary>
    /// Presentation helpers for the cut screen. They only choose convenient defaults (anchor and
    /// starting pose); legality and the transferred cells always come from the engine's preview.
    /// </summary>
    public static class CutAssist
    {
        /// <summary>
        /// The border anchor (loser-owned, sharing a full edge with the winner) inside the posed
        /// envelope that is closest to the envelope centre; when a polygon is given, only anchors the
        /// filled polygon contains qualify. Ties choose the lower cell ID. Returns -1 when none exists.
        /// </summary>
        public static int PickAnchor(Territory territory, PlayerSide winner, CardId card, CardPose pose, IReadOnlyList<CellPoint> polygon)
        {
            if (territory == null) throw new ArgumentNullException(nameof(territory));
            if (!pose.IsInRange) return -1;
            CutPolygon poly = null;
            if (polygon != null && polygon.Count > 0 && CutPolygon.TryCreate(polygon, out CutPolygon p) == CutRejection.None) poly = p;
            int best = -1;
            long bestD = long.MaxValue;
            foreach (int cell in CardEnvelope.RasterizeOnBoard(card, pose))
            {
                if (!territory.IsBorderAnchor(cell, winner)) continue;
                int x = Board.X(cell), y = Board.Y(cell);
                if (poly != null && !poly.Contains(x, y)) continue;
                long dx = x - pose.CenterX, dy = y - pose.CenterY;
                long d = dx * dx + dy * dy;
                if (d < bestD)
                {
                    bestD = d;
                    best = cell;
                }
            }
            return best;
        }

        /// <summary>
        /// Starting pose for a card: centred on the border anchor nearest to <paramref name="nearCellId"/>
        /// (the duel's frontier challenge cell), pointing into the loser's land, at 60% of the largest
        /// scale the allowance permits. Returns false when the loser has no border cell left.
        /// </summary>
        public static bool DefaultPose(Territory territory, PlayerSide winner, CardId card, int quota, int nearCellId, out CardPose pose)
        {
            pose = default;
            if (territory == null) throw new ArgumentNullException(nameof(territory));
            int rotation = winner == PlayerSide.A ? 0 : RulesConstants.RotationSteps / 2;
            int anchor = NearestBorderAnchor(territory, winner, nearCellId);
            if (anchor < 0 || quota <= 0) return false;
            int maxScale = CutPlanner.LargestFittingScale(card, rotation, quota);
            if (maxScale < RulesConstants.MinScaleQuarters) return false;
            int scale = Math.Max(RulesConstants.MinScaleQuarters, maxScale * 60 / 100);
            pose = new CardPose(Board.X(anchor), Board.Y(anchor), scale, rotation);
            return true;
        }

        public static int NearestBorderAnchor(Territory territory, PlayerSide winner, int nearCellId)
        {
            int nx = nearCellId >= 0 ? Board.X(nearCellId) : Board.Size / 2;
            int ny = nearCellId >= 0 ? Board.Y(nearCellId) : Board.Size / 2;
            int best = -1;
            long bestD = long.MaxValue;
            foreach (int cell in Board.ActiveCellIds)
            {
                if (!territory.IsBorderAnchor(cell, winner)) continue;
                long dx = Board.X(cell) - nx, dy = Board.Y(cell) - ny;
                long d = dx * dx + dy * dy;
                if (d < bestD)
                {
                    bestD = d;
                    best = cell;
                }
            }
            return best;
        }

        /// <summary>Clamps a requested scale to [1, largest scale whose envelope fits 2Q].</summary>
        public static int ClampScale(CardId card, int rotation, int quota, int requestedQuarters)
        {
            int max = Math.Max(RulesConstants.MinScaleQuarters, CutPlanner.LargestFittingScale(card, rotation, quota));
            if (requestedQuarters < RulesConstants.MinScaleQuarters) return RulesConstants.MinScaleQuarters;
            return requestedQuarters > max ? max : requestedQuarters;
        }
    }
}
