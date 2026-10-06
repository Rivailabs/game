using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Combat;
using AstraKingdoms.Rules.Core;
using AstraKingdoms.Rules.Land;
using AstraKingdoms.Rules.Match;

namespace AstraKingdoms.Modes.FourPlayer
{
    /// <summary>
    /// A command to a <see cref="FourPlayerMatch"/>. Player commands carry the authenticated
    /// sender's label; server commands (deadlines) have a null <see cref="Sender"/>. Accepted
    /// commands form the replay log: the same roster, seed, match ID and log reproduce every
    /// wave hash.
    /// </summary>
    public abstract class FourPlayerCommand
    {
        public Kingdom? Sender { get; }

        protected FourPlayerCommand(Kingdom? sender)
        {
            Sender = sender;
        }

        /// <summary>Deterministic text of the command (log hashing and diagnostics).</summary>
        public abstract void WriteTo(CanonicalWriter w);
    }

    /// <summary>Private loadout from the room's symmetric catalog (Setup only).</summary>
    public sealed class SubmitLoadout4P : FourPlayerCommand
    {
        public IReadOnlyList<int> Weapons { get; }
        public int Reserve { get; }

        public SubmitLoadout4P(Kingdom sender, IReadOnlyList<int> weapons, int reserve = 0) : base(sender)
        {
            Weapons = weapons ?? throw new ArgumentNullException(nameof(weapons));
            Reserve = reserve;
        }

        public override void WriteTo(CanonicalWriter w)
        {
            w.Ascii("loadout").U8((int)Sender.Value).U32((uint)Weapons.Count);
            foreach (int id in Weapons) w.I32(id);
            w.I32(Reserve);
        }
    }

    /// <summary>A hidden volley choice for the sender's duel in the given wave.</summary>
    public sealed class Lock4P : FourPlayerCommand
    {
        public int Wave { get; }
        public int Volley { get; }
        public VolleyInput Choice { get; }

        public Lock4P(Kingdom sender, int wave, int volley, VolleyInput choice) : base(sender)
        {
            Wave = wave;
            Volley = volley;
            Choice = choice ?? throw new ArgumentNullException(nameof(choice));
        }

        public override void WriteTo(CanonicalWriter w) =>
            w.Ascii("lock").U8((int)Sender.Value).I32(Wave).I32(Volley).I32(Choice.WeaponId).I32(Choice.PitchQdeg)
             .I32(Choice.YawQdeg).I32(Choice.PowerPercent).U8((int)Choice.Dodge);
    }

    /// <summary>
    /// A pair winner's cut against the wave's frozen board. <see cref="Vertices"/> null means Auto Cut.
    /// </summary>
    public sealed class SubmitCut4P : FourPlayerCommand
    {
        public int Wave { get; }
        public CardId Card { get; }
        public CardPose Pose { get; }
        public CellPoint Anchor { get; }
        public IReadOnlyList<CellPoint> Vertices { get; }
        public CutMode Mode => Vertices == null ? CutMode.Auto : CutMode.Manual;

        public SubmitCut4P(Kingdom sender, int wave, CardId card, CardPose pose, CellPoint anchor, IReadOnlyList<CellPoint> vertices = null)
            : base(sender)
        {
            Wave = wave;
            Card = card;
            Pose = pose;
            Anchor = anchor;
            Vertices = vertices;
        }

        public override void WriteTo(CanonicalWriter w)
        {
            w.Ascii("cut").U8((int)Sender.Value).I32(Wave).U8((int)Card).I32(Pose.CenterX).I32(Pose.CenterY)
             .I32(Pose.ScaleQuarters).I32(Pose.Rotation).I32(Anchor.X).I32(Anchor.Y).U8((int)Mode);
            if (Vertices == null) return;
            w.U32((uint)Vertices.Count);
            foreach (CellPoint p in Vertices) w.I32(p.X).I32(p.Y);
        }
    }

    /// <summary>The sender leaves the match. Their land becomes locked neutral at the next settlement.</summary>
    public sealed class Forfeit4P : FourPlayerCommand
    {
        public Forfeit4P(Kingdom sender) : base(sender)
        {
        }

        public override void WriteTo(CanonicalWriter w) => w.Ascii("forfeit").U8((int)Sender.Value);
    }

    /// <summary>Server: the setup deadline passed; every entrant without a loadout forfeits.</summary>
    public sealed class ExpireSetup4P : FourPlayerCommand
    {
        public ExpireSetup4P() : base(null)
        {
        }

        public override void WriteTo(CanonicalWriter w) => w.Ascii("expire-setup");
    }

    /// <summary>Server: the selection deadline of one pair passed; missing locks become Pass.</summary>
    public sealed class ExpireSelection4P : FourPlayerCommand
    {
        public int Wave { get; }
        public int PairSlot { get; }

        public ExpireSelection4P(int wave, int pairSlot) : base(null)
        {
            Wave = wave;
            PairSlot = pairSlot;
        }

        public override void WriteTo(CanonicalWriter w) => w.Ascii("expire-selection").I32(Wave).I32(PairSlot);
    }

    /// <summary>Server: one pair winner's cut window ended without an accepted cut (zero transfer).</summary>
    public sealed class ExpireCut4P : FourPlayerCommand
    {
        public int Wave { get; }
        public int PairSlot { get; }

        public ExpireCut4P(int wave, int pairSlot) : base(null)
        {
            Wave = wave;
            PairSlot = pairSlot;
        }

        public override void WriteTo(CanonicalWriter w) => w.Ascii("expire-cut").I32(Wave).I32(PairSlot);
    }
}
