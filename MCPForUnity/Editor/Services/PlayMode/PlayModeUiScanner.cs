using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Runtime.Helpers;
using UnityEngine;
using UnityEngine.UIElements;

namespace MCPForUnity.Editor.Services.PlayMode
{
    internal sealed class PlayModeUiItem
    {
        public string Framework { get; set; }
        public string Type { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public int? InstanceId { get; set; }
        public string Text { get; set; }
        public bool Interactable { get; set; }
        public object Value { get; set; }
        public float[] Rect { get; set; }
        public float[] Center { get; set; }

        public object ToSerializable()
        {
            return new
            {
                framework = Framework,
                type = Type,
                name = Name,
                path = Path,
                instanceID = InstanceId,
                text = Text,
                interactable = Interactable,
                value = Value,
                normalizedRect = Rect,
                center = Center,
                coordinateOrigin = "top_left",
            };
        }
    }

    internal static class PlayModeUiScanner
    {
        private static readonly string[] TextComponentNames =
        {
            "UnityEngine.UI.Text",
            "TMPro.TMP_Text",
        };

        internal static List<PlayModeUiItem> Scan(string framework = "all")
        {
            string normalized = (framework ?? "all").Trim().ToLowerInvariant();
            var items = new List<PlayModeUiItem>();
            if (normalized == "all" || normalized == "ugui")
                ScanUGui(items);
            if (normalized == "all" || normalized == "uitoolkit")
                ScanUiToolkit(items);

            return items
                .OrderBy(item => item.Framework, StringComparer.Ordinal)
                .ThenBy(item => item.Path, StringComparer.Ordinal)
                .ThenBy(item => item.Type, StringComparer.Ordinal)
                .ToList();
        }

