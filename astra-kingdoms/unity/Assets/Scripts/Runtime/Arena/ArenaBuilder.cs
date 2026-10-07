using AstraKingdoms.Rules.Core;
using UnityEngine;

namespace AstraKingdoms.Client.Arena
{
    /// <summary>
    /// Creates the grey-box arena from primitives. Used by the editor scene builders (tickets 4 and
    /// 36) and as a runtime fallback, so the duel always has its two capsule fighters at x = 0 and
    /// x = 8 m. Colliders are removed: rendering never decides contacts. The arena treatment
    /// (courtyard or riverside, <see cref="Presentation.ArenaVariants"/>) supplies the ground and props.
    /// </summary>
    public static class ArenaBuilder
    {
        public static GameObject CreateArena() => CreateArena(Presentation.ArenaVariants.Courtyard);

        public static GameObject CreateArena(string variantId)
        {
            var root = new GameObject(ArenaLayout.ArenaRootName);
            ArenaVariantBuilder.Build(variantId, root.transform);
            CreateFighter(PlayerSide.A, root.transform);
            CreateFighter(PlayerSide.B, root.transform);
            CreateLight(root.transform);
            CreateCamera(null);
            return root;
        }

        public static GameObject CreateGround(Transform parent)
        {
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            RemoveCollider(ground);
            ground.transform.SetParent(parent, false);
            ground.transform.position = ArenaLayout.GroundPosition;
            ground.transform.localScale = ArenaLayout.GroundScale;
            Tint(ground, new Color(0.42f, 0.45f, 0.38f));
            return ground;
        }

        public static GameObject CreateFighter(PlayerSide side, Transform parent)
        {
            GameObject fighter = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            fighter.name = side == PlayerSide.A ? ArenaLayout.FighterAName : ArenaLayout.FighterBName;
            RemoveCollider(fighter);
            fighter.transform.SetParent(parent, false);
            fighter.transform.position = ArenaLayout.FighterPosition(side);
            fighter.transform.localScale = ArenaLayout.CapsuleScale;
            Tint(fighter, side == PlayerSide.A ? UI.UiTheme.PlayerA : UI.UiTheme.PlayerB);

            // A small "bow" block on the facing side so direction reads without colour.
            GameObject bow = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bow.name = "Bow";
            RemoveCollider(bow);
            bow.transform.SetParent(fighter.transform, false);
            float facing = side == PlayerSide.A ? 1f : -1f;
            bow.transform.localPosition = new Vector3(facing * 0.9f, 0.3f, 0f);
            bow.transform.localScale = new Vector3(0.3f, 0.6f, 0.3f);
            Tint(bow, new Color(0.3f, 0.2f, 0.12f));
            return fighter;
        }

        public static GameObject CreateLight(Transform parent)
        {
            var go = new GameObject("Directional Light");
            go.transform.SetParent(parent, false);
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.shadows = LightShadows.None; // plan: no automatic realtime shadow-casting lights
            go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            return go;
        }

        public static GameObject CreateCamera(Transform parent)
        {
            var go = new GameObject("Main Camera");
            go.tag = "MainCamera";
            go.transform.SetParent(parent, false);
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = ArenaLayout.CameraFieldOfView;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.55f, 0.68f, 0.82f);
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 100f;
            go.transform.position = ArenaLayout.CameraPosition;
            go.transform.LookAt(ArenaLayout.CameraTarget);
            go.AddComponent<AudioListener>();
            return go;
        }

        private static void RemoveCollider(GameObject go)
        {
            var c = go.GetComponent<Collider>();
            if (c == null) return;
            if (Application.isPlaying) Object.Destroy(c);
            else Object.DestroyImmediate(c);
        }

        private static void Tint(GameObject go, Color color)
        {
            var r = go.GetComponent<Renderer>();
            if (r == null) return;
            if (Application.isPlaying)
            {
                r.material.color = color;
                return;
            }
            // In the editor, give each grey-box object its own material instance saved into the scene.
            Material shared = r.sharedMaterial;
            if (shared == null) return;
            var m = new Material(shared) { color = color, name = go.name + " Material" };
            r.sharedMaterial = m;
        }
    }
}
