using System.Collections.Generic;
using AstraKingdoms.Client.Presentation;
using AstraKingdoms.Rules.Core;
using UnityEngine;

namespace AstraKingdoms.Client.Arena
{
    /// <summary>
    /// Runtime archer rig contract (ticket 26). Any archer prefab — the placeholder built here or the
    /// approved art asset (ticket 65) — exposes the named attachment points of
    /// <see cref="ArcherAttachments"/>: <c>hand_l</c>, <c>hand_r</c>, <c>bow_grip</c>,
    /// <c>string_nock</c> and <c>arrow_spawn</c>. Character, bow, arrow and the procedural
    /// bowstring stay separate objects joined at these points (plan: "Asset contract").
    /// The placeholder also names its joints (Hips, Spine, BowArm, DrawArm, LegL, LegR) so
    /// <see cref="ArcherPresenter"/> can pose it procedurally before production clips exist.
    /// </summary>
    public sealed class ArcherRig : MonoBehaviour
    {
        public const string PlaceholderName = "ArcherPlaceholder";

        public Transform HandLeft;
        public Transform HandRight;
        public Transform BowGrip;
        public Transform StringNock;
        public Transform ArrowSpawn;

        // Placeholder joints (null on production rigs, which are posed by their Animator).
        public Transform Hips;
        public Transform Spine;
        public Transform BowArm;
        public Transform DrawArm;
        public Transform LegLeft;
        public Transform LegRight;
        public bool Placeholder;

        private readonly List<Renderer> _bodyRenderers = new List<Renderer>();

        /// <summary>Resolves attachment points by name; returns the names that are missing (empty = contract met).</summary>
        public IReadOnlyList<string> Bind()
        {
            HandLeft = FindDeep(transform, ArcherAttachments.HandLeft);
            HandRight = FindDeep(transform, ArcherAttachments.HandRight);
            BowGrip = FindDeep(transform, ArcherAttachments.BowGrip);
            StringNock = FindDeep(transform, ArcherAttachments.StringNock);
            ArrowSpawn = FindDeep(transform, ArcherAttachments.ArrowSpawn);
            Hips = FindDeep(transform, "Hips");
            Spine = FindDeep(transform, "Spine");
            BowArm = FindDeep(transform, "BowArm");
            DrawArm = FindDeep(transform, "DrawArm");
            LegLeft = FindDeep(transform, "LegL");
            LegRight = FindDeep(transform, "LegR");
            var names = new List<string>();
            CollectNames(transform, names);
            return ArcherAttachments.Missing(names);
        }

        public void RegisterBodyRenderer(Renderer r)
        {
            if (r != null) _bodyRenderers.Add(r);
        }

        /// <summary>Tints the body (not the bow): the flat pilot palette, or the hit flash.</summary>
        public void SetTint(Color color)
        {
            foreach (Renderer r in _bodyRenderers)
                if (r != null) r.material.color = color;
        }

        public static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindDeep(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        private static void CollectNames(Transform t, List<string> names)
        {
            names.Add(t.name);
            for (int i = 0; i < t.childCount; i++) CollectNames(t.GetChild(i), names);
        }
    }

    /// <summary>
    /// Builds the placeholder archer from primitives (ledger: model.archer-placeholder). Proportions
    /// match the rules' body test (feet at 0, head top near 1.7 m); every primitive has its collider
    /// removed because rendering never decides contacts.
    /// </summary>
    public static class ArcherRigBuilder
    {
        public static ArcherRig CreatePlaceholder(PlayerSide side, Transform parent, Color tint)
        {
            var root = new GameObject(ArcherRig.PlaceholderName + side);
            root.transform.SetParent(parent, false);
            root.transform.rotation = Quaternion.Euler(0f, side == PlayerSide.A ? 90f : -90f, 0f);
            var rig = root.AddComponent<ArcherRig>();
            rig.Placeholder = true;

            Transform hips = Node("Hips", root.transform, new Vector3(0f, 0.9f, 0f));
            Transform spine = Node("Spine", hips, Vector3.zero);
            Part(rig, PrimitiveType.Cube, "Torso", spine, new Vector3(0f, 0.3f, 0f), new Vector3(0.36f, 0.56f, 0.22f), tint, true);
            Part(rig, PrimitiveType.Sphere, "Head", spine, new Vector3(0f, 0.72f, 0f), new Vector3(0.24f, 0.26f, 0.24f), tint, true);

            // Bow arm (left): pivot at the shoulder; the arm points along local +z (forward).
            Transform bowArm = Node("BowArm", spine, new Vector3(-0.22f, 0.52f, 0f));
            Part(rig, PrimitiveType.Cube, "BowArmMesh", bowArm, new Vector3(0f, 0f, 0.3f), new Vector3(0.08f, 0.08f, 0.6f), tint, true);
            Transform handL = Node(ArcherAttachments.HandLeft, bowArm, new Vector3(0f, 0f, 0.62f));
            Transform grip = Node(ArcherAttachments.BowGrip, handL, Vector3.zero);
            Node(ArcherAttachments.ArrowSpawn, grip, Vector3.zero);
            var bowColor = new Color(0.35f, 0.22f, 0.12f);
            var bowGeometry = new BowGeometry();
            float half = (float)bowGeometry.HalfLength, setback = (float)bowGeometry.TipSetback;
            Part(rig, PrimitiveType.Cube, "BowUpperLimb", grip, new Vector3(0f, half / 2f, -setback / 2f), new Vector3(0.04f, half, 0.04f), bowColor, false);
            Part(rig, PrimitiveType.Cube, "BowLowerLimb", grip, new Vector3(0f, -half / 2f, -setback / 2f), new Vector3(0.04f, half, 0.04f), bowColor, false);

            // Draw arm (right): the hand is moved onto the string nock every frame.
            Transform drawArm = Node("DrawArm", spine, new Vector3(0.22f, 0.52f, 0f));
            Part(rig, PrimitiveType.Cube, "DrawArmMesh", drawArm, new Vector3(0f, 0f, 0.15f), new Vector3(0.08f, 0.08f, 0.3f), tint, true);
            Node(ArcherAttachments.HandRight, root.transform, Vector3.zero);
            Node(ArcherAttachments.StringNock, root.transform, Vector3.zero);

            Transform legL = Node("LegL", hips, new Vector3(-0.1f, 0f, 0f));
            Part(rig, PrimitiveType.Cube, "LegLMesh", legL, new Vector3(0f, -0.45f, 0f), new Vector3(0.13f, 0.88f, 0.13f), Darker(tint), true);
            Transform legR = Node("LegR", hips, new Vector3(0.1f, 0f, 0f));
            Part(rig, PrimitiveType.Cube, "LegRMesh", legR, new Vector3(0f, -0.45f, 0f), new Vector3(0.13f, 0.88f, 0.13f), Darker(tint), true);

            rig.Bind();
            return rig;
        }

        private static Color Darker(Color c) => new Color(c.r * 0.8f, c.g * 0.8f, c.b * 0.8f, c.a);

        private static Transform Node(string name, Transform parent, Vector3 localPosition)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            return go.transform;
        }

        private static void Part(ArcherRig rig, PrimitiveType type, string name, Transform parent, Vector3 localPosition, Vector3 scale, Color color, bool body)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            var c = go.GetComponent<Collider>();
            if (c != null)
            {
                if (Application.isPlaying) Object.Destroy(c);
                else Object.DestroyImmediate(c);
            }
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = scale;
            var r = go.GetComponent<Renderer>();
            if (r != null && Application.isPlaying) r.material.color = color;
            if (body) rig.RegisterBodyRenderer(r);
        }
    }
}