        internal static string FindTextContaining(string expected)
        {
            if (string.IsNullOrEmpty(expected)) return null;
            return Scan().Select(item => item.Text).FirstOrDefault(text =>
                !string.IsNullOrEmpty(text)
                && text.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static void ScanUGui(List<PlayModeUiItem> output)
        {
            var byKey = new Dictionary<string, PlayModeUiItem>(StringComparer.Ordinal);
            foreach (string typeName in TextComponentNames)
            {
                Type type = UnityTypeResolver.ResolveComponent(typeName);
                if (type == null) continue;
                foreach (Component component in UnityFindObjectsCompat.FindAll(type).OfType<Component>())
                {
                    if (!IsVisible(component)) continue;
                    string text = ReadStringProperty(component, "text");
                    if (text == null) continue;
                    var item = BuildUGuiItem(component, text, false, null);
                    byKey[BuildKey(item)] = item;
                }
            }

            Type selectableType = UnityTypeResolver.ResolveComponent("UnityEngine.UI.Selectable");
            if (selectableType != null)
            {
                foreach (Component component in UnityFindObjectsCompat.FindAll(selectableType).OfType<Component>())
                {
                    if (!IsVisible(component)) continue;
                    bool interactable = ReadBoolProperty(component, "interactable", true);
                    string text = FindChildText(component.gameObject);
                    object value = ReadControlValue(component);
                    var item = BuildUGuiItem(component, text, interactable, value);
                    byKey[BuildKey(item)] = item;
                }
            }

            output.AddRange(byKey.Values);
        }

        private static void ScanUiToolkit(List<PlayModeUiItem> output)
        {
            foreach (UIDocument document in UnityFindObjectsCompat.FindAll<UIDocument>())
            {
                if (document == null || !document.isActiveAndEnabled) continue;
                VisualElement root = document.rootVisualElement;
                if (root == null || root.panel == null) continue;
                TraverseVisualTree(document, root, document.gameObject.name, output);
            }
        }

        private static void TraverseVisualTree(
            UIDocument document,
            VisualElement element,
            string parentPath,
            List<PlayModeUiItem> output)
        {
            string segment = string.IsNullOrEmpty(element.name)
                ? element.GetType().Name
                : element.name;
            string path = parentPath + "/" + segment;

            bool displayed;
            try
            {
                displayed = element.visible
                    && element.resolvedStyle.display != DisplayStyle.None
                    && element.resolvedStyle.visibility == Visibility.Visible;
            }
            catch
            {
                displayed = element.visible;
            }

            if (displayed)
            {
                string typeName = element.GetType().Name;
                string text = element is TextElement textElement ? textElement.text : ReadValueAsString(element);
                bool interactable = element.enabledInHierarchy && IsUiToolkitInteractable(typeName);
                object value = interactable ? ReadProperty(element, "value") : null;
                Rect worldBound = element.worldBound;
                float width = Math.Max(1f, Screen.width);
                float height = Math.Max(1f, Screen.height);
                float xMin = Mathf.Clamp01(worldBound.xMin / width);
                float yMin = Mathf.Clamp01(worldBound.yMin / height);
                float xMax = Mathf.Clamp01(worldBound.xMax / width);
                float yMax = Mathf.Clamp01(worldBound.yMax / height);

                if (!string.IsNullOrEmpty(text) || interactable)
                {
                    output.Add(new PlayModeUiItem
                    {
                        Framework = "uitoolkit",
                        Type = typeName,
                        Name = element.name,
                        Path = path,
                        InstanceId = document.gameObject.GetInstanceIDCompat(),
                        Text = text,
                        Interactable = interactable,
                        Value = value,
                        Rect = new[] { xMin, yMin, xMax, yMax },
                        Center = new[] { (xMin + xMax) * 0.5f, (yMin + yMax) * 0.5f },
                    });
                }
            }

            foreach (VisualElement child in element.Children())
                TraverseVisualTree(document, child, path, output);
        }

        private static PlayModeUiItem BuildUGuiItem(
            Component component,
            string text,
            bool interactable,
            object value)
        {
            GetNormalizedRect(component.transform as RectTransform, out float[] rect, out float[] center);
            return new PlayModeUiItem
            {
                Framework = "ugui",
                Type = component.GetType().Name,
                Name = component.gameObject.name,
                Path = GetTransformPath(component.transform),
                InstanceId = component.gameObject.GetInstanceIDCompat(),
                Text = text,
                Interactable = interactable,
                Value = value,
                Rect = rect,
                Center = center,
            };
        }

        private static void GetNormalizedRect(
            RectTransform rectTransform,
            out float[] rect,
            out float[] center)
        {
            if (rectTransform == null || Screen.width <= 0 || Screen.height <= 0)
            {
                rect = null;
                center = null;
                return;
            }

            var corners = new Vector3[4];
            rectTransform.GetWorldCorners(corners);
            Canvas canvas = rectTransform.GetComponentInParent<Canvas>();
            Camera camera = canvas != null && canvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null
                : canvas != null && canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
            var points = corners
                .Select(corner => RectTransformUtility.WorldToScreenPoint(camera, corner))
                .ToArray();
            float xMin = Mathf.Clamp01(points.Min(point => point.x) / Screen.width);
            float xMax = Mathf.Clamp01(points.Max(point => point.x) / Screen.width);
            float yMinBottom = Mathf.Clamp01(points.Min(point => point.y) / Screen.height);
            float yMaxBottom = Mathf.Clamp01(points.Max(point => point.y) / Screen.height);
            float yMin = 1f - yMaxBottom;
            float yMax = 1f - yMinBottom;
            rect = new[] { xMin, yMin, xMax, yMax };
            center = new[] { (xMin + xMax) * 0.5f, (yMin + yMax) * 0.5f };
        }

        private static bool IsVisible(Component component)
        {
            if (component == null || !component.gameObject.activeInHierarchy) return false;
            return component is not Behaviour behaviour || behaviour.isActiveAndEnabled;
        }

        private static string FindChildText(GameObject gameObject)
        {
            foreach (Component component in gameObject.GetComponentsInChildren<Component>(true))
            {
                string fullName = component?.GetType().FullName;
                if (fullName == null || !TextComponentNames.Contains(fullName)) continue;
                string text = ReadStringProperty(component, "text");
                if (!string.IsNullOrEmpty(text)) return text;
            }
            return null;
        }

        private static object ReadControlValue(Component component)
        {
            foreach (string propertyName in new[] { "isOn", "value", "text" })
            {
                object value = ReadProperty(component, propertyName);
                if (value != null) return value;
            }
            return null;
        }

        private static object ReadProperty(object target, string propertyName)
        {
            try
            {
                return target?.GetType()
                    .GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(target);
            }
            catch
            {
                return null;
            }
        }

        private static string ReadStringProperty(object target, string propertyName)
            => ReadProperty(target, propertyName) as string;

        private static bool ReadBoolProperty(object target, string propertyName, bool fallback)
            => ReadProperty(target, propertyName) is bool value ? value : fallback;

        private static string ReadValueAsString(object target)
            => ReadProperty(target, "value") is string value ? value : null;

        private static bool IsUiToolkitInteractable(string typeName)
        {
            return typeName is "Button" or "Toggle" or "Slider" or "SliderInt"
                or "DropdownField" or "TextField" or "ScrollView";
        }

        internal static string GetTransformPath(Transform transform)
        {
            if (transform == null) return null;
            var names = new Stack<string>();
            for (Transform current = transform; current != null; current = current.parent)
                names.Push(current.name);
            return string.Join("/", names);
        }

        private static string BuildKey(PlayModeUiItem item)
            => item.Framework + ":" + item.InstanceId + ":" + item.Type;
    }
}
