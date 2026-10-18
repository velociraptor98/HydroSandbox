using UnityEngine;
using UnityEngine.UI;

namespace HydroSandbox
{
    /// <summary>World-space UI panels for labels and readouts that sit in the scene.</summary>
    public static class WorldText
    {
        /// <summary>
        /// A world-space canvas <paramref name="sizeMeters"/> big, laid out in pixels at
        /// <paramref name="pixelsPerMeter"/>; its rect's origin is the top left.
        /// </summary>
        public static RectTransform CreateCanvas(Transform parent, string name, Vector3 localPosition, Quaternion localRotation, Vector2 sizeMeters, float pixelsPerMeter)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = 2f;
            var rect = (RectTransform)go.transform;
            rect.sizeDelta = sizeMeters * pixelsPerMeter;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.localPosition = localPosition;
            rect.localRotation = localRotation;
            rect.localScale = Vector3.one / pixelsPerMeter;
            return rect;
        }

        public static Text AddText(RectTransform canvas, string name, string text, Rect pixels, int fontSize, Color color, TextAnchor anchor)
        {
            var label = Add<Text>(canvas, name, pixels);
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.text = text;
            label.fontSize = fontSize;
            label.color = color;
            label.alignment = anchor;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.raycastTarget = false;
            return label;
        }

        public static Image AddPanel(RectTransform canvas, string name, Rect pixels, Color color)
        {
            var image = Add<Image>(canvas, name, pixels);
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        /// <summary>A UI element at <paramref name="pixels"/>, measured from the canvas's top left.</summary>
        public static T Add<T>(RectTransform canvas, string name, Rect pixels) where T : Graphic
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(canvas, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(pixels.x, -pixels.y);
            rect.sizeDelta = pixels.size;
            return go.AddComponent<T>();
        }
    }
}
