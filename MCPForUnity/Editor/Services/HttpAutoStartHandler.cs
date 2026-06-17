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
    /// </summary>
    [InitializeOnLoad]
    internal static class HttpAutoStartHandler
    {
        private const string SessionInitKey = "HttpAutoStartHandler.SessionInitialized";
        private const string ManualSessionStopSuppressedKey = "HttpAutoStartHandler.ManualSessionStopSuppressed";
        private const double ExternalServerPollIntervalSeconds = 2.0;
        private const double FailedConnectCooldownSeconds = 15.0;
        private static double _lastExternalServerPollTime;
        private static double _lastFailedConnectTime = double.NegativeInfinity;
        private static string _lastFailedConnectBaseUrl;
        private static bool _autoConnectInProgress;
        private static bool _editorUpdateRegistered;

        static HttpAutoStartHandler()
        {
            if (Application.isBatchMode &&
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UNITY_MCP_ALLOW_BATCH")))
            {
                return;
            }

            RefreshExternalServerPolling();

            // SessionState resets on editor process start but persists across domain reloads.
            // Only run once per session — let HttpBridgeReloadHandler handle reload-resume cases.
            if (SessionState.GetBool(SessionInitKey, false)) return;

            // Only check lightweight EditorPrefs here — services like EditorConfigurationCache
            // and MCPServiceLocator may not be initialized yet on fresh editor launch.
            bool autoStartEnabled = IsAutoStartOnLoadEnabled();
            if (!autoStartEnabled) return;

            SessionState.SetBool(SessionInitKey, true);

            // Delay to let the editor and services finish initialization.
            EditorApplication.delayCall += OnEditorReady;
        }

        private static void OnEditorReady()
        {
            try
            {
                bool autoStartEnabled = IsAutoStartOnLoadEnabled();
                if (!autoStartEnabled) return;

                bool useHttp = EditorConfigurationCache.Instance.UseHttpTransport;
                if (!useHttp) return;

                // Don't auto-start if bridge is already running.
                if (MCPServiceLocator.TransportManager.IsRunning(TransportMode.Http)) return;

                _ = AutoStartAsync();
            }
            catch (Exception ex)
            {
                McpLog.Debug($"[HTTP Auto-Start] Deferred check failed: {ex.Message}");
            }
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

            if (_autoConnectInProgress)
            {
                return;
            }

            if (!CanAttemptExternalLocalConnect(logPolicyError: false))
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

        private static bool CanAttemptExternalLocalConnect(bool logPolicyError)
        {
            if (!IsAutoStartOnLoadEnabled()) return false;
            if (!EditorConfigurationCache.Instance.UseHttpTransport) return false;
            if (HttpEndpointUtility.IsRemoteScope()) return false;
            if (MCPServiceLocator.Bridge.IsRunning) return false;
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
            if (!TryBeginAutoConnect())
            {
                return;
            }

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
            }
        }

        internal static async Task<bool> TryConnectIfLocalServerReachableAsync()
        {
            try
            {
                if (!CanAttemptExternalLocalConnect(logPolicyError: true)) return false;

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

        /// <summary>
        /// Waits for the local HTTP server to accept connections, then connects the bridge.
        /// Mirrors TryAutoStartSessionAsync in McpConnectionSection: keep polling reachability while
        /// the launched process is alive; declare failure only when it exits without the port coming
        /// up, or a generous hard cap is reached.
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
                // Abort if user changed settings while we were waiting.
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

                bool processAlive = server.IsManagedServerLaunchProcessAlive();
                double elapsed = EditorApplication.timeSinceStartup - startTime;

                if ((!processAlive && elapsed > 1.0) || elapsed > hardCap.TotalSeconds)
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
