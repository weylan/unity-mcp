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
        private static readonly string[] PointerHandlerNames =
        {
            "IPointerClickHandler",
            "IPointerDownHandler",
            "IPointerUpHandler",
            "IBeginDragHandler",
            "IDragHandler",
            "IEndDragHandler",
            "IDropHandler",
        };
        private static readonly Dictionary<string, Type> HandlerTypes = new(StringComparer.Ordinal);
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
                    click_hit_tests = new[] { "strict", "direct" },
                    default_target_hit_test = "strict",
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

        internal static string[] GetPointerHandlerNames(Component component)
        {
            if (component == null) return Array.Empty<string>();
            Type componentType = component.GetType();
            return PointerHandlerNames
                .Where(name => GetHandlerType(name)?.IsAssignableFrom(componentType) == true)
                .ToArray();
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
            bool hasExplicitTarget = sourceToken != null;
            GameObject target = ResolveTarget(sourceToken, eventSystem, parameters["position"] as JArray);
            if (target == null)
                return PlayModeInputResult.Fail("ui_target_not_found", "Could not resolve the UI target.");
            if (!target.activeInHierarchy)
                return PlayModeInputResult.Fail("ui_target_inactive", $"'{target.name}' is not active in the hierarchy.");

            Vector2 startPosition = ResolvePosition(parameters["position"] as JArray, target);
            object pointerData = CreatePointerEventData(eventSystem, startPosition);
            if (pointerData == null)
                return PlayModeInputResult.Fail("pointer_event_failed", "Could not create PointerEventData.");

            if (action == "ui_click")
            {
                string hitTest = parameters["hitTest"]?.ToString()?.Trim().ToLowerInvariant()
                    ?? parameters["hit_test"]?.ToString()?.Trim().ToLowerInvariant()
                    ?? (hasExplicitTarget ? "strict" : "direct");
                if (hitTest is not "strict" and not "direct")
                    return PlayModeInputResult.Fail(
                        "invalid_hit_test",
                        "hitTest must be 'strict' or 'direct'.");

                GameObject dispatchStart = target;
                GameObject actualTopHit = null;
                object actualTopRaycast = null;
                GameObject clickHandler = FindHandlerOwner(target, "IPointerClickHandler");
                if (hitTest == "strict")
                {
                    actualTopHit = Raycast(eventSystem, pointerData, out actualTopRaycast);
                    if (actualTopHit == null)
                    {
                        return PlayModeInputResult.Fail(
                            "ui_target_not_hittable",
                            $"'{target.name}' is not under an EventSystem raycast hit.",
                            new { expectedTarget = BuildIdentity(target), actualTopHit = (object)null });
                    }

                    clickHandler = FindHandlerOwner(actualTopHit, "IPointerClickHandler");
                    if ((hasExplicitTarget && clickHandler != target)
                        || HasConflictingHandler(actualTopHit, clickHandler, "IPointerDownHandler")
                        || HasConflictingHandler(actualTopHit, clickHandler, "IPointerUpHandler"))
                    {
                        return PlayModeInputResult.Fail(
                            "blocked_by_overlay",
                            "The expected target does not own the top EventSystem hit.",
                            new
                            {
                                expectedTarget = BuildIdentity(target),
                                actualTopHit = BuildIdentity(actualTopHit),
                                handlerTarget = BuildIdentity(clickHandler),
                            });
                    }
                    dispatchStart = actualTopHit;
                    SetPointerProperty(pointerData, "pointerCurrentRaycast", actualTopRaycast);
                    SetPointerProperty(pointerData, "pointerPressRaycast", actualTopRaycast);
                    SetPointerProperty(pointerData, "pointerEnter", actualTopHit);
                }

                if (clickHandler == null)
                    return PlayModeInputResult.Fail("ui_handler_missing", $"'{target.name}' has no pointer click handler.");
                if (!IsInteractable(clickHandler))
                    return PlayModeInputResult.Fail(
                        "ui_target_not_interactable",
                        $"'{clickHandler.name}' is not interactable.");

                PrepareClickPointerData(pointerData, startPosition, clickHandler);
                var invoked = new List<string>();
                invoked.AddRange(DispatchHierarchy(dispatchStart, "IPointerDownHandler", "OnPointerDown", pointerData));
                invoked.AddRange(DispatchHierarchy(dispatchStart, "IPointerUpHandler", "OnPointerUp", pointerData));
                invoked.AddRange(DispatchHierarchy(dispatchStart, "IPointerClickHandler", "OnPointerClick", pointerData));
                if (invoked.Count == 0)
                    return PlayModeInputResult.Fail("ui_handler_missing", $"'{target.name}' has no pointer click handler.");
                return PlayModeInputResult.Ok("UI click dispatched.", new
                {
                    backend = "event_system",
                    hitTest,
                    target = PlayModeUiScanner.GetTransformPath(target.transform),
                    instanceID = target.GetInstanceIDCompat(),
                    actualTopHit = BuildIdentity(actualTopHit),
                    handlerTarget = BuildIdentity(clickHandler),
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
            if (position == null) return null;
            object pointerData = CreatePointerEventData(eventSystem, NormalizedToScreen(position));
            return Raycast(eventSystem, pointerData, out _);
        }

        private static GameObject Raycast(object eventSystem, object pointerData, out object topResult)
        {
            topResult = null;
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
            topResult = results[0];
            return raycastResultType.GetProperty("gameObject")?.GetValue(topResult) as GameObject
                ?? raycastResultType.GetField("gameObject")?.GetValue(topResult) as GameObject;
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

        private static void PrepareClickPointerData(object data, Vector2 position, GameObject handler)
        {
            SetPointerProperty(data, "pressPosition", position);
            SetPointerProperty(data, "pointerPress", handler);
            SetPointerProperty(data, "eligibleForClick", true);
            SetPointerProperty(data, "clickCount", 1);
            try
            {
                PropertyInfo button = PointerEventDataType.GetProperty("button");
                if (button?.PropertyType.IsEnum == true)
                    button.SetValue(data, Enum.ToObject(button.PropertyType, 0));
            }
            catch
            {
                // Older uGUI versions may expose button metadata differently.
            }
        }

        private static GameObject FindHandlerOwner(GameObject start, string interfaceName)
        {
            Type handlerType = GetHandlerType(interfaceName);
            if (start == null || handlerType == null) return null;
            for (Transform current = start.transform; current != null; current = current.parent)
            {
                if (current.GetComponents<Component>()
                    .Any(component => component != null && handlerType.IsAssignableFrom(component.GetType())))
                    return current.gameObject;
            }
            return null;
        }

        private static bool HasConflictingHandler(
            GameObject topHit,
            GameObject clickHandler,
            string interfaceName)
        {
            GameObject handler = FindHandlerOwner(topHit, interfaceName);
            return handler != null && handler != clickHandler;
        }

        private static bool IsInteractable(GameObject target)
        {
            Type selectableType = FindType("UnityEngine.UI.Selectable");
            if (target == null || selectableType == null) return true;
            Component selectable = target.GetComponents<Component>()
                .FirstOrDefault(component => component != null && selectableType.IsAssignableFrom(component.GetType()));
            if (selectable == null) return true;
            try
            {
                MethodInfo method = selectable.GetType().GetMethod(
                    "IsInteractable",
                    BindingFlags.Public | BindingFlags.Instance);
                return method?.Invoke(selectable, null) is not bool value || value;
            }
            catch
            {
                return true;
            }
        }

        private static object BuildIdentity(GameObject gameObject)
        {
            if (gameObject == null) return null;
            return new
            {
                name = gameObject.name,
                path = PlayModeUiScanner.GetTransformPath(gameObject.transform),
                instanceID = gameObject.GetInstanceIDCompat(),
            };
        }

        private static Type GetHandlerType(string interfaceName)
        {
            if (!HandlerTypes.TryGetValue(interfaceName, out Type handlerType))
            {
                handlerType = FindType("UnityEngine.EventSystems." + interfaceName);
                HandlerTypes[interfaceName] = handlerType;
            }
            return handlerType;
        }

        private static IEnumerable<string> DispatchHierarchy(
            GameObject start,
            string interfaceName,
            string methodName,
            object eventData)
        {
            Type handlerType = GetHandlerType(interfaceName);
            if (handlerType == null) yield break;

            for (Transform current = start.transform; current != null; current = current.parent)
            {
                var handlers = current.GetComponents<Component>()
                    .Where(component => component != null && handlerType.IsAssignableFrom(component.GetType()))
                    .ToArray();
                if (handlers.Length == 0) continue;
                foreach (Component handler in handlers)
                {
                    MethodInfo method = ResolveHandlerMethod(handler.GetType(), handlerType, methodName);
                    if (method == null) continue;
                    method.Invoke(handler, new[] { eventData });
                    yield return handler.GetType().Name + "." + methodName;
                }
                yield break;
            }
        }

        private static MethodInfo ResolveHandlerMethod(
            Type componentType,
            Type handlerType,
            string methodName)
        {
            MethodInfo interfaceMethod = handlerType.GetMethod(methodName);
            if (interfaceMethod == null) return null;
            try
            {
                InterfaceMapping mapping = componentType.GetInterfaceMap(handlerType);
                int index = Array.IndexOf(mapping.InterfaceMethods, interfaceMethod);
                return index >= 0 ? mapping.TargetMethods[index] : null;
            }
            catch
            {
                return componentType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
            }
        }

        private static Vector2 ResolvePosition(JArray normalized, GameObject target)
        {
            if (normalized is { Count: >= 2 }) return NormalizedToScreen(normalized);
            if (target.transform is RectTransform rect)
            {
                Canvas canvas = rect.GetComponentInParent<Canvas>();
                Camera camera = canvas != null && canvas.renderMode == RenderMode.ScreenSpaceOverlay
                    ? null
                    : canvas != null && canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
                return RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center));
            }
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
