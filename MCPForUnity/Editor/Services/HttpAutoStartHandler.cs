using System;
using System.Threading.Tasks;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services.Transport;
using MCPForUnity.Editor.Windows;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Automatically starts the HTTP MCP bridge on editor load when the user has opted in
    /// via the "Auto-Start on Editor Load" toggle in Advanced Settings.
    /// This complements HttpBridgeReloadHandler (which only resumes after domain reloads).
    ///
    /// Two responsibilities live here and must not stomp on each other:
    ///  - A reload-safe one-shot auto-start (upstream #1229 fix): WaitForEditorReady drives the
    ///    TickCore state machine and never writes the session latch until the start work actually
    ///    dispatches, so a domain reload that includes a compile can no longer consume the
    ///    once-per-session auto-start. ConnectPendingKey lets a reload-interrupted connect resume
    ///    without re-spawning the server.
    ///  - A continuous "connect to an already-running local server" poller (private fork feature):
    ///    OnEditorUpdate periodically calls TryConnectIfLocalServerReachableAsync so an editor that
    ///    launches against a pre-started local HTTP server attaches on its own, and restarts a stale
    ///    local bridge. Auto-start defaults ON (EditorPrefDefaults.AutoStartOnLoad) for this workflow.
    ///
    /// _autoConnectInProgress is a single in-AppDomain mutex shared by BOTH paths so at most one
    /// connect attempt runs at a time; it is acquired in the synchronous decision methods and released
    /// in the dispatched async task's finally (never re-acquired inside the async body). ConnectPendingKey
    /// is the orthogonal cross-domain "a connect was in flight" marker; the two are not interchangeable.
    /// </summary>
    [InitializeOnLoad]
    internal static class HttpAutoStartHandler
    {
        internal const string SessionInitKey = "HttpAutoStartHandler.SessionInitialized";

        // Set while a connect is in flight. A domain reload kills the in-flight task but leaves this
        // set, so the next domain load can finish the connect phase without re-spawning the server
        // (StartLocalHttpServer would first stop a still-booting process).
        internal const string ConnectPendingKey = "HttpAutoStartHandler.ConnectPending";

        // Fork: set when the user manually stops the session, so the external-server poller and the
        // reconnect/wait paths do not immediately revive a bridge the user just took down.
        private const string ManualSessionStopSuppressedKey = "HttpAutoStartHandler.ManualSessionStopSuppressed";

        // Fork: external-server poll cadence + a cooldown after a failed connect so we don't churn.
        private const double ExternalServerPollIntervalSeconds = 2.0;
        private const double FailedConnectCooldownSeconds = 15.0;
        private static double _lastExternalServerPollTime;
        private static double _lastFailedConnectTime = double.NegativeInfinity;
        private static string _lastFailedConnectBaseUrl;

        // Fork: single in-AppDomain mutex shared by the one-shot auto-start, the reload reconnect,
        // and the external-server poller. Acquired in the sync decision methods; released only in the
        // dispatched async task's finally. Reset to false on every domain reload (plain static).
        private static bool _autoConnectInProgress;
        private static bool _editorUpdateRegistered;

        // Upstream: bounds the per-frame retry when editor services keep throwing on a fresh launch.
        // Plain static, so every domain reload grants a fresh budget.
        private const int MaxServiceNotReadyRetries = 300;
        private static int _serviceNotReadyRetries;

        internal enum TickDecision
        {
            DeferBusy,
            DeferToResume,
            Skip,
            ShouldStart,
            ShouldReconnect,
        }

        static HttpAutoStartHandler()
        {
            if (Application.isBatchMode &&
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UNITY_MCP_ALLOW_BATCH")))
            {
                return;
            }

            // Fork: arm (or disarm) the continuous external-server poller BEFORE the one-shot
            // early-returns below. It re-evaluates on every domain load, so it must run every load
            // regardless of the latch — otherwise polling silently vanishes after the first reload.
            RefreshExternalServerPolling();

            bool latched = SessionState.GetBool(SessionInitKey, false);
            bool connectPending = SessionState.GetBool(ConnectPendingKey, false);

            // Cheap pre-check so the common case (auto-start off, nothing pending) costs one
            // EditorPrefs read per domain load instead of an update subscription. The pref is
            // re-read every domain load, so enabling it takes effect at the next reload.
            if (!latched && !connectPending && !IsAutoStartOnLoadEnabled())
            {
                return;
            }

            // Latched with nothing pending: this session already auto-started.
            if (latched && !connectPending)
            {
                return;
            }

            // Pending EditorApplication.delayCall/update delegates are wiped by domain reloads,
            // so the deferred work must NOT latch up front — latching eagerly killed auto-start
            // for the whole session whenever startup included a compile (#1229). An update tick
            // is reload-safe because this ctor re-arms it on every domain load.
            EditorApplication.update += WaitForEditorReady;
        }

        /// <summary>
        /// Drops a reload-interrupted auto-start connect. Called when the user takes manual
        /// control of the bridge lifecycle, so no later domain load revives the connect. Only
        /// erases the marker — it does not cancel an already-running Task, which exits and
        /// releases the guard via its own finally.
        /// </summary>
        internal static void CancelPendingReconnect() => SessionState.EraseBool(ConnectPendingKey);

        private static void WaitForEditorReady()
        {
            switch (TickCore(HttpBridgeReloadHandler.IsEditorBusy()))
            {
                case TickDecision.DeferBusy:
                case TickDecision.DeferToResume:
                    return; // stay registered, try again next tick

                case TickDecision.Skip:
                    EditorApplication.update -= WaitForEditorReady;
                    return;

                case TickDecision.ShouldStart:
                    // The external-server poller may hold the connect this frame. Stay subscribed
                    // and retry next tick WITHOUT writing the latch or consuming the not-ready
                    // budget — the guard is acquired in TryBeginAutoStart, never after latching.
                    if (_autoConnectInProgress) return;
                    if (!TryBeginAutoStart())
                    {
                        DeferOrGiveUp();
                        return;
                    }
                    SessionState.SetBool(SessionInitKey, true);
                    EditorApplication.update -= WaitForEditorReady;
                    return;

                case TickDecision.ShouldReconnect:
                    if (_autoConnectInProgress) return;
                    if (!TryBeginReconnect())
                    {
                        DeferOrGiveUp();
                        return;
                    }
                    EditorApplication.update -= WaitForEditorReady;
                    return;
            }
        }

        // Services may not be initialized on the first frames of a fresh launch; retry next
        // tick, but not forever — a persistently broken environment shouldn't churn exceptions
        // every frame for the whole session. A later domain reload retries with a fresh budget.
        private static void DeferOrGiveUp()
        {
            if (++_serviceNotReadyRetries < MaxServiceNotReadyRetries) return;
            EditorApplication.update -= WaitForEditorReady;
            McpLog.Warn("[HTTP Auto-Start] Editor services unavailable; giving up until the next domain reload");
        }

        /// <summary>
        /// Decision core for the editor-ready tick, separated so EditMode tests can drive it
        /// without spawning servers. Never writes the session latch — the caller latches only
        /// after the start work actually dispatches.
        /// </summary>
        internal static TickDecision TickCore(bool editorBusy)
        {
            if (editorBusy) return TickDecision.DeferBusy;

            bool connectPending = SessionState.GetBool(ConnectPendingKey, false);
            if (!connectPending)
            {
                if (SessionState.GetBool(SessionInitKey, false)) return TickDecision.Skip;

                // Only check lightweight EditorPrefs here — heavier services are touched in
                // TryBeginAutoStart once the editor is idle. No latch when disabled: the pref
                // is re-read on the next domain load.
                if (!IsAutoStartOnLoadEnabled()) return TickDecision.Skip;
            }

            // A pending reload-resume owns bridge revival — checked only when we would
            // otherwise act, so a plain Skip never waits out the resume window.
            if (HttpBridgeReloadHandler.IsResumePending) return TickDecision.DeferToResume;

            return connectPending ? TickDecision.ShouldReconnect : TickDecision.ShouldStart;
        }

        /// <summary>
        /// Returns true when the auto-start decision was actually made (including deliberate
        /// early-outs). Returns false when services were not ready yet, so the caller leaves
        /// the session latch unset and retries instead of consuming it. Acquires the connect
        /// guard before dispatching; AutoStartAsync releases it in its finally.
        /// </summary>
        private static bool TryBeginAutoStart()
        {
            try
            {
                if (!EditorConfigurationCache.Instance.UseHttpTransport) return true;

                // Don't auto-start if bridge is already running.
                if (MCPServiceLocator.TransportManager.IsRunning(TransportMode.Http)) return true;

                // Guaranteed free because WaitForEditorReady gated on !_autoConnectInProgress on the
                // same synchronous tick; defensive false keeps the latch unset if it ever is busy.
                if (!TryBeginAutoConnect()) return false;

                SessionState.SetBool(ConnectPendingKey, true);
                _ = AutoStartAsync();
                return true;
            }
            catch (Exception ex)
            {
                McpLog.Debug($"[HTTP Auto-Start] Services not ready: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Returns false when services were not ready yet (caller retries). On true the
        /// pending reconnect was either dispatched or deliberately dropped (auto-start
        /// disabled, transport switched, bridge already running). When it dispatches it acquires
        /// the connect guard; ReconnectAsync releases it in its finally.
        /// </summary>
        internal static bool TryBeginReconnect()
        {
            bool proceed;
            try
            {
                proceed = IsAutoStartOnLoadEnabled()
                    && EditorConfigurationCache.Instance.UseHttpTransport
                    && !MCPServiceLocator.TransportManager.IsRunning(TransportMode.Http);
            }
            catch (Exception ex)
            {
                McpLog.Debug($"[HTTP Auto-Start] Services not ready: {ex.Message}");
                return false;
            }

            if (!proceed)
            {
                SessionState.EraseBool(ConnectPendingKey);
                return true;
            }

            // ConnectPendingKey is already set (that is why we are reconnecting); just guard the
            // dispatch. Guaranteed free because WaitForEditorReady gated on !_autoConnectInProgress.
            if (!TryBeginAutoConnect()) return false;
            _ = ReconnectAsync();
            return true;
        }

        internal static void RefreshExternalServerPolling()
        {
            bool shouldRegister = ShouldPollExternalLocalServer();
            if (shouldRegister && !_editorUpdateRegistered)
            {
                EditorApplication.update += OnEditorUpdate;
                _editorUpdateRegistered = true;
            }
            else if (!shouldRegister && _editorUpdateRegistered)
            {
                EditorApplication.update -= OnEditorUpdate;
                _editorUpdateRegistered = false;
            }
        }

        internal static bool IsAutoStartOnLoadEnabled()
        {
            return EditorPrefs.GetBool(EditorPrefKeys.AutoStartOnLoad, EditorPrefDefaults.AutoStartOnLoad);
        }

        private static bool ShouldPollExternalLocalServer()
        {
            try
            {
                // Same batch-mode gate as the static ctor. RefreshExternalServerPolling is public
                // (real callers in McpConnectionSection/McpAdvancedSection), so without this a caller
                // could arm polling in a headless batch editor that never opted in via UNITY_MCP_ALLOW_BATCH.
                if (Application.isBatchMode &&
                    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UNITY_MCP_ALLOW_BATCH")))
                {
                    return false;
                }

                if (!IsAutoStartOnLoadEnabled()) return false;
                if (!EditorPrefs.GetBool(EditorPrefKeys.UseHttpTransport, true)) return false;

                string scope = EditorPrefs.GetString(EditorPrefKeys.HttpTransportScope, string.Empty);
                if (string.Equals(scope, "remote", StringComparison.OrdinalIgnoreCase)) return false;

                return true;
            }
            catch (Exception ex)
            {
                McpLog.Debug($"[HTTP Auto-Start] Polling eligibility check failed: {ex.Message}");
                return false;
            }
        }

        private static void OnEditorUpdate()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastExternalServerPollTime < ExternalServerPollIntervalSeconds)
            {
                return;
            }
            _lastExternalServerPollTime = now;

            // Yield to the one-shot auto-start / reload-resume machinery. Never poll while a connect
            // is in flight, a reload-interrupted reconnect is pending, the reload handler is resuming,
            // or the editor is busy — otherwise the stale-bridge check in the poll below could tear
            // down a bridge one of those paths just brought up.
            if (_autoConnectInProgress) return;
            if (SessionState.GetBool(ConnectPendingKey, false)) return;
            if (HttpBridgeReloadHandler.IsResumePending) return;
            if (HttpBridgeReloadHandler.IsEditorBusy()) return;

            if (!CanAttemptExternalLocalConnect(logPolicyError: false, allowRunningBridge: true))
            {
                return;
            }

            _ = PollExternalServerAsync();
        }

        private static async Task PollExternalServerAsync()
        {
            if (!TryBeginAutoConnect())
            {
                return;
            }

            try
            {
                await TryConnectIfLocalServerReachableAsync();
            }
            finally
            {
                EndAutoConnect();
            }
        }

        private static bool TryBeginAutoConnect()
        {
            if (_autoConnectInProgress)
            {
                return false;
            }

            _autoConnectInProgress = true;
            return true;
        }

        private static void EndAutoConnect()
        {
            _autoConnectInProgress = false;
        }

        internal static void RecordManualSessionStop()
        {
            SessionState.SetBool(ManualSessionStopSuppressedKey, true);
            // A manual stop during an in-flight auto-start would otherwise leave ConnectPendingKey
            // set, and the next domain load's ShouldReconnect would revive the connect the user
            // just stopped. Drop the pending marker so suppression actually holds across reloads.
            CancelPendingReconnect();
        }

        internal static void ClearManualSessionStopSuppression()
        {
            SessionState.SetBool(ManualSessionStopSuppressedKey, false);
        }

        internal static void ClearExternalConnectFailureCooldown()
        {
            _lastFailedConnectTime = double.NegativeInfinity;
            _lastFailedConnectBaseUrl = null;
        }

        private static bool IsManualSessionStopSuppressed()
        {
            return SessionState.GetBool(ManualSessionStopSuppressedKey, false);
        }

        private static bool CanAttemptExternalLocalConnect(bool logPolicyError, bool allowRunningBridge = false)
        {
            if (!IsAutoStartOnLoadEnabled()) return false;
            if (!EditorConfigurationCache.Instance.UseHttpTransport) return false;
            if (HttpEndpointUtility.IsRemoteScope()) return false;
            if (!allowRunningBridge && MCPServiceLocator.Bridge.IsRunning) return false;
            if (IsManualSessionStopSuppressed()) return false;

            string localBaseUrl = HttpEndpointUtility.GetLocalBaseUrl();
            if (IsFailureCooldownActive(localBaseUrl)) return false;

            if (!HttpEndpointUtility.IsHttpLocalUrlAllowedForLaunch(localBaseUrl, out string policyError))
            {
                if (logPolicyError)
                {
                    McpLog.Debug($"[HTTP Auto-Start] Local URL blocked by security policy: {policyError}");
                }
                return false;
            }

            return true;
        }

        private static bool IsFailureCooldownActive(string localBaseUrl)
        {
            if (string.IsNullOrEmpty(_lastFailedConnectBaseUrl))
            {
                return false;
            }

            if (!string.Equals(_lastFailedConnectBaseUrl, localBaseUrl, StringComparison.Ordinal))
            {
                ClearExternalConnectFailureCooldown();
                return false;
            }

            double elapsed = EditorApplication.timeSinceStartup - _lastFailedConnectTime;
            return elapsed >= 0 && elapsed < FailedConnectCooldownSeconds;
        }

        private static void RecordExternalConnectFailure()
        {
            _lastFailedConnectBaseUrl = HttpEndpointUtility.GetLocalBaseUrl();
            _lastFailedConnectTime = EditorApplication.timeSinceStartup;
        }

        private static async Task AutoStartAsync()
        {
            // The connect guard and ConnectPendingKey were taken by TryBeginAutoStart; this method
            // owns releasing both and must NOT re-acquire the guard.
            try
            {
                bool isLocal = !HttpEndpointUtility.IsRemoteScope();

                if (isLocal)
                {
                    // For HTTP Local: launch the server process first, then connect the bridge.
                    // This mirrors what the UI "Start Server" button does.
                    if (!HttpEndpointUtility.IsHttpLocalUrlAllowedForLaunch(
                            HttpEndpointUtility.GetLocalBaseUrl(), out string policyError))
                    {
                        McpLog.Debug($"[HTTP Auto-Start] Local URL blocked by security policy: {policyError}");
                        return;
                    }

                    // Check if server is already reachable (e.g. user started it externally).
                    if (!MCPServiceLocator.Server.IsLocalHttpServerReachable())
                    {
                        bool serverStarted = MCPServiceLocator.Server.StartLocalHttpServer(quiet: true);
                        if (!serverStarted)
                        {
                            McpLog.Warn("[HTTP Auto-Start] Failed to start local HTTP server");
                            return;
                        }
                        ClearExternalConnectFailureCooldown();
                    }

                    // Wait for the server to become reachable, then connect.
                    await WaitForServerAndConnectAsync();
                }
                else
                {
                    // For HTTP Remote: server is external, just connect the bridge.
                    await ConnectBridgeAsync();
                }
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[HTTP Auto-Start] Failed: {ex.Message}");
            }
            finally
            {
                EndAutoConnect();
                // Reached on every terminal outcome. A domain reload that kills the task
                // mid-flight skips this, leaving the key set for the reconnect path.
                SessionState.EraseBool(ConnectPendingKey);
            }
        }

        /// <summary>
        /// Finishes an auto-start whose connect phase was killed by a domain reload.
        /// Connect-only: never spawns a server — the previous domain already did. The connect
        /// guard was taken by TryBeginReconnect; this method releases it (and the pending marker).
        /// </summary>
        private static async Task ReconnectAsync()
        {
            try
            {
                if (HttpEndpointUtility.IsRemoteScope())
                {
                    await ConnectBridgeAsync();
                    return;
                }

                await WaitForServerAndConnectAsync();
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[HTTP Auto-Start] Post-reload reconnect failed: {ex.Message}");
            }
            finally
            {
                EndAutoConnect();
                SessionState.EraseBool(ConnectPendingKey);
            }
        }

        /// <summary>
        /// Attempts to connect to an already-running local HTTP server (fork feature). Unlike the
        /// one-shot auto-start it never launches a server — the poller only attaches to a server
        /// that is already reachable, and it restarts a stale local bridge before reconnecting.
        /// </summary>
        internal static async Task<bool> TryConnectIfLocalServerReachableAsync()
        {
            try
            {
                if (!CanAttemptExternalLocalConnect(logPolicyError: true, allowRunningBridge: true)) return false;

                bool canStartBridge = await StopStaleBridgeIfNeededAsync();
                if (!canStartBridge)
                {
                    return false;
                }

                // The verify/stop awaits above yield; re-check the user has not manually stopped
                // since CanAttempt so we don't revive a bridge they just took down.
                if (IsManualSessionStopSuppressed())
                {
                    return false;
                }

                bool reachable = MCPServiceLocator.Server.IsLocalHttpServerReachable();
                if (!reachable)
                {
                    return false;
                }

                bool started = await MCPServiceLocator.Bridge.StartAsync();
                if (!started)
                {
                    RecordExternalConnectFailure();
                    return false;
                }

                ClearManualSessionStopSuppression();
                ClearExternalConnectFailureCooldown();
                McpLog.Info("[HTTP Auto-Start] Connected to reachable local HTTP server");
                MCPForUnityEditorWindow.RequestHealthVerification();
                return true;
            }
            catch (Exception ex)
            {
                RecordExternalConnectFailure();
                McpLog.Debug($"[HTTP Auto-Start] Local server connect check failed: {ex.Message}");
                return false;
            }
        }

        private static async Task<bool> StopStaleBridgeIfNeededAsync()
        {
            var bridge = MCPServiceLocator.Bridge;
            if (!bridge.IsRunning)
            {
                return true;
            }

            BridgeVerificationResult verification = null;
            try
            {
                verification = await bridge.VerifyAsync();
            }
            catch (Exception ex)
            {
                McpLog.Debug($"[HTTP Auto-Start] Existing bridge verification threw: {ex.Message}");
            }

            if (verification != null && verification.Success)
            {
                return false;
            }

            string reason = verification?.Message;
            string suffix = string.IsNullOrWhiteSpace(reason) ? string.Empty : $": {reason}";
            McpLog.Info($"[GuardedNotice] [HTTP Auto-Start] Existing local HTTP bridge is stale; restarting session{suffix}");

            try
            {
                await bridge.StopAsync();
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[HTTP Auto-Start] Failed to stop stale bridge before reconnect: {ex.Message}");
            }

            return true;
        }

        /// <summary>
        /// Waits for the local HTTP server to accept connections, then connects the bridge.
        /// Mirrors TryAutoStartSessionAsync in McpConnectionSection: while a managed launch
        /// process is alive, keep polling reachability and declare failure only when it exits
        /// without the port coming up. Without a launch handle (post-reload reconnect, or a
        /// server started externally) there is nothing to watch die, so poll to the hard cap.
        /// </summary>
        private static async Task WaitForServerAndConnectAsync()
        {
            var server = MCPServiceLocator.Server;
            string url = HttpEndpointUtility.GetLocalBaseUrl();
            var pollDelay = TimeSpan.FromMilliseconds(500);
            var hardCap = TimeSpan.FromMinutes(5);
            double startTime = EditorApplication.timeSinceStartup;

            while (true)
            {
                // Abort if the user manually stopped, or changed settings, while we were waiting.
                // The suppression check ends this up-to-5-minute wait without reviving the session.
                if (IsManualSessionStopSuppressed()) return;
                if (!IsAutoStartOnLoadEnabled()) return;
                if (!EditorConfigurationCache.Instance.UseHttpTransport) return;
                if (MCPServiceLocator.TransportManager.IsRunning(TransportMode.Http)) return;

                if (server.IsLocalHttpServerReachable())
                {
                    McpLog.Info($"Server ready on {url}");
                    bool started = await MCPServiceLocator.Bridge.StartAsync();
                    if (started)
                    {
                        ClearManualSessionStopSuppression();
                        ClearExternalConnectFailureCooldown();
                        McpLog.Info("Session connected");
                        MCPForUnityEditorWindow.RequestHealthVerification();
                        return;
                    }
                }

                double elapsed = EditorApplication.timeSinceStartup - startTime;
                bool launchProcessDied = server.HasManagedServerLaunchHandle
                    && !server.IsManagedServerLaunchProcessAlive()
                    && elapsed > 1.0;

                if (launchProcessDied || elapsed > hardCap.TotalSeconds)
                {
                    // Last-resort connect attempt in case reachability detection missed a live server.
                    if (await MCPServiceLocator.Bridge.StartAsync())
                    {
                        ClearManualSessionStopSuppression();
                        ClearExternalConnectFailureCooldown();
                        McpLog.Info("Session connected");
                        MCPForUnityEditorWindow.RequestHealthVerification();
                        return;
                    }

                    server.LogLocalHttpServerLaunchFailure();
                    return;
                }

                try { await Task.Delay(pollDelay); }
                catch { return; }
            }
        }

        /// <summary>
        /// Connects the bridge directly (for remote HTTP where the server is already running).
        /// </summary>
        private static async Task ConnectBridgeAsync()
        {
            string url = HttpEndpointUtility.GetRemoteBaseUrl();
            McpLog.Info($"Connecting to {url}…");
            bool started = await MCPServiceLocator.Bridge.StartAsync();
            if (started)
            {
                ClearManualSessionStopSuppression();
                McpLog.Info("Connected");
                MCPForUnityEditorWindow.RequestHealthVerification();
            }
            else
            {
                McpLog.Warn("Connection failed: could not connect to remote HTTP server");
            }
        }
    }
}
