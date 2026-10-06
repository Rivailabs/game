using AstraKingdoms.Client.Combat;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using UnityEngine;

namespace AstraKingdoms.Client.Arena
{
    /// <summary>
    /// Grey-box arena geometry derived from the rules' combat geometry (metres). The rules frame is
    /// right-handed (A faces +x, local Right = +z, up = +y); Unity is left-handed, so world z is
    /// mirrored (unity.z = -rules.z) to keep "left" and "right" dodges on the correct visual side.
    /// The capsule primitive is scaled to the rules' body test: axis 0.35-1.45 m, radius 0.25 m.
    /// </summary>
    public static class ArenaLayout
    {
        public const string FighterAName = "FighterA";
        public const string FighterBName = "FighterB";
        public const string ArenaRootName = "Arena";

        public static readonly float CapsuleBottom = (float)CombatGeometry.CapsuleBottom.ToDouble();
        public static readonly float CapsuleTop = (float)CombatGeometry.CapsuleTop.ToDouble();
        public static readonly float CoreRadius = (float)CombatGeometry.CoreRadius.ToDouble();

        /// <summary>Centre height of the capsule body (0.9 m).</summary>
        public static float CapsuleCentreY => (CapsuleBottom + CapsuleTop) / 2f;

        /// <summary>Unity's capsule primitive is 2 m tall with radius 0.5 m; this scale matches the rules' capsule.</summary>
        public static Vector3 CapsuleScale
        {
            get
            {
                float height = CapsuleTop - CapsuleBottom + 2f * CoreRadius;
                return new Vector3(CoreRadius * 2f, height / 2f, CoreRadius * 2f);
            }
        }

        /// <summary>Fighter standing position (x = 0 for A, x = 8 for B), at the capsule centre height.</summary>
        public static Vector3 FighterPosition(PlayerSide side) =>
            new Vector3((float)CombatGeometry.PlaneX(side).ToDouble(), CapsuleCentreY, 0f);

        public static Vector3 ToUnity(PreviewPoint p) => new Vector3((float)p.X, (float)p.Y, (float)-p.Z);

        public static Vector3 ToUnity(FixedVector3 v) => new Vector3((float)v.X.ToDouble(), (float)v.Y.ToDouble(), (float)-v.Z.ToDouble());

        /// <summary>
        /// Visual body position for a resolution window: baseline lateral offset plus the effective
        /// dodge (side dodge 0.45 m along the defender's own Right, jump 0.5 m up).
        /// </summary>
        public static Vector3 PosedPosition(PlayerSide side, long baselineOffsetRightRaw, Dodge dodge)
        {
            Fixed right = Fixed.FromRaw(baselineOffsetRightRaw);
            if (dodge == Dodge.Right) right = right + CombatGeometry.SideDodgeShift;
            else if (dodge == Dodge.Left) right = right - CombatGeometry.SideDodgeShift;
            float worldZRules = (float)CombatGeometry.LocalRightToWorldZ(side, right).ToDouble();
            Vector3 p = FighterPosition(side);
            p.z = -worldZRules;
            if (dodge == Dodge.Jump) p.y += (float)CombatGeometry.JumpRaise.ToDouble();
            return p;
        }

        /// <summary>Ground plane: 16 m x 8 m centred between the fighters (Unity's plane primitive is 10 m x 10 m).</summary>
        public static Vector3 GroundPosition => new Vector3(4f, 0f, 0f);
        public static Vector3 GroundScale => new Vector3(1.6f, 1f, 0.8f);

        /// <summary>Side-on camera framing both fighters in landscape.</summary>
        public static Vector3 CameraPosition => new Vector3(4f, 2.6f, -9.5f);
        public static Vector3 CameraTarget => new Vector3(4f, 1.1f, 0f);
        public const float CameraFieldOfView = 38f;
    }
}
