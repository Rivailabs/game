using AstraKingdoms.Client.Presentation;
using AstraKingdoms.Client.UI;
using AstraKingdoms.Rules.Core;
using UnityEngine;

namespace AstraKingdoms.Client.Arena
{
    /// <summary>
    /// Pooled weapon/element effects (ticket 32). Slots come from the engine-independent
    /// <see cref="EffectBudgetPool"/> (at most 12 active emitters, 64 particles each); every slot owns
    /// a preallocated set of camera-facing quads, so playing a volley allocates nothing. Particles use
    /// the element's glyph shape and motion pattern (<see cref="ElementEffectStyle"/>); colour is only a
    /// third cue. Trails follow their projectile; bursts expand along the style's spokes and fade.
    /// </summary>
    public sealed class EffectPoolView : MonoBehaviour
    {
        /// <summary>Quads rendered per slot (a slot may request up to 64; the rest are not drawn).</summary>
        public const int QuadsPerSlot = 24;

        private EffectBudgetPool _pool;
        private Transform[][] _quads;
        private Renderer[][] _renderers;
        private Vector3[] _centre;
        private float[] _life;
        private Transform[] _follow;
        private Vector3[][] _history;
        private int[] _historyHead;
        private int[] _historyCount;
        private Camera _camera;
        private float _clock;

        public EffectBudgetPool Pool => _pool;
        public bool ReducedMotion { get; set; }

        public void Init(Camera camera)
        {
            _camera = camera;
            _pool = new EffectBudgetPool();
            _pool.SlotStolen += slot => Hide(slot.Index);
            int n = _pool.Capacity;
            _quads = new Transform[n][];
            _renderers = new Renderer[n][];
            _centre = new Vector3[n];
            _life = new float[n];
            _follow = new Transform[n];
            _history = new Vector3[n][];
            _historyHead = new int[n];
            _historyCount = new int[n];
            for (int s = 0; s < n; s++) _history[s] = new Vector3[QuadsPerSlot];
            Shader shader = Shader.Find("Sprites/Default");
            for (int s = 0; s < n; s++)
            {
                var slotGo = new GameObject("EffectSlot" + s);
                slotGo.transform.SetParent(transform, false);
                _quads[s] = new Transform[QuadsPerSlot];
                _renderers[s] = new Renderer[QuadsPerSlot];
                for (int q = 0; q < QuadsPerSlot; q++)
                {
                    GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    quad.name = "P" + q;
                    Destroy(quad.GetComponent<Collider>());
                    quad.transform.SetParent(slotGo.transform, false);
                    Renderer r = quad.GetComponent<Renderer>();
                    if (shader != null) r.material = new Material(shader);
                    _quads[s][q] = quad.transform;
                    _renderers[s][q] = r;
                    quad.SetActive(false);
                }
            }
        }

        /// <summary>Starts an effect at a world position (or following a transform); returns false when the budget refused it.</summary>
        public bool Spawn(EffectKind kind, Element element, Vector3 position, Transform follow = null, float lifeSeconds = 0.6f)
        {
            EffectSlot slot = _pool.Acquire(kind, element, _clock);
            if (slot == null) return false;
            int s = slot.Index;
            _centre[s] = position;
            _follow[s] = follow;
            _historyHead[s] = 0;
            _historyCount[s] = 0;
            _life[s] = kind == EffectKind.ProjectileTrail && follow != null ? float.MaxValue : lifeSeconds;
            Texture icon = ElementIcons.Get(element);
            Color tint = ElementIcons.Tint(element);
            int visible = Mathf.Min(slot.Particles, QuadsPerSlot);
            for (int q = 0; q < QuadsPerSlot; q++)
            {
                bool on = q < visible;
                _quads[s][q].gameObject.SetActive(on);
                if (!on) continue;
                Material m = _renderers[s][q].material;
                m.mainTexture = icon;
                m.color = tint;
                float size = kind == EffectKind.ProjectileTrail ? 0.12f : 0.18f;
                _quads[s][q].localScale = new Vector3(size, size, size);
            }
            return true;
        }

        /// <summary>Stops every trail following <paramref name="target"/>.</summary>
        public void StopFollowing(Transform target)
        {
            foreach (EffectSlot slot in _pool.Slots)
                if (slot.Active && _follow[slot.Index] == target) Release(slot);
        }

        public void ReleaseAll()
        {
            foreach (EffectSlot slot in _pool.Slots)
                if (slot.Active) Release(slot);
        }

        private void Release(EffectSlot slot)
        {
            Hide(slot.Index);
            _pool.Release(slot);
        }

        private void Hide(int s)
        {
            _follow[s] = null;
            foreach (Transform q in _quads[s]) q.gameObject.SetActive(false);
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            _clock += dt;
            Quaternion face = _camera != null ? _camera.transform.rotation : Quaternion.identity;
            foreach (EffectSlot slot in _pool.Slots)
            {
                if (!slot.Active) continue;
                int s = slot.Index;
                _life[s] -= dt;
                if (_life[s] <= 0f || (_follow[s] == null && slot.Kind == EffectKind.ProjectileTrail && _life[s] > 1000f))
                {
                    Release(slot);
                    continue;
                }
                ElementEffectStyle style = ElementEffectStyle.For(slot.Element);
                if (_follow[s] != null)
                {
                    _centre[s] = _follow[s].position;
                    // Record the path at the element's spacing (dense embers vs sparse stones read differently).
                    Vector3[] h = _history[s];
                    if (_historyCount[s] == 0 || Vector3.Distance(h[(_historyHead[s] + h.Length - 1) % h.Length], _centre[s]) >= style.TrailSpacing)
                    {
                        h[_historyHead[s]] = _centre[s];
                        _historyHead[s] = (_historyHead[s] + 1) % h.Length;
                        _historyCount[s] = Mathf.Min(_historyCount[s] + 1, h.Length);
                    }
                }
                double age = _clock - slot.StartedAt;
                int visible = Mathf.Min(slot.Particles, QuadsPerSlot);
                for (int q = 0; q < visible; q++)
                {
                    Vector3 p = _centre[s];
                    if (slot.Kind == EffectKind.ProjectileTrail)
                    {
                        // Glyph q sits on the q-th recorded path point behind the projectile, moving in the element's pattern.
                        Vector3[] h = _history[s];
                        if (q >= _historyCount[s])
                        {
                            _quads[s][q].gameObject.SetActive(false);
                            continue;
                        }
                        _quads[s][q].gameObject.SetActive(true);
                        p = h[(_historyHead[s] - 1 - q + h.Length * 2) % h.Length] + Vec.ToVector3(style.Offset(q, ReducedMotion ? 0 : age)) * 0.5f;
                    }
                    else
                    {
                        int spokes = Mathf.Max(1, style.ImpactSpokes == 0 ? visible : style.ImpactSpokes);
                        float angle = (q % spokes) * Mathf.PI * 2f / spokes;
                        float radius = (float)(age * 1.2) * (1f + q / spokes * 0.35f);
                        p += new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 0f) + Vec.ToVector3(style.Offset(q, ReducedMotion ? 0 : age)) * 0.3f;
                    }
                    _quads[s][q].position = p;
                    _quads[s][q].rotation = face;
                }
            }
        }
    }
}
