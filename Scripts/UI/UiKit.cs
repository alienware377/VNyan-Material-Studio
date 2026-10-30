using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace MaterialStudio
{
    /// <summary>
    /// Runtime-built uGUI primitives. Everything is positioned top-left down (y grows
    /// downward) inside its parent, which keeps layout code readable. Colors follow the
    /// other studio plugins' dark theme.
    /// </summary>
    public static class Ui
    {
        public static Font font;
        public static Action<string> OnError;   // the plugin shows these in its status line
        public static readonly Color Bg = new Color(0.13f, 0.13f, 0.15f, 0.97f);
        public static readonly Color Panel = new Color(0.17f, 0.17f, 0.20f, 1f);
        public static readonly Color PanelDark = new Color(0.09f, 0.09f, 0.11f, 1f);
        public static readonly Color Btn = new Color(0.26f, 0.27f, 0.32f, 1f);
        public static readonly Color BtnHi = new Color(0.34f, 0.36f, 0.44f, 1f);
        public static readonly Color BtnPress = new Color(0.20f, 0.42f, 0.70f, 1f);
        public static readonly Color Accent = new Color(0.30f, 0.62f, 0.95f, 1f);
        public static readonly Color Ink = new Color(0.88f, 0.90f, 0.95f, 1f);
        public static readonly Color TextDim = new Color(0.62f, 0.65f, 0.72f, 1f);
        public static readonly Color Warn = new Color(1f, 0.78f, 0.35f, 1f);
        public static readonly Color Good = new Color(0.45f, 0.85f, 0.45f, 1f);
        public static readonly Color Field = new Color(0.93f, 0.94f, 0.96f, 1f);
        public static readonly Color FieldText = new Color(0.05f, 0.05f, 0.07f, 1f);
        public static readonly Color Selected = new Color(0.22f, 0.36f, 0.58f, 1f);
        public static readonly Color HelpChip = new Color(0.22f, 0.30f, 0.55f, 0.95f);

        public static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent != null ? parent.gameObject.layer : 5;
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            return rt;
        }

        public static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
        }

        /// <summary>Stretch to fill the parent with insets (l, t, r, b).</summary>
        public static void Fill(RectTransform rt, float l, float t, float r, float b)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0, 1);
            rt.offsetMin = new Vector2(l, b);
            rt.offsetMax = new Vector2(-r, -t);
        }

        public static Image Box(string name, Transform parent, float x, float y, float w, float h, Color c)
        {
            RectTransform rt = Rect(name, parent);
            Place(rt, x, y, w, h);
            rt.gameObject.AddComponent<CanvasRenderer>();
            Image img = rt.gameObject.AddComponent<Image>();
            img.color = c;
            return img;
        }

        public static Text Label(string name, Transform parent, string text, float x, float y, float w, float h,
                                 int size = 12, TextAnchor anchor = TextAnchor.MiddleLeft, FontStyle style = FontStyle.Normal)
        {
            RectTransform rt = Rect(name, parent);
            Place(rt, x, y, w, h);
            rt.gameObject.AddComponent<CanvasRenderer>();
            Text t = rt.gameObject.AddComponent<Text>();
            t.font = font;
            t.fontSize = size;
            t.fontStyle = style;
            t.alignment = anchor;
            t.color = Ink;
            t.text = text;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Truncate;
            t.raycastTarget = false;
            return t;
        }

        public static Button Button(string name, Transform parent, string text, float x, float y, float w, float h,
                                    UnityAction onClick, int size = 12)
        {
            Image img = Box(name, parent, x, y, w, h, Btn);
            Button b = img.gameObject.AddComponent<Button>();
            ColorBlock cb = b.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1.25f, 1.25f, 1.3f, 1f);
            cb.pressedColor = new Color(0.75f, 0.95f, 1.4f, 1f);
            cb.selectedColor = Color.white;
            cb.colorMultiplier = 1.2f;
            b.colors = cb;
            b.targetGraphic = img;
            Text t = Label("Text", img.transform, text, 2, 0, w - 4, h, size, TextAnchor.MiddleCenter);
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            if (onClick != null)
            {
                // a throwing handler used to look like a dead button: say what went wrong instead
                b.onClick.AddListener(delegate
                {
                    try { onClick(); }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[MaterialStudio] '" + text + "' failed: " + e);
                        if (OnError != null) OnError("'" + text + "' did not work: " + e.Message);
                    }
                });
            }
            return b;
        }

        public static void SetButtonText(Button b, string s)
        {
            if (b == null) return;
            Text t = b.GetComponentInChildren<Text>(true);
            if (t != null) t.text = s;
        }

        /// <summary>Small green-ticked checkbox with a label to its right.</summary>
        public static Toggle Check(string name, Transform parent, string text, float x, float y, float w, float h,
                                   bool on, UnityAction<bool> onChange)
        {
            RectTransform rt = Rect(name, parent);
            Place(rt, x, y, w, h);
            Toggle tg = rt.gameObject.AddComponent<Toggle>();
            float bs = Mathf.Min(14f, h - 2f);
            Image box = Box("Box", rt, 0, (h - bs) * 0.5f, bs, bs, Field);
            Image tick = Box("Tick", box.transform, 3, 3, bs - 6, bs - 6, Good);
            tick.raycastTarget = false;
            tg.targetGraphic = box;
            tg.graphic = tick;
            tg.isOn = on;
            Text lab = Label("Label", rt, text, bs + 5, 0, w - bs - 5, h, 12);
            lab.raycastTarget = true;     // clicking the words toggles too
            // the label must be a raycast target under the toggle's own GameObject: a
            // transparent hit image on the root covers both
            Image hit = rt.gameObject.AddComponent<Image>();
            hit.color = new Color(0, 0, 0, 0.001f);
            if (onChange != null) tg.onValueChanged.AddListener(onChange);
            return tg;
        }

        public static InputField Input(string name, Transform parent, string text, float x, float y, float w, float h,
                                       InputField.ContentType type, UnityAction<string> onSubmit, int size = 11)
        {
            Image bg = Box(name, parent, x, y, w, h, Field);
            InputField f = bg.gameObject.AddComponent<InputField>();
            Text t = Label("Text", bg.transform, "", 4, 0, w - 8, h, size,
                           type == InputField.ContentType.DecimalNumber || type == InputField.ContentType.IntegerNumber
                               ? TextAnchor.MiddleRight : TextAnchor.MiddleLeft);
            t.color = FieldText;
            t.supportRichText = false;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            Text ph = Label("Placeholder", bg.transform, "", 4, 0, w - 8, h, size, t.alignment, FontStyle.Italic);
            ph.color = new Color(0.4f, 0.4f, 0.45f, 1f);
            f.textComponent = t;
            f.placeholder = ph;
            f.contentType = type;
            f.targetGraphic = bg;
            f.text = text ?? "";
            f.lineType = InputField.LineType.SingleLine;
            // Commit on Enter only: onEndEdit also fires on blur, and a panel rebuilt on blur
            // swallows the click on whatever was pressed (studio convention).
            if (onSubmit != null)
            {
                f.onEndEdit.AddListener(delegate (string s)
                {
                    if (UnityEngine.Input.GetKey(KeyCode.Return) || UnityEngine.Input.GetKey(KeyCode.KeypadEnter)) onSubmit(s);
                });
            }
            return f;
        }

        public static Slider Slider(string name, Transform parent, float x, float y, float w, float h,
                                    float min, float max, float value, bool whole, UnityAction<float> onChange)
        {
            RectTransform rt = Rect(name, parent);
            Place(rt, x, y, w, h);
            Slider s = rt.gameObject.AddComponent<Slider>();
            Image track = Box("Track", rt, 0, h * 0.5f - 2f, w, 4f, PanelDark);
            track.raycastTarget = true;
            RectTransform fillArea = Rect("Fill Area", rt);
            Fill(fillArea, 0, h * 0.5f - 2f, 0, h * 0.5f - 2f);
            Image fill = Box("Fill", fillArea, 0, 0, 0, 4f, Accent);
            Fill((RectTransform)fill.transform, 0, 0, 0, 0);
            fill.raycastTarget = false;
            RectTransform handleArea = Rect("Handle Slide Area", rt);
            Fill(handleArea, 5, 0, 5, 0);
            Image handle = Box("Handle", handleArea, 0, 0, 10f, h, Field);
            RectTransform hrt = (RectTransform)handle.transform;
            hrt.anchorMin = new Vector2(0, 0);
            hrt.anchorMax = new Vector2(0, 1);
            hrt.pivot = new Vector2(0.5f, 0.5f);
            hrt.sizeDelta = new Vector2(10f, 0f);
            hrt.anchoredPosition = Vector2.zero;
            s.fillRect = (RectTransform)fill.transform;
            s.handleRect = hrt;
            s.targetGraphic = handle;
            s.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            s.minValue = Mathf.Min(min, max);
            s.maxValue = Mathf.Max(min, max);
            s.wholeNumbers = whole;
            s.SetValueWithoutNotify(value);
            if (onChange != null) s.onValueChanged.AddListener(onChange);
            return s;
        }

        /// <summary>A vertical scroll view; add rows to the returned content.</summary>
        public static RectTransform Scroll(string name, Transform parent, float x, float y, float w, float h,
                                           out ScrollRect scroll)
        {
            Image vp = Box(name, parent, x, y, w, h, PanelDark);
            vp.gameObject.AddComponent<RectMask2D>();
            RectTransform content = Rect("Content", vp.transform);
            content.anchorMin = new Vector2(0, 1);
            content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0, 1);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0, 10);
            scroll = vp.gameObject.AddComponent<ScrollRect>();
            scroll.content = content;
            scroll.viewport = (RectTransform)vp.transform;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24f;
            scroll.inertia = false;
            return content;
        }

        public static void SetContentHeight(RectTransform content, float h)
        {
            content.sizeDelta = new Vector2(content.sizeDelta.x, Mathf.Max(10f, h));
        }

        public static void Clear(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--)
            {
                GameObject c = t.GetChild(i).gameObject;
                c.SetActive(false);
                UnityEngine.Object.Destroy(c);
            }
        }

        public static string F(float v)
        {
            if (Mathf.Abs(v) >= 1000f) return v.ToString("0.#", CultureInfo.InvariantCulture);
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        public static bool TryF(string s, out float v)
        {
            return float.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
    }

    /// <summary>
    /// "?" chips and the speech-bubble they open. Same behaviour as the other studio
    /// plugins: same chip toggles, bubble closes 5 s after the cursor leaves it.
    /// </summary>
    public class Tooltips
    {
        readonly RectTransform windowRoot;
        GameObject bubble;
        Text bubbleText;
        string owner;
        float leaveAt = -1f;

        public Tooltips(RectTransform root) { windowRoot = root; }

        public void Chip(RectTransform control, string key, string text, bool sliderStyle)
        {
            if (control == null || string.IsNullOrEmpty(text)) return;
            Button b = Ui.Button("Help_" + key, control, "?", 0, 0, 13, 13, null, 10);
            b.GetComponent<Image>().color = Ui.HelpChip;
            Text t = b.GetComponentInChildren<Text>();
            t.fontStyle = FontStyle.Bold;
            t.color = Color.white;
            RectTransform rt = (RectTransform)b.transform;
            if (sliderStyle)
            {
                rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1);
                rt.pivot = new Vector2(1, 1);
                rt.anchoredPosition = new Vector2(-5, -1);
            }
            else
            {
                rt.anchorMin = new Vector2(1, 1); rt.anchorMax = new Vector2(1, 1);
                rt.pivot = new Vector2(1, 1);
                rt.anchoredPosition = new Vector2(0, 0);
            }
            rt.SetAsLastSibling();
            // A chip in a text box's corner would sit on top of right-aligned numbers: narrow
            // the text area so the value always stays readable.
            InputField field = control.GetComponent<InputField>();
            if (field != null && !sliderStyle)
            {
                foreach (Graphic g in new Graphic[] { field.textComponent, field.placeholder })
                {
                    if (g == null) continue;
                    RectTransform grt = g.rectTransform;
                    grt.sizeDelta = new Vector2(Mathf.Max(10f, grt.sizeDelta.x - 15f), grt.sizeDelta.y);
                }
            }
            string k = key, tx = text;
            b.onClick.AddListener(delegate { Toggle(k, tx, rt); });
        }

        void Toggle(string key, string text, RectTransform near)
        {
            if (bubble != null && bubble.activeSelf && owner == key) { Hide(); return; }
            if (bubble == null) Build();
            owner = key;
            bubbleText.text = text;
            float h = Mathf.Clamp(bubbleText.preferredHeight + 30f, 40f, 400f);
            RectTransform brt = (RectTransform)bubble.transform;
            brt.sizeDelta = new Vector2(280f, h);
            // snap above the chip, clamped inside the window horizontally
            Vector3 world = near.TransformPoint(new Vector3(0, 0, 0));
            Vector3 local = windowRoot.InverseTransformPoint(world);
            float halfW = windowRoot.rect.width * 0.5f;
            float x = Mathf.Clamp(local.x - 140f, -halfW + 4f, halfW - 284f);
            float y = local.y + h + 6f;
            float top = windowRoot.rect.height * 0.5f - 4f;
            if (y > top) y = local.y - 18f;   // no room above: show below
            brt.localPosition = new Vector3(x, y, 0);
            bubble.SetActive(true);
            bubble.transform.SetAsLastSibling();
            leaveAt = -1f;
        }

        void Build()
        {
            Image bg = Ui.Box("TipBubble", windowRoot, 0, 0, 280, 80, new Color(0.06f, 0.07f, 0.10f, 0.98f));
            RectTransform rt = (RectTransform)bg.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0, 1);
            bubble = bg.gameObject;
            bubbleText = Ui.Label("Tip", bg.transform, "", 8, 6, 250, 380, 11, TextAnchor.UpperLeft);
            bubbleText.verticalOverflow = VerticalWrapMode.Overflow;
            Ui.Button("Close", bg.transform, "x", 258, 4, 18, 16, Hide, 11);
        }

        public void Hide()
        {
            if (bubble != null) bubble.SetActive(false);
            owner = null;
        }

        public void Tick()
        {
            if (bubble == null || !bubble.activeSelf) return;
            RectTransform rt = (RectTransform)bubble.transform;
            bool over = RectTransformUtility.RectangleContainsScreenPoint(rt, UnityEngine.Input.mousePosition, null);
            if (over) { leaveAt = -1f; return; }
            if (leaveAt < 0f) leaveAt = Time.unscaledTime + 5f;
            else if (Time.unscaledTime >= leaveAt) Hide();
        }
    }

    /// <summary>Raises a callback on pointer click (for plain Images used as swatches/rows).</summary>
    public class ClickCatcher : MonoBehaviour, IPointerClickHandler
    {
        public Action<PointerEventData> onClick;
        public void OnPointerClick(PointerEventData e) { if (onClick != null) onClick(e); }
    }
}
