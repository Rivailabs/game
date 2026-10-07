using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace AstraKingdoms.Client.UI
{
    /// <summary>
    /// Builds uGUI hierarchies in code (no hand-written prefab YAML). Font sizes are multiplied by the
    /// player's text-scale setting; screens are rebuilt when it changes.
    /// <para>
    /// Text path note (ticket 48): every label is created by <see cref="Label"/>, which uses legacy
    /// <c>Text</c>. Legacy Text cannot shape Devanagari or Kannada (conjuncts, vowel signs), so before
    /// Hindi or Kannada ship this one method switches to TextMesh Pro with the Noto faces planned in
    /// the asset ledger and atlases built from <c>FontCoverage.AtlasCharacters</c>, verified on the
    /// reference phone.
    /// </para>
    /// </summary>
    public sealed class UiFactory
    {
        public const int SizeSmall = 26;
        public const int SizeBody = 32;
        public const int SizeLarge = 44;
        public const int SizeTitle = 60;

        public Font Font { get; }
        public float TextScale { get; }

        public UiFactory(Font font, float textScale)
        {
            Font = font;
            TextScale = Mathf.Clamp(textScale, 0.5f, 2f);
        }

        public static Font BuiltinFont() => Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        public int Scaled(int size) => Mathf.RoundToInt(size * TextScale);

        public static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            return rt;
        }

        public static void Stretch(RectTransform rt, float left = 0, float bottom = 0, float right = 0, float top = 0)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        /// <summary>Anchors to a normalized region of the parent (x0,y0)-(x1,y1), bottom-left origin.</summary>
        public static void Region(RectTransform rt, float x0, float y0, float x1, float y1)
        {
            rt.anchorMin = new Vector2(x0, y0);
            rt.anchorMax = new Vector2(x1, y1);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        public RectTransform Panel(Transform parent, string name, Color color, bool blocksInput = true)
        {
            RectTransform rt = Rect(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = blocksInput;
            return rt;
        }

        public Text Label(Transform parent, string text, int size, TextAnchor align, Color color, string name = "Label")
        {
            RectTransform rt = Rect(name, parent);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = Font;
            t.text = text;
            t.fontSize = Scaled(size);
            t.alignment = align;
            t.color = color;
            t.raycastTarget = false;
            t.supportRichText = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        public Button Button(Transform parent, string label, UnityAction onClick, Color color, int size = SizeBody, string name = "Button")
        {
            RectTransform rt = Rect(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            var b = rt.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            if (onClick != null) b.onClick.AddListener(onClick);
            Text t = Label(rt, label, size, TextAnchor.MiddleCenter, IsLight(color) ? UiTheme.TextDark : UiTheme.Text);
            Stretch(t.rectTransform, 12, 6, 12, 6);
            Prefer(rt.gameObject, -1, Scaled(size) + 40);
            return b;
        }

        public static Text ButtonLabel(Button b) => b.GetComponentInChildren<Text>();

        public static void SetButtonColor(Button b, Color color)
        {
            var img = b.GetComponent<Image>();
            if (img != null) img.color = color;
            Text t = ButtonLabel(b);
            if (t != null) t.color = IsLight(color) ? UiTheme.TextDark : UiTheme.Text;
        }

        public RectTransform Column(Transform parent, string name, float spacing, int padding)
        {
            RectTransform rt = Rect(name, parent);
            var g = rt.gameObject.AddComponent<VerticalLayoutGroup>();
            g.spacing = spacing;
            g.padding = new RectOffset(padding, padding, padding, padding);
            g.childAlignment = TextAnchor.UpperCenter;
            g.childControlWidth = true;
            g.childControlHeight = true;
            g.childForceExpandWidth = true;
            g.childForceExpandHeight = false;
            return rt;
        }

        public RectTransform Row(Transform parent, string name, float spacing, int padding = 0)
        {
            RectTransform rt = Rect(name, parent);
            var g = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
            g.spacing = spacing;
            g.padding = new RectOffset(padding, padding, padding, padding);
            g.childAlignment = TextAnchor.MiddleCenter;
            g.childControlWidth = true;
            g.childControlHeight = true;
            g.childForceExpandWidth = true;
            g.childForceExpandHeight = true;
            return rt;
        }

        public static LayoutElement Prefer(GameObject go, float width, float height)
        {
            var le = go.GetComponent<LayoutElement>();
            if (le == null) le = go.AddComponent<LayoutElement>();
            if (width >= 0) le.preferredWidth = width;
            if (height >= 0)
            {
                le.preferredHeight = height;
                le.minHeight = height;
            }
            return le;
        }

        public Slider Slider(Transform parent, float min, float max, bool whole, float value, UnityAction<float> onChanged)
        {
            RectTransform root = Rect("Slider", parent);
            Prefer(root.gameObject, -1, Scaled(SizeBody) + 30);
            RectTransform bg = Panel(root, "Background", UiTheme.Button);
            Stretch(bg, 0, 18, 0, 18);
            RectTransform fillArea = Rect("Fill Area", root);
            Stretch(fillArea, 10, 18, 10, 18);
            RectTransform fill = Panel(fillArea, "Fill", UiTheme.ButtonSelected, false);
            Stretch(fill);
            RectTransform handleArea = Rect("Handle Slide Area", root);
            Stretch(handleArea, 10, 0, 10, 0);
            RectTransform handle = Panel(handleArea, "Handle", UiTheme.Text);
            handle.sizeDelta = new Vector2(36, 0);
            handle.anchorMin = new Vector2(0, 0);
            handle.anchorMax = new Vector2(0, 1);

            var s = root.gameObject.AddComponent<Slider>();
            s.fillRect = fill;
            s.handleRect = handle;
            s.targetGraphic = handle.GetComponent<Image>();
            s.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            s.minValue = min;
            s.maxValue = max;
            s.wholeNumbers = whole;
            s.value = value;
            if (onChanged != null) s.onValueChanged.AddListener(onChanged);
            return s;
        }

        public RawImage Raw(Transform parent, string name, Texture texture)
        {
            RectTransform rt = Rect(name, parent);
            var r = rt.gameObject.AddComponent<RawImage>();
            r.texture = texture;
            r.raycastTarget = false;
            return r;
        }

        /// <summary>Spacer that absorbs remaining space in a layout group.</summary>
        public static void Flexible(Transform parent)
        {
            RectTransform rt = Rect("Spacer", parent);
            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.flexibleHeight = 1;
            le.flexibleWidth = 1;
        }

        public static bool IsLight(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b > 0.6f;

        public static string Seconds(double s) => Math.Ceiling(Math.Max(0, s)).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
    }
}
