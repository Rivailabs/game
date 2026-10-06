using System;

namespace AstraKingdoms.Client.Presentation
{
    /// <summary>Bow dimensions (metres) for the procedural string (ticket 28).</summary>
    public sealed class BowGeometry
    {
        /// <summary>Distance from the grip to each limb tip along the bow's up axis.</summary>
        public double HalfLength = 0.62;
        /// <summary>How far the tips sit behind the grip (limb curve), toward the archer.</summary>
        public double TipSetback = 0.12;
        /// <summary>String-to-grip distance at rest (brace height).</summary>
        public double BraceHeight = 0.18;
        /// <summary>Additional pull at full draw beyond brace height.</summary>
        public double MaxDraw = 0.52;
        /// <summary>Arrow length; its tip rests at the grip when nocked at full draw.</summary>
        public double ArrowLength = 0.70;
    }

    /// <summary>The three string points plus the arrow placement derived from them.</summary>
    public struct BowstringPoints
    {
        public V3 TopTip;
        public V3 Nock;
        public V3 BottomTip;
        /// <summary>Where the nocked arrow's tail sits (equals <see cref="Nock"/>).</summary>
        public V3 ArrowTail;
        /// <summary>Arrow tip position along the aim line.</summary>
        public V3 ArrowTip;
        /// <summary>Unit aim direction (bow forward).</summary>
        public V3 AimDirection;
    }

    /// <summary>
    /// Procedural bowstring driven by the draw state and the grip frame (plan: "The bowstring is
    /// driven from draw state and attachment points"). The string runs tip - nock - tip; the nock
    /// lies on the aim line through the grip (arrow rest) at brace height plus the drawn distance,
    /// so grip, nock and arrow stay collinear at every draw amount and aiming angle. At release the
    /// draw amount returns to zero and the nock snaps back to brace height.
    /// </summary>
    public static class BowstringSolver
    {
        /// <param name="grip">Grip (arrow rest) position in world space.</param>
        /// <param name="forward">Aim direction the bow faces (need not be normalized).</param>
        /// <param name="up">Bow's limb axis (orthogonalized against forward here).</param>
        /// <param name="draw">0 at rest to 1 at full draw (clamped).</param>
        public static BowstringPoints Solve(V3 grip, V3 forward, V3 up, double draw, BowGeometry bow)
        {
            if (bow == null) throw new ArgumentNullException(nameof(bow));
            V3 f = forward.Normalized;
            if (f.Length < 0.5) throw new ArgumentException("Forward must be non-zero.", nameof(forward));
            V3 u = (up - f * V3.Dot(up, f)).Normalized;
            if (u.Length < 0.5) throw new ArgumentException("Up must not be parallel to forward.", nameof(up));
            double d = draw < 0 ? 0 : draw > 1 ? 1 : draw;

            V3 back = -f;
            var p = new BowstringPoints
            {
                TopTip = grip + u * bow.HalfLength + back * bow.TipSetback,
                BottomTip = grip - u * bow.HalfLength + back * bow.TipSetback,
                Nock = grip + back * (bow.BraceHeight + d * bow.MaxDraw),
                AimDirection = f,
            };
            p.ArrowTail = p.Nock;
            p.ArrowTip = p.Nock + f * bow.ArrowLength;
            return p;
        }

        /// <summary>Total string length tip-nock-tip (grows with draw; for sanity checks and stretch shading).</summary>
        public static double StringLength(BowstringPoints p) => V3.Distance(p.TopTip, p.Nock) + V3.Distance(p.Nock, p.BottomTip);
    }
}
