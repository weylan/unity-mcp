using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Runtime.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services.PlayMode
{
    internal sealed class PlayModeInputResult
    {
        private PlayModeInputResult(bool success, string code, string message, object data)
        {
            Success = success;
            Code = code;
            Message = message;
            Data = data;
        }

        internal bool Success { get; }
        internal string Code { get; }
        internal string Message { get; }
        internal object Data { get; }

        internal static PlayModeInputResult Ok(string message, object data = null)
            => new(true, null, message, data);

        internal static PlayModeInputResult Fail(string code, string message, object data = null)
            => new(false, code, message, data);

        internal object ToResponse()
            => Success
                ? new SuccessResponse(Message, Data)
                : new ErrorResponse(Code ?? "input_failed", new { message = Message, details = Data });
    }

    internal interface IPlayModeInputBackend
    {
        string Name { get; }
        object Capabilities { get; }
        PlayModeInputResult Execute(JObject parameters);
        void ReleaseAll();
    }

    internal static class PlayModeInputBackendRegistry
    {
        private static IPlayModeInputBackend _inputSystemBackend;

        internal static IPlayModeInputBackend InputSystemBackend => _inputSystemBackend;

        internal static void RegisterInputSystem(IPlayModeInputBackend backend)
        {
            _inputSystemBackend = backend;
        }

        internal static void ReleaseAll()
        {
            try { _inputSystemBackend?.ReleaseAll(); }
            catch (Exception ex) { McpLog.Warn($"Failed to release simulated input: {ex.Message}"); }
        }
    }

    [InitializeOnLoad]
    internal static class PlayModeInputService
    {
        private static readonly Type EventSystemType = FindType("UnityEngine.EventSystems.EventSystem");
        private static readonly Type PointerEventDataType = FindType("UnityEngine.EventSystems.PointerEventData");

        static PlayModeInputService()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state is PlayModeStateChange.ExitingPlayMode or PlayModeStateChange.EnteredEditMode)
                    PlayModeInputBackendRegistry.ReleaseAll();
            };
            AssemblyReloadEvents.beforeAssemblyReload += PlayModeInputBackendRegistry.ReleaseAll;
        }

        internal static object GetCapabilities()
        {
            return new
            {
                event_system = new
                {
                    available = EventSystemType != null && PointerEventDataType != null,
                    actions = new[] { "ui_click", "ui_drag" },
                },
                input_system = new
                {
                    available = PlayModeInputBackendRegistry.InputSystemBackend != null,
                    backend = PlayModeInputBackendRegistry.InputSystemBackend?.Name,
                    capabilities = PlayModeInputBackendRegistry.InputSystemBackend?.Capabilities,
                },
                coordinate_origin = "top_left",
            };
        }

        internal static PlayModeInputResult Execute(JObject parameters, bool allowDuringJob)
        {
            string action = parameters?["action"]?.ToString()?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(action))
                return PlayModeInputResult.Fail("invalid_action", "'action' is required.");

            if (!allowDuringJob && PlayModeTestJobManager.HasRunningJob)
                return PlayModeInputResult.Fail(
                    "playmode_job_busy",
                    "A Play Mode test sequence owns the input stream. Cancel or wait for it to finish.");

            if (action == "release_all")
            {
                PlayModeInputBackendRegistry.ReleaseAll();
                return PlayModeInputResult.Ok("Released all simulated input.");
            }

            string backend = parameters?["backend"]?.ToString()?.Trim().ToLowerInvariant() ?? "auto";
            bool uiAction = action is "ui_click" or "ui_drag";
            if (backend == "event_system" || (backend == "auto" && uiAction && HasTarget(parameters)))
                return ExecuteEventSystem(parameters, action);

            IPlayModeInputBackend inputSystem = PlayModeInputBackendRegistry.InputSystemBackend;
            if (inputSystem == null)
            {
                return PlayModeInputResult.Fail(
                    "input_backend_unavailable",
                    "This input action requires com.unity.inputsystem. Target-based UI actions can use backend='event_system'.",
                    GetCapabilities());
            }
            return inputSystem.Execute(parameters);
        }

        private static PlayModeInputResult ExecuteEventSystem(JObject parameters, string action)
        {
            if (EventSystemType == null || PointerEventDataType == null)
                return PlayModeInputResult.Fail(
                    "event_system_unavailable",
                    "Unity EventSystem types are unavailable. Install com.unity.ugui.");

            object eventSystem = EventSystemType
                .GetProperty("current", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            if (eventSystem == null)
                return PlayModeInputResult.Fail("event_system_missing", "No active EventSystem exists in the scene.");

            JToken sourceToken = action == "ui_drag"
                ? parameters["fromTarget"] ?? parameters["from_target"] ?? parameters["target"]
                : parameters["target"];
            GameObject target = ResolveTarget(sourceToken, eventSystem, parameters["position"] as JArray);
            if (target == null)
                return PlayModeInputResult.Fail("ui_target_not_found", "Could not resolve the UI target.");

            Vector2 startPosition = ResolvePosition(parameters["position"] as JArray, target);
            object pointerData = CreatePointerEventData(eventSystem, startPosition);
            if (pointerData == null)
                return PlayModeInputResult.Fail("pointer_event_failed", "Could not create PointerEventData.");

            if (action == "ui_click")
            {
                var invoked = new List<string>();
                invoked.AddRange(DispatchHierarchy(target, "IPointerDownHandler", "OnPointerDown", pointerData));
                invoked.AddRange(DispatchHierarchy(target, "IPointerUpHandler", "OnPointerUp", pointerData));
                invoked.AddRange(DispatchHierarchy(target, "IPointerClickHandler", "OnPointerClick", pointerData));
                if (invoked.Count == 0)
                    return PlayModeInputResult.Fail("ui_handler_missing", $"'{target.name}' has no pointer click handler.");
                return PlayModeInputResult.Ok("UI click dispatched.", new
                {
                    backend = "event_system",
                    target = PlayModeUiScanner.GetTransformPath(target.transform),
                    instanceID = target.GetInstanceIDCompat(),
                    position = new[] { startPosition.x, startPosition.y },
                    handlers = invoked.Distinct().ToArray(),
                });
            }

            GameObject destination = ResolveTarget(
                parameters["toTarget"] ?? parameters["to_target"],
                eventSystem,
                parameters["endPosition"] as JArray ?? parameters["end_position"] as JArray);
            if (destination == null)
                return PlayModeInputResult.Fail("ui_drag_target_not_found", "Could not resolve the drag destination.");
            Vector2 endPosition = ResolvePosition(
                parameters["endPosition"] as JArray ?? parameters["end_position"] as JArray,
                destination);
            var dragHandlers = new List<string>();
            SetPointerProperty(pointerData, "pressPosition", startPosition);
            SetPointerProperty(pointerData, "pointerPress", target);
            SetPointerProperty(pointerData, "pointerDrag", target);
            dragHandlers.AddRange(DispatchHierarchy(target, "IPointerDownHandler", "OnPointerDown", pointerData));
            dragHandlers.AddRange(DispatchHierarchy(target, "IInitializePotentialDragHandler", "OnInitializePotentialDrag", pointerData));
            dragHandlers.AddRange(DispatchHierarchy(target, "IBeginDragHandler", "OnBeginDrag", pointerData));
            SetPointerPosition(pointerData, endPosition);
            dragHandlers.AddRange(DispatchHierarchy(target, "IDragHandler", "OnDrag", pointerData));
            dragHandlers.AddRange(DispatchHierarchy(destination, "IDropHandler", "OnDrop", pointerData));
            dragHandlers.AddRange(DispatchHierarchy(target, "IEndDragHandler", "OnEndDrag", pointerData));
            dragHandlers.AddRange(DispatchHierarchy(target, "IPointerUpHandler", "OnPointerUp", pointerData));
            if (dragHandlers.Count == 0)
                return PlayModeInputResult.Fail("ui_handler_missing", $"'{target.name}' has no drag handler.");
            return PlayModeInputResult.Ok("UI drag dispatched.", new
            {
                backend = "event_system",
                source = PlayModeUiScanner.GetTransformPath(target.transform),
                destination = PlayModeUiScanner.GetTransformPath(destination.transform),
                handlers = dragHandlers.Distinct().ToArray(),
            });
        }

        private static bool HasTarget(JObject parameters)
            => parameters?["target"] != null || parameters?["fromTarget"] != null;

        private static GameObject ResolveTarget(JToken targetToken, object eventSystem, JArray position)
        {
            if (targetToken != null)
            {
                GameObject direct = PlayModeTargetResolver.Resolve(targetToken);
                if (direct != null) return direct;
            }
            return position != null ? Raycast(eventSystem, NormalizedToScreen(position)) : null;
        }

        private static GameObject Raycast(object eventSystem, Vector2 position)
        {
            object pointerData = CreatePointerEventData(eventSystem, position);
            Type raycastResultType = FindType("UnityEngine.EventSystems.RaycastResult");
            if (pointerData == null || raycastResultType == null) return null;

            Type listType = typeof(List<>).MakeGenericType(raycastResultType);
            var results = (IList)Activator.CreateInstance(listType);
            MethodInfo raycastAll = EventSystemType.GetMethod(
                "RaycastAll",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { PointerEventDataType, listType },
                null);
            raycastAll?.Invoke(eventSystem, new object[] { pointerData, results });
            if (results == null || results.Count == 0) return null;
            return raycastResultType.GetProperty("gameObject")?.GetValue(results[0]) as GameObject
                ?? raycastResultType.GetField("gameObject")?.GetValue(results[0]) as GameObject;
        }

        private static object CreatePointerEventData(object eventSystem, Vector2 position)
        {
            try
            {
                object data = Activator.CreateInstance(PointerEventDataType, eventSystem);
                SetPointerPosition(data, position);
                return data;
            }
            catch
            {
                return null;
            }
        }

        private static void SetPointerPosition(object data, Vector2 position)
        {
            PointerEventDataType.GetProperty("position")?.SetValue(data, position);
        }

        private static void SetPointerProperty(object data, string propertyName, object value)
        {
            try { PointerEventDataType.GetProperty(propertyName)?.SetValue(data, value); }
            catch { /* Optional metadata differs across uGUI versions. */ }
        }

        private static IEnumerable<string> DispatchHierarchy(
            GameObject start,
            string interfaceName,
            string methodName,
            object eventData)
        {
            Type handlerType = FindType("UnityEngine.EventSystems." + interfaceName);
            if (handlerType == null) yield break;

            for (Transform current = start.transform; current != null; current = current.parent)
            {
                var handlers = current.GetComponents<Component>()
                    .Where(component => component != null && handlerType.IsAssignableFrom(component.GetType()))
                    .ToArray();
                if (handlers.Length == 0) continue;
                foreach (Component handler in handlers)
                {
                    MethodInfo method = handler.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
                    if (method == null) continue;
                    method.Invoke(handler, new[] { eventData });
                    yield return handler.GetType().Name + "." + methodName;
                }
                yield break;
            }
        }

        private static Vector2 ResolvePosition(JArray normalized, GameObject target)
        {
            if (normalized is { Count: >= 2 }) return NormalizedToScreen(normalized);
            if (target.transform is RectTransform rect)
                return RectTransformUtility.WorldToScreenPoint(Camera.main, rect.TransformPoint(rect.rect.center));
            return Camera.main != null
                ? (Vector2)Camera.main.WorldToScreenPoint(target.transform.position)
                : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        }

        internal static Vector2 NormalizedToScreen(JArray normalized)
        {
            float x = Mathf.Clamp01(normalized?[0]?.Value<float>() ?? 0.5f);
            float y = Mathf.Clamp01(normalized?[1]?.Value<float>() ?? 0.5f);
            return new Vector2(x * Screen.width, (1f - y) * Screen.height);
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            return null;
        }
    }
}
