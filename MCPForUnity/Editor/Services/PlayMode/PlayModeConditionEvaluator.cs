using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Runtime.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MCPForUnity.Editor.Services.PlayMode
{
    internal sealed class PlayModeConditionResult
    {
        internal bool Matched { get; set; }
        internal JToken Actual { get; set; }
        internal JToken Expected { get; set; }
        internal string Error { get; set; }
        internal string Description { get; set; }
    }

    internal static class PlayModeConditionEvaluator
    {
        internal static PlayModeConditionResult Evaluate(JObject condition)
        {
            if (condition == null)
                return Failure("condition is required.");
            string type = condition["type"]?.ToString()?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(type))
                return Failure("condition.type is required.");

            try
            {
                return type switch
                {
                    "component_property" => EvaluateComponentProperty(condition),
                    "object_exists" => EvaluateObjectExists(condition),
                    "ui_text" => EvaluateUiText(condition),
                    "scene" => CompareObserved(new JValue(SceneManager.GetActiveScene().name), condition),
                    "play_mode" => CompareObserved(new JValue(GetPlayMode()), condition),
                    "frame_count" => CompareObserved(new JValue(Time.frameCount), condition),
                    "animator_state" => EvaluateAnimatorState(condition),
                    "animator_parameter" => EvaluateAnimatorParameter(condition),
                    _ => Failure($"Unknown condition type '{type}'."),
                };
            }
            catch (Exception ex)
            {
                return Failure($"Condition evaluation failed: {ex.Message}");
            }
        }

        internal static PlayModeConditionResult Compare(
            JToken actual,
            string op,
            JToken expected,
            double tolerance)
        {
            string normalized = NormalizeOperator(op);
            if (normalized == null)
                return Failure($"Unknown condition operator '{op}'.", actual, expected);

            if (normalized is "gt" or "gte" or "lt" or "lte")
            {
                if (!TryNumber(actual, out double actualNumber)
                    || !TryNumber(expected, out double expectedNumber))
                    return Failure(
                        $"Operator '{normalized}' requires numeric values.",
                        actual,
                        expected);

                bool numericMatch = normalized switch
                {
                    "gt" => actualNumber > expectedNumber,
                    "gte" => actualNumber >= expectedNumber,
                    "lt" => actualNumber < expectedNumber,
                    "lte" => actualNumber <= expectedNumber,
                    _ => false,
                };
                return Success(numericMatch, actual, expected);
            }

            if (normalized is "contains" or "not_contains")
            {
                string actualText = actual?.Type == JTokenType.Null ? string.Empty : actual?.ToString() ?? string.Empty;
                string expectedText = expected?.Type == JTokenType.Null ? string.Empty : expected?.ToString() ?? string.Empty;
                bool contains = actualText.IndexOf(expectedText, StringComparison.OrdinalIgnoreCase) >= 0;
                return Success(normalized == "contains" ? contains : !contains, actual, expected);
            }

            bool equal;
            if (TryNumber(actual, out double actualNumeric) && TryNumber(expected, out double expectedNumeric))
                equal = Math.Abs(actualNumeric - expectedNumeric) <= Math.Max(0.0, tolerance);
            else if (actual?.Type == JTokenType.Boolean || expected?.Type == JTokenType.Boolean)
                equal = TryBool(actual, out bool actualBool)
                    && TryBool(expected, out bool expectedBool)
                    && actualBool == expectedBool;
            else
                equal = string.Equals(
                    actual?.Type == JTokenType.Null ? null : actual?.ToString(),
                    expected?.Type == JTokenType.Null ? null : expected?.ToString(),
                    StringComparison.OrdinalIgnoreCase);

            return Success(normalized == "equals" ? equal : !equal, actual, expected);
        }

        private static PlayModeConditionResult EvaluateComponentProperty(JObject condition)
        {
            GameObject target = PlayModeTargetResolver.Resolve(
                condition["target"],
                condition["search_method"]?.ToString() ?? condition["searchMethod"]?.ToString());
            if (target == null)
                return Failure("Condition target GameObject was not found.");

            string componentName = condition["component"]?.ToString();
            string propertyPath = condition["property"]?.ToString();
            if (string.IsNullOrWhiteSpace(componentName) || string.IsNullOrWhiteSpace(propertyPath))
                return Failure("component_property requires component and property.");

            Type componentType = UnityTypeResolver.ResolveComponent(componentName);
            Component component = componentType != null ? target.GetComponent(componentType) : null;
            if (component == null)
                return Failure($"Component '{componentName}' was not found on '{target.name}'.");

            if (!TryReadMemberPath(component, propertyPath, out object value, out string error))
                return Failure(error);
            return CompareObserved(ToToken(value), condition);
        }

        private static PlayModeConditionResult EvaluateObjectExists(JObject condition)
        {
            GameObject target = PlayModeTargetResolver.Resolve(
                condition["target"],
                condition["search_method"]?.ToString() ?? condition["searchMethod"]?.ToString());
            JObject copy = (JObject)condition.DeepClone();
            copy["value"] ??= true;
            return CompareObserved(new JValue(target != null), copy);
        }

        private static PlayModeConditionResult EvaluateUiText(JObject condition)
        {
            JToken expected = condition["value"] ?? condition["text"];
            if (expected == null) return Failure("ui_text requires value or text.");
            string op = condition["operator"]?.ToString() ?? "contains";
            double tolerance = condition["tolerance"]?.Value<double>() ?? 0.0;
            var texts = PlayModeUiScanner.Scan()
                .Select(item => item.Text)
                .Where(text => !string.IsNullOrEmpty(text))
                .Distinct(StringComparer.Ordinal)
                .Take(100)
                .ToArray();
            string normalized = NormalizeOperator(op);
            if (normalized is "not_contains" or "not_equals")
            {
                string positiveOperator = normalized == "not_contains" ? "contains" : "equals";
                bool positiveMatch = texts.Any(text =>
                    Compare(new JValue(text), positiveOperator, expected, tolerance).Matched);
                var negativeResult = Success(!positiveMatch, new JArray(texts), expected);
                negativeResult.Description = $"ui_text: no visible text {positiveOperator} expected={expected}";
                return negativeResult;
            }
            foreach (string text in texts)
            {
                PlayModeConditionResult result = Compare(new JValue(text), op, expected, tolerance);
                if (result.Matched) return result;
                if (result.Error != null) return result;
            }
            return Success(false, new JArray(texts), expected);
        }

        private static PlayModeConditionResult EvaluateAnimatorState(JObject condition)
        {
            Animator animator = ResolveAnimator(condition);
            if (animator == null) return Failure("Animator target was not found.");
            int layer = Math.Max(0, condition["layer"]?.Value<int>() ?? 0);
            if (layer >= animator.layerCount) return Failure($"Animator layer {layer} is out of range.");
            AnimatorStateInfo info = animator.IsInTransition(layer)
                ? animator.GetNextAnimatorStateInfo(layer)
                : animator.GetCurrentAnimatorStateInfo(layer);
            JToken expected = condition["value"] ?? condition["state_hash"];
            if (expected?.Type == JTokenType.String && !int.TryParse(expected.ToString(), out _))
                expected = new JValue(Animator.StringToHash(expected.ToString()));
            var copy = (JObject)condition.DeepClone();
            copy["value"] = expected;
            return CompareObserved(new JValue(info.shortNameHash), copy);
        }

        private static PlayModeConditionResult EvaluateAnimatorParameter(JObject condition)
        {
            Animator animator = ResolveAnimator(condition);
            if (animator == null) return Failure("Animator target was not found.");
            string parameterName = condition["parameter"]?.ToString();
            AnimatorControllerParameter parameter = animator.parameters.FirstOrDefault(item => item.name == parameterName);
            if (parameter == null) return Failure($"Animator parameter '{parameterName}' was not found.");
            object value = parameter.type switch
            {
                AnimatorControllerParameterType.Float => animator.GetFloat(parameter.nameHash),
                AnimatorControllerParameterType.Int => animator.GetInteger(parameter.nameHash),
                AnimatorControllerParameterType.Bool => animator.GetBool(parameter.nameHash),
                AnimatorControllerParameterType.Trigger => animator.GetBool(parameter.nameHash),
                _ => null,
            };
            return CompareObserved(ToToken(value), condition);
        }

        private static Animator ResolveAnimator(JObject condition)
        {
            GameObject target = PlayModeTargetResolver.Resolve(
                condition["target"],
                condition["search_method"]?.ToString() ?? condition["searchMethod"]?.ToString());
            return target != null ? target.GetComponent<Animator>() : null;
        }

        private static PlayModeConditionResult CompareObserved(JToken actual, JObject condition)
        {
            JToken expected = condition["value"];
            if (expected == null) return Failure("condition.value is required.", actual);
            string op = condition["operator"]?.ToString() ?? "equals";
            double tolerance = condition["tolerance"]?.Value<double>() ?? 0.0;
            PlayModeConditionResult result = Compare(actual, op, expected, tolerance);
            result.Description = $"{condition["type"]}: actual={actual}, {op} expected={expected}";
            return result;
        }

        private static bool TryReadMemberPath(
            object root,
            string path,
            out object value,
            out string error)
        {
            value = root;
            error = null;
            string[] segments = path.Split('.');
            if (segments.Length > 4)
            {
                error = "Property paths may contain at most four segments.";
                return false;
            }

            foreach (string segment in segments)
            {
                if (value == null)
                {
                    error = $"Property path '{path}' reached null at '{segment}'.";
                    return false;
                }
                Type type = value.GetType();
                const BindingFlags publicFlags = BindingFlags.Public | BindingFlags.Instance;
                PropertyInfo property = type.GetProperty(segment, publicFlags);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    value = property.GetValue(value);
                    continue;
                }
                FieldInfo field = type.GetField(segment, publicFlags)
                    ?? type.GetField(segment, BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null
                    && (field.IsPublic || field.GetCustomAttribute<SerializeField>() != null))
                {
                    value = field.GetValue(value);
                    continue;
                }
                error = $"Member '{segment}' was not found while reading '{path}'.";
                return false;
            }
            return true;
        }

        private static string GetPlayMode()
            => !EditorApplication.isPlaying ? "editing" : EditorApplication.isPaused ? "paused" : "playing";

        private static string NormalizeOperator(string op)
        {
            return (op ?? "equals").Trim().ToLowerInvariant() switch
            {
                "equals" or "equal" or "eq" or "==" => "equals",
                "not_equals" or "not_equal" or "ne" or "!=" => "not_equals",
                "gt" or ">" => "gt",
                "gte" or ">=" => "gte",
                "lt" or "<" => "lt",
                "lte" or "<=" => "lte",
                "contains" => "contains",
                "not_contains" => "not_contains",
                _ => null,
            };
        }

        private static bool TryNumber(JToken token, out double value)
        {
            value = 0.0;
            if (token == null || token.Type == JTokenType.Null) return false;
            if (token.Type is JTokenType.Integer or JTokenType.Float)
            {
                value = token.Value<double>();
                return true;
            }
            return double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryBool(JToken token, out bool value)
        {
            value = false;
            if (token == null || token.Type == JTokenType.Null) return false;
            if (token.Type == JTokenType.Boolean)
            {
                value = token.Value<bool>();
                return true;
            }
            return bool.TryParse(token.ToString(), out value);
        }

        private static JToken ToToken(object value)
        {
            if (value == null) return JValue.CreateNull();
            if (value is UnityEngine.Object unityObject)
                return new JObject
                {
                    ["instanceID"] = unityObject.GetInstanceIDCompat(),
                    ["name"] = unityObject.name,
                };
            try { return JToken.FromObject(value); }
            catch { return new JValue(value.ToString()); }
        }

        private static PlayModeConditionResult Success(bool matched, JToken actual, JToken expected)
            => new() { Matched = matched, Actual = actual, Expected = expected };

        private static PlayModeConditionResult Failure(
            string error,
            JToken actual = null,
            JToken expected = null)
            => new() { Matched = false, Actual = actual, Expected = expected, Error = error };
    }
}
