using AstraKingdoms.Client.Presentation;
using UnityEngine;

namespace AstraKingdoms.Client.Arena
{
    /// <summary>
    /// Builds one of the two grey-box arena treatments (ticket 36) from the engine-independent layout
    /// in <see cref="ArenaVariants"/>: primitives without colliders, one shared flat material per
    /// palette entry (so the opaque batch count stays at the palette size), no shadow-casting
    /// lights. Both treatments share the rules, fighter positions and camera, so the second arena
    /// adds presentation only.
    /// </summary>
    public static class ArenaVariantBuilder
    {
        public const string PropsRootName = "ArenaProps";

        public static GameObject Build(string variantId, Transform parent)
        {
            ArenaVariant v = ArenaVariants.Get(variantId);
            var root = new GameObject(PropsRootName + "_" + v.Id);
            root.transform.SetParent(parent, false);
            var materials = new Material[v.Palette.Count];
            foreach (ArenaProp p in v.Props)
            {
                GameObject go = GameObject.CreatePrimitive(Primitive(p.Primitive));
                go.name = p.Name;
                Collider c = go.GetComponent<Collider>();
                if (c != null)
                {
                    if (Application.isPlaying) Object.Destroy(c);
                    else Object.DestroyImmediate(c);
                }
                go.transform.SetParent(root.transform, false);
                go.transform.position = Vec.ToVector3(p.Position);
                go.transform.localScale = Vec.ToVector3(p.Scale);
                Renderer r = go.GetComponent<Renderer>();
                if (r == null) continue;
                if (materials[p.Material] == null && r.sharedMaterial != null)
                {
                    V3 rgb = v.Palette[p.Material];
                    materials[p.Material] = new Material(r.sharedMaterial)
                    {
                        color = new Color((float)rgb.X, (float)rgb.Y, (float)rgb.Z),
                        name = v.Id + " Palette " + p.Material,
                    };
                }
                if (materials[p.Material] != null) r.sharedMaterial = materials[p.Material];
            }
            return root;
        }

        public static Color Sky(string variantId)
        {
            V3 s = ArenaVariants.Get(variantId).Sky;
            return new Color((float)s.X, (float)s.Y, (float)s.Z);
        }

        private static PrimitiveType Primitive(string name)
        {
            switch (name)
            {
                case "Sphere": return PrimitiveType.Sphere;
                case "Capsule": return PrimitiveType.Capsule;
                case "Cylinder": return PrimitiveType.Cylinder;
                case "Plane": return PrimitiveType.Plane;
                case "Quad": return PrimitiveType.Quad;
                default: return PrimitiveType.Cube;
            }
        }
    }
}
