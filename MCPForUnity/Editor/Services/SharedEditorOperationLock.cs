using System;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace MCPForUnity.Editor.Services
{
    internal static class SharedEditorOperationLock
    {
        private const string EnvEnabledKey = "UNITY_MCP_SHARED_EDITOR_LOCK";
        internal const string BusyCode = "editor_lock_busy";
        internal const string InvalidTokenCode = "editor_lock_token_invalid";

        private const int DefaultAutoTtlSeconds = 60;
        private const int DefaultExplicitTtlSeconds = 120;
        private const int MaxTtlSeconds = 300;
        private const int DefaultRetryAfterMs = 3000;

        private static readonly object SyncRoot = new();

        private sealed class LockState
        {
            public string Token { get; set; }
            public string HolderHint { get; set; }
            public string Reason { get; set; }
            public DateTime AcquiredAtUtc { get; set; }
            public DateTime ExpiresAtUtc { get; set; }
            public bool IsExplicit { get; set; }
        }

        private static LockState _current;

        internal static bool IsEnabled
        {
            get
            {
                string env = Environment.GetEnvironmentVariable(EnvEnabledKey);
                if (string.Equals(env, "off", StringComparison.OrdinalIgnoreCase)) return false;
                if (string.Equals(env, "on", StringComparison.OrdinalIgnoreCase)) return true;
                try { return EditorPrefs.GetBool(EditorPrefKeys.OperationLockEnabled, true); }
                catch { return true; }
            }
        }

        internal static bool IsHighRiskTool(string toolName, string action)
        {
            if (string.IsNullOrEmpty(toolName)) return false;

            switch (toolName)
            {
                case "run_tests":
                case "refresh_unity":
                case "execute_menu_item":
                    return true;

                case "manage_editor":
                    return EqualsAny(action, "play", "pause", "stop", "deploy_package", "restore_package");

                case "manage_build":
                    return EqualsAny(action, "build", "batch", "platform", "profiles", "settings", "scenes");

                case "manage_packages":
                    return EqualsAny(action, "add_package", "remove_package", "embed_package",
                        "resolve_packages", "add_registry", "remove_registry");

                case "manage_graphics":
                    return action != null && action.StartsWith("bake_", StringComparison.OrdinalIgnoreCase);

                default:
                    return false;
            }
        }

        internal sealed class AcquireResult
        {
            public bool Acquired { get; }
            public string Token { get; }
            public DateTime ExpiresAtUtc { get; }
            public LockSnapshot BusyHolder { get; }

            private AcquireResult(bool acquired, string token, DateTime expiresAtUtc, LockSnapshot busyHolder)
            {
                Acquired = acquired;
                Token = token;
                ExpiresAtUtc = expiresAtUtc;
                BusyHolder = busyHolder;
            }

            public static AcquireResult Success(string token, DateTime expiresAtUtc)
                => new(true, token, expiresAtUtc, null);

            public static AcquireResult Busy(LockSnapshot holder)
                => new(false, null, default, holder);
        }

        internal sealed class LockSnapshot
        {
            public bool Locked { get; set; }
            public string HolderHint { get; set; }
            public string Reason { get; set; }
            public DateTime AcquiredAtUtc { get; set; }
            public DateTime ExpiresAtUtc { get; set; }
            public long ExpiresInMs { get; set; }
            public bool IsExplicit { get; set; }
        }

        internal static AcquireResult TryAcquire(string holderHint, string reason, bool isExplicit, int ttlSeconds = 0, string existingToken = null)
        {
            if (!IsEnabled) return AcquireResult.Success(null, DateTime.MaxValue);

            lock (SyncRoot)
            {
                EvictIfExpired();

                if (_current != null)
                {
                    if (!string.IsNullOrEmpty(existingToken) && _current.Token == existingToken)
                    {
                        int ttl = ClampTtl(ttlSeconds, isExplicit);
                        _current.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(ttl);
                        if (!string.IsNullOrEmpty(reason)) _current.Reason = reason;
                        LogAcquired(_current, reentrant: true);
                        return AcquireResult.Success(_current.Token, _current.ExpiresAtUtc);
                    }

                    var snapshot = TakeSnapshot(_current);
                    LogBusy(holderHint, reason, _current);
                    return AcquireResult.Busy(snapshot);
                }

                int effectiveTtl = ClampTtl(ttlSeconds, isExplicit);
                var state = new LockState
                {
                    Token = Guid.NewGuid().ToString("N"),
                    HolderHint = holderHint ?? "unknown",
                    Reason = reason ?? "",
                    AcquiredAtUtc = DateTime.UtcNow,
                    ExpiresAtUtc = DateTime.UtcNow.AddSeconds(effectiveTtl),
                    IsExplicit = isExplicit,
                };
                _current = state;
                LogAcquired(state, reentrant: false);
                return AcquireResult.Success(state.Token, state.ExpiresAtUtc);
            }
        }

        internal static bool Release(string token)
        {
            if (!IsEnabled) return true;

            lock (SyncRoot)
            {
                if (_current == null) return false;
                if (_current.Token != token) return false;

                LogReleased(_current);
                _current = null;
                return true;
            }
        }

        internal static bool ReleaseIfAutoLock(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;

            lock (SyncRoot)
            {
                if (_current == null) return false;
                if (_current.Token != token) return false;
                if (_current.IsExplicit) return false;

                LogReleased(_current);
                _current = null;
                return true;
            }
        }

        internal static bool Extend(string token, int additionalSeconds)
        {
            lock (SyncRoot)
            {
                if (_current == null || _current.Token != token) return false;

                var newExpiry = _current.ExpiresAtUtc.AddSeconds(additionalSeconds);
                var maxExpiry = _current.AcquiredAtUtc.AddSeconds(MaxTtlSeconds);
                _current.ExpiresAtUtc = newExpiry < maxExpiry ? newExpiry : maxExpiry;
                McpLog.Info($"[EditorLock] EXTENDED holder='{_current.HolderHint}' new_expires={_current.ExpiresAtUtc:HH:mm:ss}Z");
                return true;
            }
        }

        internal static LockSnapshot GetState()
        {
            lock (SyncRoot)
            {
                EvictIfExpired();
                return _current != null ? TakeSnapshot(_current) : new LockSnapshot { Locked = false };
            }
        }

        internal static LockSnapshot ForceRelease()
        {
            lock (SyncRoot)
            {
                if (_current == null) return new LockSnapshot { Locked = false };

                var snapshot = TakeSnapshot(_current);
                McpLog.Warn($"[EditorLock] FORCE_RELEASE holder='{_current.HolderHint}' reason='{_current.Reason}' held={(DateTime.UtcNow - _current.AcquiredAtUtc).TotalMilliseconds:F0}ms");
                _current = null;
                return snapshot;
            }
        }

        internal static bool ValidateToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            lock (SyncRoot)
            {
                EvictIfExpired();
                return _current != null && _current.Token == token;
            }
        }

        internal static ErrorResponse BuildBusyResponse(LockSnapshot holder, string requestedTool)
        {
            return new ErrorResponse(BusyCode, new
            {
                holder_hint = holder.HolderHint,
                reason = holder.Reason,
                acquired_at = holder.AcquiredAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                expires_at = holder.ExpiresAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                expires_in_ms = holder.ExpiresInMs,
                retry_after_ms = DefaultRetryAfterMs,
                is_explicit = holder.IsExplicit,
                requested_tool = requestedTool,
                message = $"Unity editor locked by '{holder.HolderHint}' (reason: {holder.Reason}). Retry in {DefaultRetryAfterMs}ms or wait ~{holder.ExpiresInMs}ms."
            });
        }

        internal static ErrorResponse BuildTokenInvalidResponse(string token, string requestedTool)
        {
            return new ErrorResponse(InvalidTokenCode, new
            {
                requested_tool = requestedTool,
                message = $"editor_lock_token '{token?[..Math.Min(token?.Length ?? 0, 8)]}...' does not match current lock holder."
            });
        }

        private static void EvictIfExpired()
        {
            if (_current != null && DateTime.UtcNow >= _current.ExpiresAtUtc)
            {
                var held = (DateTime.UtcNow - _current.AcquiredAtUtc).TotalMilliseconds;
                McpLog.Warn($"[EditorLock] EXPIRED auto-released holder='{_current.HolderHint}' reason='{_current.Reason}' held={held:F0}ms");
                LogRecord(_current, "EXPIRED");
                _current = null;
            }
        }

        private static LockSnapshot TakeSnapshot(LockState state)
        {
            var now = DateTime.UtcNow;
            return new LockSnapshot
            {
                Locked = true,
                HolderHint = state.HolderHint,
                Reason = state.Reason,
                AcquiredAtUtc = state.AcquiredAtUtc,
                ExpiresAtUtc = state.ExpiresAtUtc,
                ExpiresInMs = Math.Max(0, (long)(state.ExpiresAtUtc - now).TotalMilliseconds),
                IsExplicit = state.IsExplicit,
            };
        }

        private static int ClampTtl(int requested, bool isExplicit)
        {
            if (requested > 0) return Math.Min(requested, MaxTtlSeconds);
            return isExplicit ? DefaultExplicitTtlSeconds : DefaultAutoTtlSeconds;
        }

        private static void LogAcquired(LockState state, bool reentrant)
        {
            string tag = reentrant ? "REACQUIRED" : "ACQUIRED";
            McpLog.Info($"[EditorLock] {tag} holder='{state.HolderHint}' reason='{state.Reason}' token={state.Token[..8]}... expires={state.ExpiresAtUtc:HH:mm:ss}Z explicit={state.IsExplicit}");
            LogRecord(state, tag);
        }

        private static void LogReleased(LockState state)
        {
            var held = (DateTime.UtcNow - state.AcquiredAtUtc).TotalMilliseconds;
            McpLog.Info($"[EditorLock] RELEASED holder='{state.HolderHint}' reason='{state.Reason}' held={held:F0}ms");
            LogRecord(state, "RELEASED");
        }

        private static void LogBusy(string requester, string requestedReason, LockState holder)
        {
            var expiresIn = Math.Max(0, (long)(holder.ExpiresAtUtc - DateTime.UtcNow).TotalMilliseconds);
            McpLog.Warn($"[EditorLock] BUSY requester='{requester}' reason='{requestedReason}' → holder='{holder.HolderHint}' holder_reason='{holder.Reason}' expires_in={expiresIn}ms");
            LogRecord(holder, "BUSY");
        }

        private static void LogRecord(LockState state, string eventName)
        {
            var p = new JObject
            {
                ["lock_event"] = eventName,
                ["holder_hint"] = state.HolderHint,
                ["reason"] = state.Reason,
                ["is_explicit"] = state.IsExplicit,
            };
            McpLogRecord.Log("editor_lock", p, "lock", eventName == "BUSY" ? "ERROR" : "SUCCESS", 0);
        }

        private static bool EqualsAny(string value, params string[] candidates)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (string c in candidates)
            {
                if (string.Equals(value, c, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
