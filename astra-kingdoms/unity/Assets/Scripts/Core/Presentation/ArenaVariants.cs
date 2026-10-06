using System;
using System.Collections.Generic;
using AstraKingdoms.Rules.Combat;

namespace AstraKingdoms.Client.Presentation
{
    /// <summary>One grey-box prop: a Unity primitive with a world transform (Unity frame, metres).</summary>
    public sealed class ArenaProp
    {
        public string Name { get; }
        /// <summary>Unity primitive name: Cube, Cylinder, Sphere, Capsule, Plane or Quad.</summary>
        public string Primitive { get; }
        public V3 Position { get; }
        public V3 Scale { get; }
        /// <summary>Index into the variant's flat-colour palette (one opaque material per index).</summary>
        public int Material { get; }

        public ArenaProp(string name, string primitive, V3 position, V3 scale, int material)
        {
            Name = name;
            Primitive = primitive;
            Position = position;
            Scale = scale;
            Material = material;
        }

        /// <summary>Axis-aligned half extents (primitives are unit-sized except Plane = 10 m and Cylinder/Capsule = 2 m tall).</summary>
        public V3 HalfExtents
        {
            get
            {
                switch (Primitive)
                {
                    case "Plane": return new V3(5 * Scale.X, 0, 5 * Scale.Z);
                    case "Cylinder":
                    case "Capsule": return new V3(0.5 * Scale.X, Scale.Y, 0.5 * Scale.Z);
                    default: return new V3(0.5 * Scale.X, 0.5 * Scale.Y, 0.5 * Scale.Z);
                }
            }
        }
    }

    /// <summary>A grey-box arena treatment. Both variants share the rules and navigation (plan: a second arena must not double validation).</summary>
    public sealed class ArenaVariant
    {
        public string Id { get; }
        public string NameKey { get; }
        /// <summary>Flat palette (RGB 0..1); index 0 is the ground.</summary>
        public IReadOnlyList<V3> Palette { get; }
        public V3 Sky { get; }
        public IReadOnlyList<ArenaProp> Props { get; }

        public ArenaVariant(string id, string nameKey, V3 sky, IReadOnlyList<V3> palette, IReadOnlyList<ArenaProp> props)
        {
            Id = id;
            NameKey = nameKey;
            Sky = sky;
            Palette = palette;
            Props = props;
        }

        public int EstimatedTriangles
        {
            get
            {
                int n = 0;
                foreach (ArenaProp p in Props) n += AssetBudgetValidator.PrimitiveTriangles(p.Primitive);
                return n;
            }
        }

        public int MaterialBatches
        {
            get
            {
                var used = new HashSet<int>();
                foreach (ArenaProp p in Props) used.Add(p.Material);
                return used.Count;
            }
        }
    }

    /// <summary>
    /// The two arena treatments (ticket 36), built from primitives by the editor scene builder and
    /// at run time. Layout rule: no prop may intrude into the authoritative flight volume (rules
    /// bounds x -2..10 m, |z| &lt;= 3 m, y &lt;= 12 m) or stand between the side camera and the
    /// fighters, so nothing looks like cover or an obstacle that the rules do not have.
    /// </summary>
    public static class ArenaVariants
    {
        public const string Courtyard = "courtyard";
        public const string Riverside = "riverside";

        /// <summary>Camera z in Unity space (the side camera sits on the negative z side).</summary>
        public const double CameraZ = -9.5;

        private static readonly Lazy<IReadOnlyList<ArenaVariant>> AllLazy = new Lazy<IReadOnlyList<ArenaVariant>>(Build);

        public static IReadOnlyList<ArenaVariant> All => AllLazy.Value;

        public static ArenaVariant Get(string id)
        {
            foreach (ArenaVariant v in All)
                if (v.Id == id) return v;
            return All[0];
        }

        /// <summary>Problems with a variant's layout (empty when it is safe).</summary>
        public static IReadOnlyList<string> Validate(ArenaVariant v)
        {
            var problems = new List<string>();
            double minX = CombatGeometry.MinX.ToDouble(), maxX = CombatGeometry.MaxX.ToDouble();
            double maxY = CombatGeometry.MaxY.ToDouble(), maxZ = CombatGeometry.MaxZ.ToDouble(); // |z| bound (symmetric)
            foreach (ArenaProp p in v.Props)
            {
                if (p.Name == "Ground") continue;
                V3 h = p.HalfExtents;
                double x0 = p.Position.X - h.X, x1 = p.Position.X + h.X;
                double y0 = p.Position.Y - h.Y;
                double z0 = p.Position.Z - h.Z, z1 = p.Position.Z + h.Z;
                bool overlapsFlight = x1 > minX && x0 < maxX && y0 < maxY && z1 > -maxZ && z0 < maxZ;
                if (overlapsFlight) problems.Add(v.Id + ": " + p.Name + " intrudes into the flight volume");
                bool inFrontOfFighters = z0 < -maxZ && z1 > CameraZ && x1 > minX - 2 && x0 < maxX + 2;
                if (inFrontOfFighters) problems.Add(v.Id + ": " + p.Name + " stands between the camera and the fighters");
                if (p.Material < 0 || p.Material >= v.Palette.Count) problems.Add(v.Id + ": " + p.Name + " has no palette entry");
            }
            if (v.EstimatedTriangles > AssetBudgets.ArenaTriangles) problems.Add(v.Id + ": over the arena triangle budget");
            if (v.MaterialBatches > AssetBudgets.ArenaOpaqueBatches) problems.Add(v.Id + ": over the opaque material batch budget");
            return problems;
        }

