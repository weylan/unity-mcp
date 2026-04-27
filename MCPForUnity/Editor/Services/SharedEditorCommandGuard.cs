using System;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace MCPForUnity.Editor.Services
{
    internal static class SharedEditorCommandGuard
    {
        private const string EnvModeKey = "UNITY_MCP_SHARED_EDITOR_GUARD";
        private const string EditorPrefsModeKey = "MCPForUnity.SharedEditorCommandGuard.Mode";
        private const string BlockedCode = "shared_editor_guard_blocked";
        private const int DefaultRetryAfterMs = 5000;

        internal sealed class Decision
        {
            private Decision(bool allowed, bool warnOnly, string tool, string action, string reason)
            {
                Allowed = allowed;
                WarnOnly = warnOnly;
                Tool = tool;
                Action = action;
                Reason = reason;
            }

            public bool Allowed { get; }
            public bool WarnOnly { get; }
            public string Tool { get; }
            public string Action { get; }
            public string Reason { get; }
            public int RetryAfterMs => DefaultRetryAfterMs;

            public static Decision Allow(string tool, string action = null)
            {
                return new Decision(true, false, tool, action, null);
            }

            public static Decision Warn(string tool, string action, string reason)
            {
                return new Decision(true, true, tool, action, reason);
            }

            public static Decision Block(string tool, string action, string reason)
            {
                return new Decision(false, false, tool, action, reason);
            }

            public ErrorResponse ToErrorResponse()
            {
                return new ErrorResponse(BlockedCode, new
                {
                    reason = Reason,
                    tool = Tool,
                    action = Action,
                    retry_after_ms = RetryAfterMs
                });
            }
        }

        public static Decision Evaluate(string toolName, JObject parameters, bool hasValidEditorLockToken = false)
        {
            string normalizedTool = (toolName ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(normalizedTool))
            {
                return Decision.Allow(toolName);
            }

            var p = new ToolParams(parameters ?? new JObject());
            string action = p.Get("action");
            string reason = GetRiskReason(normalizedTool, p, action);
            if (string.IsNullOrEmpty(reason))
            {
                return Decision.Allow(normalizedTool, action);
            }

            string mode = GetMode();
            if (string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase))
            {
                return Decision.Allow(normalizedTool, action);
            }

            if (hasValidEditorLockToken)
            {
                return Decision.Warn(
                    normalizedTool,
                    action,
                    $"explicit editor_lock_token accepted for guarded command: {reason}");
            }

            if (string.Equals(mode, "warn", StringComparison.OrdinalIgnoreCase))
            {
                return Decision.Warn(normalizedTool, action, reason);
            }

            return Decision.Block(normalizedTool, action, reason);
        }

        public static void LogDecision(Decision decision, JObject parameters, string commandKind, long durationMs = 0)
        {
            if (decision == null || string.IsNullOrEmpty(decision.Reason))
            {
                return;
            }

            string mode = decision.WarnOnly ? "warn" : "block";
            McpLog.Warn(
                $"SharedEditorCommandGuard {mode}: tool={decision.Tool}, action={decision.Action ?? "(none)"}, reason={decision.Reason}");

            McpLogRecord.Log(
                decision.Tool,
                parameters ?? new JObject(),
                commandKind ?? "tool",
                decision.Allowed ? "SUCCESS" : "ERROR",
                durationMs,
                $"{BlockedCode}: {decision.Reason}");
        }

        private static string GetMode()
        {
            string env = Environment.GetEnvironmentVariable(EnvModeKey);
            if (IsKnownMode(env))
            {
                return env;
            }

            string pref = null;
            try
            {
                pref = EditorPrefs.GetString(EditorPrefsModeKey, null);
            }
            catch
            {
                // EditorPrefs may be unavailable during shutdown/domain transitions.
            }

            return IsKnownMode(pref) ? pref : "block";
        }

        private static bool IsKnownMode(string mode)
        {
            return string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(mode, "warn", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(mode, "block", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetRiskReason(string toolName, ToolParams p, string action)
        {
            switch (toolName)
            {
                case "run_tests":
                    return HasTestFilter(p) ? null : "run_tests requires at least one explicit test filter on shared editors";
                case "refresh_unity":
                    return GetRefreshUnityRisk(p);
                case "execute_menu_item":
                    return "execute_menu_item can invoke arbitrary editor operations on shared editors";
                case "manage_editor":
                    return GetManageEditorRisk(action);
                case "manage_build":
                    return GetManageBuildRisk(action, p);
                case "manage_packages":
                    return GetManagePackagesRisk(action);
                case "manage_graphics":
                    return GetManageGraphicsRisk(action);
                case "manage_editor_lock":
                    return null;
                default:
                    return null;
            }
        }

        private static bool HasTestFilter(ToolParams p)
        {
            return HasStringArray(p, "testNames")
                   || HasStringArray(p, "groupNames")
                   || HasStringArray(p, "categoryNames")
                   || HasStringArray(p, "assemblyNames")
                   || HasStringArray(p, "test_names")
                   || HasStringArray(p, "group_names")
                   || HasStringArray(p, "category_names")
                   || HasStringArray(p, "assembly_names");
        }

        private static bool HasStringArray(ToolParams p, string key)
        {
            var values = p.GetStringArray(key);
            return values != null && values.Length > 0;
        }

        private static string GetRefreshUnityRisk(ToolParams p)
        {
            string mode = p.Get("mode", "if_dirty");
            string scope = p.Get("scope", "all");
            string compile = p.Get("compile", "none");
            bool waitForReady = p.GetBool("wait_for_ready", false);

            if (waitForReady)
            {
                return "refresh_unity wait_for_ready can monopolize shared editors";
            }

            if (EqualsIgnoreCase(compile, "request"))
            {
                return "refresh_unity compile=request can trigger domain reload on shared editors";
            }

            if (EqualsIgnoreCase(mode, "force"))
            {
                return "refresh_unity mode=force can trigger long synchronous imports on shared editors";
            }

            if (EqualsIgnoreCase(scope, "all"))
            {
                return "refresh_unity scope=all can trigger broad asset imports on shared editors";
            }

            return null;
        }

        private static string GetManageEditorRisk(string action)
        {
            if (EqualsAny(action, "play", "pause", "deploy_package", "restore_package"))
            {
                return $"manage_editor action={action} is high risk on shared editors";
            }

            return null;
        }

        private static string GetManageBuildRisk(string action, ToolParams p)
        {
            if (EqualsAny(action, "build", "batch", "platform"))
            {
                return $"manage_build action={action} can monopolize or reconfigure shared editors";
            }

            if (EqualsIgnoreCase(action, "profiles") && p.GetBool("activate", false))
            {
                return "manage_build profiles activate changes shared editor build configuration";
            }

            if (EqualsIgnoreCase(action, "settings") && p.Has("value"))
            {
                return "manage_build settings write changes shared editor build configuration";
            }

            if (EqualsIgnoreCase(action, "scenes")
                && (p.Has("scenes") || p.Has("remove_scene") || p.Has("removeScene")))
            {
                return "manage_build scenes write changes shared editor build configuration";
            }

            return null;
        }

        private static string GetManagePackagesRisk(string action)
        {
            if (EqualsAny(action,
                    "add_package",
                    "remove_package",
                    "embed_package",
                    "resolve_packages",
                    "add_registry",
                    "remove_registry"))
            {
                return $"manage_packages action={action} can trigger package resolution on shared editors";
            }

            return null;
        }

        private static string GetManageGraphicsRisk(string action)
        {
            if (EqualsAny(action,
                    "bake_start",
                    "bake_clear",
                    "bake_reflection_probe",
                    "bake_set_settings",
                    "bake_create_light_probe_group",
                    "bake_create_reflection_probe",
                    "bake_set_probe_positions"))
            {
                return $"manage_graphics action={action} can trigger long rendering or bake work on shared editors";
            }

            return null;
        }

        private static bool EqualsAny(string value, params string[] candidates)
        {
            foreach (string candidate in candidates)
            {
                if (EqualsIgnoreCase(value, candidate))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool EqualsIgnoreCase(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
