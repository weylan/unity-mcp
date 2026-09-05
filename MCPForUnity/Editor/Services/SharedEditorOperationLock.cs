using System;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace MCPForUnity.Editor.Services
{
    [InitializeOnLoad]
    internal static class SharedEditorOperationLock
    {
        private const string EnvEnabledKey = "UNITY_MCP_SHARED_EDITOR_LOCK";
        private const string SessionStateKey = "MCPForUnity.SharedEditorOperationLockV2";
        internal const string BusyCode = "editor_lock_busy";
        internal const string InvalidTokenCode = "editor_lock_token_invalid";
        internal const string AttachedCode = "editor_lock_attached";
        internal const string PersistenceFailedCode = "editor_lock_persistence_failed";

        private const int DefaultAutoTtlSeconds = 60;
        private const int DefaultExplicitTtlSeconds = 120;
        private const int MaxTtlSeconds = 300;
        private const int DefaultRetryAfterMs = 3000;

        private static readonly object SyncRoot = new();
        private static LockState _current;
        private static string _persistedProjection;
        private static bool _initialized;

        internal static Func<string, bool> PersistSnapshotForTests { get; set; }

        private sealed class LockState
        {
            public string Token { get; set; }
            public string HolderHint { get; set; }
            public string Reason { get; set; }
            public DateTime AcquiredAtUtc { get; set; }
            public DateTime ExpiresAtUtc { get; set; }
            public bool IsExplicit { get; set; }
            public string AttachedJobId { get; set; }
            public long AttachedJobGeneration { get; set; }
            public string ChildToken { get; set; }
            public string ChildHolderHint { get; set; }
            public string ChildReason { get; set; }
            public DateTime ChildAcquiredAtUtc { get; set; }
            public DateTime ChildExpiresAtUtc { get; set; }

            public bool IsAttached => !string.IsNullOrEmpty(AttachedJobId);

            public LockState Clone() => new()
            {
                Token = Token,
                HolderHint = HolderHint,
                Reason = Reason,
                AcquiredAtUtc = AcquiredAtUtc,
                ExpiresAtUtc = ExpiresAtUtc,
                IsExplicit = IsExplicit,
                AttachedJobId = AttachedJobId,
                AttachedJobGeneration = AttachedJobGeneration,
                ChildToken = ChildToken,
                ChildHolderHint = ChildHolderHint,
                ChildReason = ChildReason,
                ChildAcquiredAtUtc = ChildAcquiredAtUtc,
                ChildExpiresAtUtc = ChildExpiresAtUtc,
            };
        }

        private sealed class PersistedLockState
        {
            public string token { get; set; }
            public string holder_hint { get; set; }
            public string reason { get; set; }
            public long acquired_unix_ms { get; set; }
            public long expires_unix_ms { get; set; }
            public bool is_explicit { get; set; }
            public string attached_job_id { get; set; }
            public long attached_job_generation { get; set; }
            // Parse child values only after the parent fence has been restored.
            public JToken child_token { get; set; }
            public JToken child_holder_hint { get; set; }
            public JToken child_reason { get; set; }
            public JToken child_acquired_unix_ms { get; set; }
            public JToken child_expires_unix_ms { get; set; }
        }

        static SharedEditorOperationLock()
        {
            EnsureInitialized();
        }

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

        internal static void EnsureInitialized()
        {
            lock (SyncRoot)
            {
                if (_initialized) return;
                _initialized = true;
                _current = null;
                _persistedProjection = null;

                try
                {
                    string json = SessionState.GetString(SessionStateKey, string.Empty);
                    _persistedProjection = json ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(json)) return;
                    var persisted = JsonConvert.DeserializeObject<PersistedLockState>(json);
                    if (persisted == null || string.IsNullOrWhiteSpace(persisted.token)) return;

                    var restored = new LockState
                    {
                        Token = persisted.token,
                        HolderHint = persisted.holder_hint ?? "unknown",
                        Reason = persisted.reason ?? string.Empty,
                        AcquiredAtUtc = FromUnixMs(persisted.acquired_unix_ms),
                        ExpiresAtUtc = FromUnixMs(persisted.expires_unix_ms),
                        IsExplicit = persisted.is_explicit,
                        AttachedJobId = persisted.attached_job_id,
                        AttachedJobGeneration = persisted.attached_job_generation,
                    };
                    if (restored.IsAttached)
                    {
                        try
                        {
                            string childToken = persisted.child_token?.ToObject<string>();
                            if (!string.IsNullOrWhiteSpace(childToken))
                            {
                                restored.ChildToken = childToken;
                                restored.ChildHolderHint = persisted.child_holder_hint?.ToObject<string>() ?? "unknown";
                                restored.ChildReason = persisted.child_reason?.ToObject<string>() ?? string.Empty;
                                restored.ChildAcquiredAtUtc = FromUnixMs(persisted.child_acquired_unix_ms?.ToObject<long>() ?? 0);
                                restored.ChildExpiresAtUtc = FromUnixMs(persisted.child_expires_unix_ms?.ToObject<long>() ?? 0);
                                double durationSeconds = (restored.ChildExpiresAtUtc - restored.ChildAcquiredAtUtc).TotalSeconds;
                                if (durationSeconds <= 0 || durationSeconds > MaxTtlSeconds)
                                    throw new InvalidOperationException("Persisted child lease duration is outside its allowed range.");
                            }
                        }
                        catch (Exception ex)
                        {
                            ClearChild(restored);
                            McpLog.Warn($"[EditorLock] Ignoring an invalid persisted child lease while retaining its parent fence: {ex.Message}");
                        }
                    }

                    // An attached physical owner is a fence, not a TTL lease. It survives until
                    // matching physical terminal evidence. Only unattached explicit leases expire.
                    if (!restored.IsAttached && DateTime.UtcNow >= restored.ExpiresAtUtc)
                    {
                        _current = restored;
                        EvictIfExpiredLocked();
                        return;
                    }

                    if (restored.IsAttached || restored.IsExplicit)
                    {
                        _current = restored;
                    }
                    else
                    {
                        // V2 never intentionally persists an unattached automatic lease. Treat an
                        // unexpected persisted value conservatively until it can be cleared.
                        _current = restored;
                        if (TryReplaceLocked(null)) _current = null;
                    }
                }
                catch (Exception ex)
                {
                    _current = null;
                    McpLog.Warn($"[EditorLock] Failed to restore persisted lock: {ex.Message}");
                }
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
                case "simulate_input":
                    return !EqualsAny(action, "capabilities", "release_all");
                case "manage_playmode_test":
                    return !EqualsAny(action, "status", "cancel");
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
            public bool PersistenceFailed { get; }

            private AcquireResult(
                bool acquired,
                string token,
                DateTime expiresAtUtc,
                LockSnapshot busyHolder,
                bool persistenceFailed)
            {
                Acquired = acquired;
                Token = token;
                ExpiresAtUtc = expiresAtUtc;
                BusyHolder = busyHolder;
                PersistenceFailed = persistenceFailed;
            }

            public static AcquireResult Success(string token, DateTime expiresAtUtc)
                => new(true, token, expiresAtUtc, null, false);

            public static AcquireResult Busy(LockSnapshot holder)
                => new(false, null, default, holder, false);

            public static AcquireResult PersistenceFailure(LockSnapshot holder)
                => new(false, null, default, holder, true);
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
            public bool IsAttachedToJob { get; set; }
            public string AttachedJobId { get; set; }
            public long AttachedJobGeneration { get; set; }
        }

        internal static AcquireResult TryAcquire(
            string holderHint,
            string reason,
            bool isExplicit,
            int ttlSeconds = 0,
            string existingToken = null)
        {
            if (!IsEnabled) return AcquireResult.Success(null, DateTime.MaxValue);
            EnsureInitialized();

            lock (SyncRoot)
            {
                EvictIfExpiredLocked();

                if (_current != null)
                {
                    if (!string.IsNullOrEmpty(existingToken)
                        && _current.Token == existingToken
                        && !_current.IsAttached)
                    {
                        int ttl = ClampTtl(ttlSeconds, isExplicit);
                        LockState next = _current.Clone();
                        next.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(ttl);
                        if (!string.IsNullOrEmpty(reason)) next.Reason = reason;
                        if (!TryReplaceLocked(next))
                        {
                            return AcquireResult.PersistenceFailure(TakeSnapshot(_current));
                        }
                        LogAcquired(next, reentrant: true);
                        return AcquireResult.Success(next.Token, next.ExpiresAtUtc);
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
                    Reason = reason ?? string.Empty,
                    AcquiredAtUtc = DateTime.UtcNow,
                    ExpiresAtUtc = DateTime.UtcNow.AddSeconds(effectiveTtl),
                    IsExplicit = isExplicit,
                };
                if (!TryReplaceLocked(state))
                {
                    return AcquireResult.PersistenceFailure(
                        _current != null ? TakeSnapshot(_current) : null);
                }
                LogAcquired(state, reentrant: false);
                return AcquireResult.Success(state.Token, state.ExpiresAtUtc);
            }
        }

        internal static bool AttachToJob(string token, TestJobIdentity owner)
        {
            if (!IsEnabled || string.IsNullOrEmpty(token)) return !IsEnabled;
            EnsureInitialized();
            lock (SyncRoot)
            {
                EvictIfExpiredLocked();
                if (_current == null || _current.Token != token) return false;
                if (_current.IsAttached) return IsAttachedToLocked(owner);

                LockState next = _current.Clone();
                next.AttachedJobId = owner.JobId;
                next.AttachedJobGeneration = owner.Generation;
                next.Reason = string.IsNullOrEmpty(next.Reason)
                    ? "run_tests"
                    : next.Reason;
                if (!TryReplaceLocked(next))
                {
                    return false;
                }
                McpLog.Info($"[EditorLock] ATTACHED token={token[..Math.Min(8, token.Length)]}... owner={owner}");
                return true;
            }
        }

        /// <summary>
        /// Re-establishes the physical-owner fence after lifecycle state was restored before its
        /// command lock. This is intentionally unavailable to ordinary dispatcher/tool paths.
        /// </summary>
        internal static bool AttachCurrentToJobForRecovery(TestJobIdentity owner, out string token)
        {
            token = null;
            if (!IsEnabled) return true;
            EnsureInitialized();
            lock (SyncRoot)
            {
                EvictIfExpiredLocked();
                if (_current == null) return false;
                if (_current.IsAttached)
                {
                    if (!IsAttachedToLocked(owner)) return false;
                    token = _current.Token;
                    return true;
                }

                LockState next = _current.Clone();
                next.AttachedJobId = owner.JobId;
                next.AttachedJobGeneration = owner.Generation;
                if (!TryReplaceLocked(next))
                {
                    return false;
                }

                token = next.Token;
                McpLog.Warn($"[EditorLock] Reattached restored physical owner {owner} to the current command lock.");
                return true;
            }
        }

        internal static bool ValidateAttachedToken(string token, TestJobIdentity owner)
        {
            if (!IsEnabled) return true;
            EnsureInitialized();
            lock (SyncRoot)
            {
                return _current != null
                       && _current.Token == token
                       && IsAttachedToLocked(owner);
            }
        }

        internal static bool TryGetMatchingAttachedToken(string token, out LockSnapshot snapshot)
        {
            snapshot = null;
            if (!IsEnabled || string.IsNullOrEmpty(token)) return false;
            EnsureInitialized();
            lock (SyncRoot)
            {
                if (_current == null || _current.Token != token || !_current.IsAttached) return false;
                snapshot = TakeSnapshot(_current);
                return true;
            }
        }

        internal static bool TryAcquireTestChild(
            TestJobIdentity owner,
            string holderHint,
            string reason,
            int ttlSeconds,
            DateTime nowUtc,
            out string token,
            out DateTime expiresAtUtc)
        {
            token = null;
            expiresAtUtc = default;
            if (!IsEnabled) return true;
            EnsureInitialized();
            lock (SyncRoot)
            {
                if (_current == null || !IsAttachedToLocked(owner)) return false;
                if (!ClearExpiredChildLocked(nowUtc)) return false;
                if (!string.IsNullOrEmpty(_current.ChildToken)) return false;

                int ttl = ClampTtl(ttlSeconds, isExplicit: false);
                LockState next = _current.Clone();
                next.ChildToken = Guid.NewGuid().ToString("N");
                next.ChildHolderHint = holderHint ?? "unknown";
                next.ChildReason = reason ?? string.Empty;
                next.ChildAcquiredAtUtc = nowUtc;
                next.ChildExpiresAtUtc = nowUtc.AddSeconds(ttl);
                if (!TryReplaceLocked(next)) return false;

                token = next.ChildToken;
                expiresAtUtc = next.ChildExpiresAtUtc;
                McpLog.Info(
                    $"[EditorLock] CHILD_ACQUIRED holder='{next.ChildHolderHint}' "
                    + $"reason='{next.ChildReason}' owner={owner} expires={next.ChildExpiresAtUtc:HH:mm:ss}Z");
                return true;
            }
        }

        internal static bool ValidateAndExtendTestChild(
            string token,
            TestJobIdentity owner,
            int additionalSeconds,
            DateTime nowUtc)
        {
            if (!IsEnabled) return true;
            if (string.IsNullOrEmpty(token)) return false;
            EnsureInitialized();
            lock (SyncRoot)
            {
                if (_current == null || !IsAttachedToLocked(owner)) return false;
                if (!ClearExpiredChildLocked(nowUtc)) return false;
                if (!string.Equals(_current.ChildToken, token, StringComparison.Ordinal)) return false;
                if (additionalSeconds <= 0) return true;

                LockState next = _current.Clone();
                DateTime requestedExpiry = next.ChildExpiresAtUtc.AddSeconds(
                    Math.Min(additionalSeconds, MaxTtlSeconds));
                DateTime maxExpiry = next.ChildAcquiredAtUtc.AddSeconds(MaxTtlSeconds);
                next.ChildExpiresAtUtc = requestedExpiry < maxExpiry ? requestedExpiry : maxExpiry;
                if (!TryReplaceLocked(next)) return false;
                McpLog.Info(
                    $"[EditorLock] CHILD_EXTENDED holder='{next.ChildHolderHint}' "
                    + $"owner={owner} expires={next.ChildExpiresAtUtc:HH:mm:ss}Z");
                return true;
            }
        }

        internal static bool ReleaseTestChild(
            string token,
            TestJobIdentity owner,
            DateTime nowUtc)
        {
            if (!IsEnabled) return true;
            if (string.IsNullOrEmpty(token)) return false;
            EnsureInitialized();
            lock (SyncRoot)
            {
                if (_current == null || !IsAttachedToLocked(owner)) return false;
                if (!ClearExpiredChildLocked(nowUtc)) return false;
                if (!string.Equals(_current.ChildToken, token, StringComparison.Ordinal)) return false;

                string holder = _current.ChildHolderHint;
                LockState next = _current.Clone();
                ClearChild(next);
                if (!TryReplaceLocked(next)) return false;
                McpLog.Info($"[EditorLock] CHILD_RELEASED holder='{holder}' owner={owner}");
                return true;
            }
        }

        internal static bool CompleteAttachedJob(TestJobIdentity owner)
        {
            return FinishAttachment(owner);
        }

        internal static bool AbortAttachedJob(TestJobIdentity owner)
        {
            return FinishAttachment(owner);
        }

        private static bool FinishAttachment(TestJobIdentity owner)
        {
            if (!IsEnabled) return true;
            EnsureInitialized();
            lock (SyncRoot)
            {
                if (_current == null || !IsAttachedToLocked(owner)) return false;
                LockState next = _current.Clone();
                ClearChild(next);
                if (_current.IsExplicit && DateTime.UtcNow < _current.ExpiresAtUtc)
                {
                    next.AttachedJobId = null;
                    next.AttachedJobGeneration = 0;
                }
                else
                {
                    next = null;
                }

                LockState released = _current;
                if (!TryReplaceLocked(next))
                {
                    return false;
                }
                if (next == null) LogReleased(released);
                return true;
            }
        }

        internal static bool Release(string token)
        {
            if (!IsEnabled) return true;
            EnsureInitialized();
            lock (SyncRoot)
            {
                if (_current == null || _current.Token != token || _current.IsAttached) return false;
                LockState released = _current;
                if (!TryReplaceLocked(null)) return false;
                LogReleased(released);
                return true;
            }
        }

        internal static bool Reenter(string token, string reason, int ttlSeconds = 0)
        {
            if (!IsEnabled) return true;
            if (string.IsNullOrEmpty(token)) return false;
            EnsureInitialized();
            lock (SyncRoot)
            {
                EvictIfExpiredLocked();
                if (_current == null || _current.Token != token || _current.IsAttached) return false;

                int ttl = ClampTtl(ttlSeconds, _current.IsExplicit);
                LockState next = _current.Clone();
                next.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(ttl);
                if (!string.IsNullOrEmpty(reason)) next.Reason = reason;
                if (!TryReplaceLocked(next)) return false;
                LogAcquired(next, reentrant: true);
                return true;
            }
        }

        internal static bool ReleaseIfAutoLock(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            EnsureInitialized();
            lock (SyncRoot)
            {
                if (_current == null || _current.Token != token || _current.IsExplicit || _current.IsAttached)
                    return false;

                LockState released = _current;
                if (!TryReplaceLocked(null)) return false;
                LogReleased(released);
                return true;
            }
        }

        internal static bool Extend(string token, int additionalSeconds)
        {
            EnsureInitialized();
            lock (SyncRoot)
            {
                if (_current == null || _current.Token != token || _current.IsAttached) return false;

                var newExpiry = _current.ExpiresAtUtc.AddSeconds(additionalSeconds);
                var maxExpiry = _current.AcquiredAtUtc.AddSeconds(MaxTtlSeconds);
                LockState next = _current.Clone();
                next.ExpiresAtUtc = newExpiry < maxExpiry ? newExpiry : maxExpiry;
                if (!TryReplaceLocked(next)) return false;
                McpLog.Info($"[EditorLock] EXTENDED holder='{next.HolderHint}' new_expires={next.ExpiresAtUtc:HH:mm:ss}Z");
                return true;
            }
        }

        internal static LockSnapshot GetState()
        {
            EnsureInitialized();
            lock (SyncRoot)
            {
                EvictIfExpiredLocked();
                return _current != null ? TakeSnapshot(_current) : new LockSnapshot { Locked = false };
            }
        }

        internal static bool TryForceRelease(out LockSnapshot snapshot)
        {
            EnsureInitialized();
            lock (SyncRoot)
            {
                if (_current == null)
                {
                    snapshot = new LockSnapshot { Locked = false };
                    return true;
                }

                snapshot = TakeSnapshot(_current);
                if (_current.IsAttached)
                {
                    return false;
                }

                LockState released = _current;
                if (!TryReplaceLocked(null)) return false;
                McpLog.Warn($"[EditorLock] FORCE_RELEASE holder='{released.HolderHint}' reason='{released.Reason}' held={(DateTime.UtcNow - released.AcquiredAtUtc).TotalMilliseconds:F0}ms");
                return true;
            }
        }

        internal static LockSnapshot ForceRelease()
        {
            TryForceRelease(out LockSnapshot snapshot);
            return snapshot;
        }

        internal static bool ValidateToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            EnsureInitialized();
            lock (SyncRoot)
            {
                EvictIfExpiredLocked();
                return _current != null && _current.Token == token && !_current.IsAttached;
            }
        }

        internal static ErrorResponse BuildBusyResponse(LockSnapshot holder, string requestedTool)
        {
            holder ??= new LockSnapshot { Locked = true };
            return new ErrorResponse(BusyCode, new
            {
                holder_hint = holder.HolderHint,
                reason = holder.Reason,
                acquired_at = holder.AcquiredAtUtc == default ? null : holder.AcquiredAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                expires_at = holder.ExpiresAtUtc == default ? null : holder.ExpiresAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                expires_in_ms = holder.ExpiresInMs,
                retry_after_ms = DefaultRetryAfterMs,
                is_explicit = holder.IsExplicit,
                attached_job_id = holder.AttachedJobId,
                attached_job_generation = holder.AttachedJobGeneration,
                fence_active = holder.IsAttachedToJob,
                requested_tool = requestedTool,
                message = holder.IsAttachedToJob
                    ? $"Unity editor is fenced by physical test owner '{holder.AttachedJobId}'. Wait for terminal state or restart Unity."
                    : $"Unity editor locked by '{holder.HolderHint}' (reason: {holder.Reason}). Retry in {DefaultRetryAfterMs}ms or wait ~{holder.ExpiresInMs}ms."
            });
        }

        internal static ErrorResponse BuildTokenInvalidResponse(string token, string requestedTool)
        {
            return new ErrorResponse(InvalidTokenCode, new
            {
                requested_tool = requestedTool,
                message = $"editor_lock_token '{token?[..Math.Min(token?.Length ?? 0, 8)]}...' does not match an unattached current lock holder."
            });
        }

        internal static ErrorResponse BuildAttachedResponse(LockSnapshot holder, string requestedTool)
        {
            holder ??= new LockSnapshot { Locked = true, IsAttachedToJob = true };
            return new ErrorResponse(AttachedCode, new
            {
                requested_tool = requestedTool,
                physical_owner_job_id = holder.AttachedJobId,
                physical_owner_generation = holder.AttachedJobGeneration,
                fence_active = true,
                safe_to_start_new_run = false,
                restart_required_if_orphaned = true,
                message = "Attached TestRunner ownership cannot be reused or released before matching physical terminal evidence."
            });
        }

        internal static ErrorResponse BuildPersistenceFailedResponse(string requestedTool)
        {
            return new ErrorResponse(PersistenceFailedCode, new
            {
                requested_tool = requestedTool,
                retry_after_ms = DefaultRetryAfterMs,
                message = "The editor lock state could not be persisted; the mutation was rolled back."
            });
        }

        internal static void ClearInMemoryForTests()
        {
            lock (SyncRoot)
            {
                _current = null;
                _initialized = false;
                _persistedProjection = null;
            }
        }

        internal static void ResetForTests(bool clearSessionState)
        {
            lock (SyncRoot)
            {
                _current = null;
                _initialized = false;
                _persistedProjection = null;
                PersistSnapshotForTests = null;
                if (clearSessionState)
                {
                    SessionState.SetString(SessionStateKey, string.Empty);
                }
            }
        }

        private static bool IsAttachedToLocked(TestJobIdentity owner)
        {
            return _current != null
                   && string.Equals(_current.AttachedJobId, owner.JobId, StringComparison.Ordinal)
                   && _current.AttachedJobGeneration == owner.Generation;
        }

        private static bool ClearExpiredChildLocked(DateTime nowUtc)
        {
            if (_current == null
                || string.IsNullOrEmpty(_current.ChildToken)
                || nowUtc < _current.ChildExpiresAtUtc)
            {
                return true;
            }

            string holder = _current.ChildHolderHint;
            LockState next = _current.Clone();
            ClearChild(next);
            if (!TryReplaceLocked(next)) return false;
            McpLog.Warn($"[EditorLock] CHILD_EXPIRED holder='{holder}'");
            return true;
        }

        private static void ClearChild(LockState state)
        {
            if (state == null) return;
            state.ChildToken = null;
            state.ChildHolderHint = null;
            state.ChildReason = null;
            state.ChildAcquiredAtUtc = default;
            state.ChildExpiresAtUtc = default;
        }

        private static void EvictIfExpiredLocked()
        {
            if (_current == null || _current.IsAttached || DateTime.UtcNow < _current.ExpiresAtUtc) return;
            LockState expired = _current;
            if (!TryReplaceLocked(null))
            {
                McpLog.Warn("[EditorLock] Expired lock could not be cleared durably; retaining the in-memory fence.");
                return;
            }
            var held = (DateTime.UtcNow - expired.AcquiredAtUtc).TotalMilliseconds;
            McpLog.Warn($"[EditorLock] EXPIRED auto-released holder='{expired.HolderHint}' reason='{expired.Reason}' held={held:F0}ms");
            LogRecord(expired, "EXPIRED");
        }

        private static bool TryReplaceLocked(LockState next)
        {
            LockState prior = _current;
            _current = next;
            if (PersistLocked()) return true;
            _current = prior;
            return false;
        }

        private static bool PersistLocked()
        {
            try
            {
                string projection;
                if (_current == null || (!_current.IsExplicit && !_current.IsAttached))
                {
                    projection = string.Empty;
                }
                else
                {
                    var persisted = new PersistedLockState
                    {
                        token = _current.Token,
                        holder_hint = _current.HolderHint,
                        reason = _current.Reason,
                        acquired_unix_ms = ToUnixMs(_current.AcquiredAtUtc),
                        expires_unix_ms = ToUnixMs(_current.ExpiresAtUtc),
                        is_explicit = _current.IsExplicit,
                        attached_job_id = _current.AttachedJobId,
                        attached_job_generation = _current.AttachedJobGeneration,
                        child_token = _current.ChildToken,
                        child_holder_hint = _current.ChildHolderHint,
                        child_reason = _current.ChildReason,
                        child_acquired_unix_ms = string.IsNullOrEmpty(_current.ChildToken)
                            ? 0
                            : ToUnixMs(_current.ChildAcquiredAtUtc),
                        child_expires_unix_ms = string.IsNullOrEmpty(_current.ChildToken)
                            ? 0
                            : ToUnixMs(_current.ChildExpiresAtUtc),
                    };
                    projection = JsonConvert.SerializeObject(persisted);
                }

                if (_persistedProjection != null
                    && string.Equals(_persistedProjection, projection, StringComparison.Ordinal))
                {
                    return true;
                }
                if (PersistSnapshotForTests != null && !PersistSnapshotForTests(projection))
                {
                    throw new InvalidOperationException("Injected SessionState persistence failure.");
                }
                SessionState.SetString(SessionStateKey, projection);
                _persistedProjection = projection;
                return true;
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[EditorLock] Failed to persist lock: {ex.Message}");
                return false;
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
                ExpiresInMs = state.IsAttached
                    ? long.MaxValue
                    : Math.Max(0, (long)(state.ExpiresAtUtc - now).TotalMilliseconds),
                IsExplicit = state.IsExplicit,
                IsAttachedToJob = state.IsAttached,
                AttachedJobId = state.AttachedJobId,
                AttachedJobGeneration = state.AttachedJobGeneration,
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
            var expiresIn = holder.IsAttached
                ? long.MaxValue
                : Math.Max(0, (long)(holder.ExpiresAtUtc - DateTime.UtcNow).TotalMilliseconds);
            McpLog.Info($"[GuardedNotice] [EditorLock] BUSY requester='{requester}' reason='{requestedReason}' -> holder='{holder.HolderHint}' holder_reason='{holder.Reason}' expires_in={expiresIn}ms attached={holder.IsAttached}.");
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
                ["attached_job_id"] = state.AttachedJobId,
                ["attached_job_generation"] = state.AttachedJobGeneration,
            };
            McpLogRecord.Log("editor_lock", p, "lock", eventName == "BUSY" ? "ERROR" : "SUCCESS", 0);
        }

        private static bool EqualsAny(string value, params string[] candidates)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (string candidate in candidates)
            {
                if (string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static long ToUnixMs(DateTime value)
            => new DateTimeOffset(value.ToUniversalTime()).ToUnixTimeMilliseconds();

        private static DateTime FromUnixMs(long value)
            => DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime;
    }
}