        private static IReadOnlyList<ArenaVariant> Build()
        {
            var list = new List<ArenaVariant>();

            // Courtyard: sandstone floor, a colonnade and wall behind the fighters, side gates.
            var court = new List<ArenaProp> { new ArenaProp("Ground", "Plane", new V3(4, 0, 0), new V3(2.4, 1, 2.4), 0) };
            for (int i = 0; i < 7; i++)
                court.Add(new ArenaProp("Pillar" + i, "Cylinder", new V3(-2 + i * 2, 1.6, 5.5), new V3(0.6, 1.6, 0.6), 1));
            court.Add(new ArenaProp("BackWall", "Cube", new V3(4, 1.5, 7.5), new V3(18, 3, 0.6), 2));
            court.Add(new ArenaProp("Lintel", "Cube", new V3(4, 3.4, 5.5), new V3(14, 0.4, 1), 2));
            court.Add(new ArenaProp("GateLeft", "Cube", new V3(-5, 1.8, 0), new V3(1.2, 3.6, 4), 3));
            court.Add(new ArenaProp("GateRight", "Cube", new V3(13, 1.8, 0), new V3(1.2, 3.6, 4), 3));
            for (int i = 0; i < 4; i++)
                court.Add(new ArenaProp("Banner" + i, "Cube", new V3(0.5 + i * 2.3, 2.4, 7.1), new V3(0.8, 1.6, 0.05), 4));
            list.Add(new ArenaVariant(Courtyard, "arena.courtyard", new V3(0.55, 0.68, 0.82),
                new[] { new V3(0.78, 0.68, 0.5), new V3(0.85, 0.8, 0.7), new V3(0.66, 0.5, 0.38), new V3(0.45, 0.33, 0.25), new V3(0.62, 0.18, 0.2) }, court));

            // Riverside ghat: stone steps down to water behind the fighters, lamp posts at the sides.
            var ghat = new List<ArenaProp> { new ArenaProp("Ground", "Plane", new V3(4, 0, 0), new V3(2.4, 1, 2.4), 0) };
            for (int i = 0; i < 4; i++)
                ghat.Add(new ArenaProp("Step" + i, "Cube", new V3(4, -0.15 - i * 0.3, 4.2 + i * 0.8), new V3(20, 0.3, 0.8), 1));
            ghat.Add(new ArenaProp("Water", "Plane", new V3(4, -1.3, 12), new V3(3, 1, 0.9), 2));
            for (int i = 0; i < 2; i++)
            {
                double x = i == 0 ? -4.5 : 12.5;
                ghat.Add(new ArenaProp("LampPost" + i, "Cylinder", new V3(x, 1.5, 1), new V3(0.25, 1.5, 0.25), 3));
                ghat.Add(new ArenaProp("Lamp" + i, "Sphere", new V3(x, 3.2, 1), new V3(0.5, 0.5, 0.5), 4));
            }
            ghat.Add(new ArenaProp("Shrine", "Cube", new V3(4, 1.2, 9), new V3(3, 2.4, 2), 3));
            list.Add(new ArenaVariant(Riverside, "arena.riverside", new V3(0.95, 0.75, 0.55),
                new[] { new V3(0.6, 0.6, 0.58), new V3(0.7, 0.66, 0.6), new V3(0.25, 0.42, 0.55), new V3(0.4, 0.36, 0.33), new V3(1, 0.85, 0.45) }, ghat));
            return list;
        }
    }

    /// <summary>
    /// Camera shake and framing (ticket 36). The side camera never moves during selection, so touch
    /// controls stay stable; impacts add a short decaying shake that is zero when reduced motion /
    /// reduced camera shake is on (the hit is still shown by the impact glyph, damage number and
    /// HP bar).
    /// </summary>
    public sealed class CameraShake
    {
        public const double MaxOffsetMetres = 0.08;
        public const double DecayPerSecond = 2.5;

        private double _strength;
        private double _time;

        public bool Reduced { get; set; }
        public double Strength => _strength;

        public void Kick(double strength = 1.0)
        {
            if (Reduced) return;
            _strength = Math.Min(1.0, Math.Max(_strength, strength));
        }

        /// <summary>Advances and returns the camera offset (zero when reduced or settled).</summary>
        public V3 Update(double dt)
        {
            _time += Math.Max(0, dt);
            if (Reduced)
            {
                _strength = 0;
                return V3.Zero;
            }
            _strength = Math.Max(0, _strength - Math.Max(0, dt) * DecayPerSecond);
            if (_strength <= 0) return V3.Zero;
            double a = _strength * MaxOffsetMetres;
            return new V3(a * Math.Sin(_time * 41.0), a * Math.Cos(_time * 53.0), 0);
        }

        public void Stop() => _strength = 0;
    }
}
