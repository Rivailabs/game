using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Rules.Bots
{
    /// <summary>Search budget for <see cref="CutPlanner"/>.</summary>
    public sealed class CutSearchBudget
    {
        /// <summary>Anchors tried for every (card, rotation) candidate in the first stage.</summary>
        public int Anchors = 1;
        /// <summary>Additional anchors tried only for the best (card, rotation) of the first stage.</summary>
        public int RefineAnchors;
        public int Rotations = 1;
        /// <summary>Consider every offered card (otherwise only the largest-quota card).</summary>
        public bool AllCards;
        /// <summary>Fraction of the largest legal scale to use, in percent.</summary>
        public int ScalePercent = 100;
        /// <summary>Envelope-centre offset into enemy land, in percent of the envelope radius.</summary>
        public int CenterOffsetPercent = 50;
    }

    /// <summary>
    /// Plans an Auto Cut from public information only: border anchors from the ownership map, the
    /// offered cards and their quotas. For each candidate (card, rotation, anchor, centre offset) it
    /// takes the largest scale whose canonical envelope fits 2Q, pushes the envelope centre into the
    /// loser's land and previews <see cref="CutValidator.AutoCut"/>; the largest preview wins (ties:
    /// first found, so the search is deterministic).
    /// </summary>
    public static class CutPlanner
    {
        public static CutPlan Plan(PlayerView view, CutSearchBudget budget, BotRng rng)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (!view.IsCutTurn || view.OfferedCards.Count == 0) return null;
            Territory territory = view.CloneTerritory();
            PlayerSide winner = view.Viewer;

            List<int> border = Frontier.Cells(territory, winner);
            if (border.Count == 0) return null;
            int stageOne = Math.Max(1, Math.Min(budget.Anchors, border.Count));
            int total = Math.Max(stageOne, Math.Min(stageOne + Math.Max(0, budget.RefineAnchors), border.Count));
            // Evenly spread along the canonical border list with a random phase.
            var anchors = new List<int>(total);
            int phase = rng.Next(border.Count);
            for (int i = 0; i < total; i++) anchors.Add(border[(phase + (int)((long)i * border.Count / total)) % border.Count]);
            // Interleave so the first stage uses a spread-out subset.
            var first = new List<int>();
            var refine = new List<int>();
            for (int i = 0; i < total; i++)
            {
                if (first.Count < stageOne && (long)i * stageOne / total == first.Count) first.Add(anchors[i]);
                else refine.Add(anchors[i]);
            }

            var cards = new List<int>(); // indices into the offer
            if (budget.AllCards)
            {
                for (int i = 0; i < view.OfferedCards.Count; i++) cards.Add(i);
            }
            else
            {
                int bestIndex = 0;
                for (int i = 1; i < view.OfferedQuotas.Count; i++)
                    if (view.OfferedQuotas[i] > view.OfferedQuotas[bestIndex]) bestIndex = i;
                cards.Add(bestIndex);
            }

            var rotations = new List<int>();
            int rotationCount = Math.Max(1, Math.Min(budget.Rotations, RulesConstants.RotationSteps));
            int rotationPhase = rng.Next(RulesConstants.RotationSteps);
            for (int i = 0; i < rotationCount; i++) rotations.Add((rotationPhase + i * RulesConstants.RotationSteps / rotationCount) % RulesConstants.RotationSteps);

            CutPlan best = null;
            int bestCardIndex = -1, bestScale = 0, bestRotation = 0;
            foreach (int ci in cards)
            {
                CardId card = view.OfferedCards[ci];
                int quota = view.OfferedQuotas[ci];
                if (quota <= 0) continue;
                foreach (int rotation in rotations)
                {
                    int maxScale = LargestFittingScale(card, rotation, quota);
                    if (maxScale < RulesConstants.MinScaleQuarters) continue;
                    int scale = Math.Max(RulesConstants.MinScaleQuarters, maxScale * budget.ScalePercent / 100);
                    foreach (int anchor in first)
                    {
                        CutPlan plan = Evaluate(territory, winner, card, anchor, scale, rotation, budget.CenterOffsetPercent, quota);
                        if (plan != null && (best == null || plan.ExpectedCells > best.ExpectedCells))
                        {
                            best = plan;
                            bestCardIndex = ci;
                            bestScale = scale;
                            bestRotation = rotation;
                        }
                    }
                }
            }
            if (best == null) return null;
            foreach (int anchor in refine)
            {
                CutPlan plan = Evaluate(territory, winner, best.Card, anchor, bestScale, bestRotation, budget.CenterOffsetPercent,
                    view.OfferedQuotas[bestCardIndex]);
                if (plan != null && plan.ExpectedCells > best.ExpectedCells) best = plan;
            }
            return best;
        }

        private static CutPlan Evaluate(Territory territory, PlayerSide winner, CardId card, int anchor, int scale, int rotation,
            int offsetPercent, int quota)
        {
            CardPose pose = PoseFor(territory, winner, card, anchor, scale, rotation, offsetPercent);
            CutResult preview = CutValidator.AutoCut(territory, winner, card, pose, CellPoint.FromCellId(anchor), quota);
            if (!preview.IsAccepted) return null;
            return new CutPlan { Card = card, AnchorCellId = anchor, Pose = pose, ExpectedCells = preview.Cells.Count };
        }

        private const int ReferenceScale = 64;

        /// <summary>
        /// Largest scale (quarters) whose canonical envelope holds at most 2Q cells. Envelope area
        /// grows with the square of the scale, so an integer estimate from a cached reference count
        /// brackets the answer before an exact binary search over <see cref="CardEnvelope.FitsAllowance"/>.
        /// </summary>
        public static int LargestFittingScale(CardId card, int rotation, int quota)
        {
            if (quota <= 0) return 0;
            long reference = CardEnvelope.CountCanonicalCells(card, ReferenceScale, rotation);
            // count(s) ~ reference * (s / 64)^2  =>  s ~ 64 * sqrt(2Q / reference).
            long estimate = ReferenceScale * ISqrt(2L * quota * 4096 / reference) / 64;
            int lo = (int)Math.Max(0, estimate * 85 / 100 - 1);
            int hi = (int)Math.Min(RulesConstants.MaxScaleQuarters, estimate * 115 / 100 + 2);
            bool Fits(int s) => s >= RulesConstants.MinScaleQuarters && CardEnvelope.FitsAllowance(card, s, rotation, quota);
            if (lo > 0 && !Fits(lo)) lo = 0;
            if (Fits(hi)) hi = RulesConstants.MaxScaleQuarters;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (Fits(mid)) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }

        private static long ISqrt(long n)
        {
            if (n <= 0) return 0;
            // Integer Newton iteration (no floating point in the rules assembly).
            long x = n;
            long y = (x + 1) / 2;
            while (y < x)
            {
                x = y;
                y = (x + n / x) / 2;
            }
            return x;
        }

        /// <summary>
        /// Centres the envelope inside the loser's land: from the anchor, step away from the winner's
        /// adjacent cell by a fraction of the envelope radius, backing off until the posed envelope
        /// contains the anchor (a centre on the anchor always does).
        /// </summary>
        private static CardPose PoseFor(Territory territory, PlayerSide winner, CardId card, int anchor, int scale, int rotation, int offsetPercent)
        {
            int ax = Board.X(anchor), ay = Board.Y(anchor);
            int dx = 0, dy = 0;
            for (int d = 0; d < 4; d++)
            {
                if (territory.IsOwnedBy(ax + Board.NeighbourDx[d], ay + Board.NeighbourDy[d], winner))
                {
                    dx -= Board.NeighbourDx[d];
                    dy -= Board.NeighbourDy[d];
                }
            }
            int radiusCells = scale / 4; // shapes span roughly one local unit around the origin
            for (int k = radiusCells * offsetPercent / 100; k > 0; k = k * 2 / 3)
            {
                int cx = Clamp(ax + dx * k), cy = Clamp(ay + dy * k);
                var pose = new CardPose(cx, cy, scale, rotation);
                if (CardEnvelope.Contains(card, pose, ax, ay)) return pose;
            }
            return new CardPose(ax, ay, scale, rotation);
        }

        private static int Clamp(int v) => v < 0 ? 0 : v > Board.Size - 1 ? Board.Size - 1 : v;
    }
}
