using AstraKingdoms.Client.UI;
using UnityEngine;
using UnityEngine.UI;

namespace AstraKingdoms.Client.Arena
{
    /// <summary>
    /// Floating combat captions (tickets 33-34): a glyph plus localized text at the world position of
    /// a cue ("✸ 30.00", "✕ Cancelled", "◎ Blocked", "○ Miss", "← Dodge"), so cancelled and
    /// surviving arrows, damage, dodges and cover stay understandable with sound off and without
    /// relying on colour. Labels are pooled on their own overlay canvas below the UI.
    /// </summary>
    public sealed class CombatFeedbackView : MonoBehaviour
    {
        public const int PoolSize = 10;
        public const float LifeSeconds = 0.9f;

        private readonly Text[] _labels = new Text[PoolSize];
        private readonly float[] _life = new float[PoolSize];
        private readonly Vector3[] _world = new Vector3[PoolSize];
        private Camera _camera;
        private int _next;

        public bool ReducedMotion { get; set; }
        public float TextScale { get; set; } = 1f;

        public void Init(Camera camera, Font font)
        {
            _camera = camera;
            var canvasGo = new GameObject("CombatFeedback", typeof(RectTransform));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5; // under the game UI (10)
            for (int i = 0; i < PoolSize; i++)
            {
                RectTransform rt = UiFactory.Rect("Label" + i, canvasGo.transform);
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.zero;
                rt.pivot = new Vector2(0.5f, 0f);
                rt.sizeDelta = new Vector2(420, 90);
                Text t = rt.gameObject.AddComponent<Text>();
                t.font = font;
                t.alignment = TextAnchor.LowerCenter;
                t.raycastTarget = false;
                t.supportRichText = false;
                t.horizontalOverflow = HorizontalWrapMode.Overflow;
                t.verticalOverflow = VerticalWrapMode.Overflow;
                _labels[i] = t;
                rt.gameObject.SetActive(false);
            }
        }

        /// <summary>Shows a caption at a world position (the oldest label is reused when all are busy).</summary>
        public void Show(Vector3 world, string text, Color color, bool large = false)
        {
            int i = _next;
            _next = (_next + 1) % PoolSize;
            Text t = _labels[i];
            t.text = text;
            t.color = color;
            t.fontSize = Mathf.RoundToInt((large ? 44 : 32) * TextScale * Mathf.Max(0.5f, Screen.height / 1080f));
            _world[i] = world;
            _life[i] = LifeSeconds;
            t.gameObject.SetActive(true);
            Place(i, 0f);
        }

        public void HideAll()
        {
            for (int i = 0; i < PoolSize; i++)
            {
                _life[i] = 0;
                _labels[i].gameObject.SetActive(false);
            }
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            for (int i = 0; i < PoolSize; i++)
            {
                if (_life[i] <= 0f) continue;
                _life[i] -= dt;
                if (_life[i] <= 0f)
                {
                    _labels[i].gameObject.SetActive(false);
                    continue;
                }
                Place(i, 1f - _life[i] / LifeSeconds);
            }
        }

        private void Place(int i, float progress)
        {
            if (_camera == null) return;
            Vector3 screen = _camera.WorldToScreenPoint(_world[i]);
            float rise = ReducedMotion ? 0f : progress * 60f;
            _labels[i].rectTransform.anchoredPosition = new Vector2(screen.x, screen.y + 30f + rise);
        }
    }
}
