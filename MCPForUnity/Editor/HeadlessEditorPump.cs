using System;
using System.IO;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor
{
    /// <summary>
    /// Fork/headless support for the HTTP transport.
    ///
    /// In a headless Editor (batch mode, or a GUI editor on a virtual display such as Xvfb with no
    /// input events) the editor stops ticking <see cref="EditorApplication.update"/> once nothing is
    /// registered on it. The built-in HTTP auto-start (<c>HttpAutoStartHandler</c>) registers an
    /// update callback, but after it makes its one-shot decision it unregisters itself; with no other
    /// update subscriber the headless editor then goes idle and the external-server poller never runs,
    /// so the bridge never connects / registers with the local HTTP server. (The stdio transport does
    /// not hit this because <c>StdioBridgeHost</c> keeps <c>ProcessCommands</c> permanently on update.)
    ///
    /// This helper keeps a lightweight update callback registered for the whole session — which keeps a
    /// headless editor's loop ticking, the same way the stdio host stays alive — and, until the HTTP
    /// transport is connected, drives the connect directly via the existing
    /// <c>HttpAutoStartHandler.TryConnectIfLocalServerReachableAsync</c> (falling back to
    /// <c>Bridge.StartAsync</c>). Once connected it is a no-op, so per-command main-thread pumping and
    /// Play Mode's own player loop take over.
    ///
    /// Gated to <c>UNITY_MCP_ALLOW_BATCH</c> (the same opt-in the batch-mode bridge uses), so it only
    /// affects automation editors that asked for headless MCP; interactive editors are untouched.
    /// Set <c>MCP_HEADLESS_PUMP_DIAG=&lt;path&gt;</c> to append diagnostics; off by default.
    /// </summary>
    [InitializeOnLoad]
    internal static class HeadlessEditorPump
    {
        private const double AttemptIntervalSeconds = 2.0;

        private static bool _attemptInFlight;
        private static double _nextAttempt;
        private static int _attempts;
        private static string _diagPath;

        static HeadlessEditorPump()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UNITY_MCP_ALLOW_BATCH")))
            {
                return;
            }

            _diagPath = Environment.GetEnvironmentVariable("MCP_HEADLESS_PUMP_DIAG");
            Diag($"armed isBatchMode={Application.isBatchMode}");

            // Staying subscribed for the whole session is what keeps a headless editor's update loop
            // alive; it is re-added automatically after every domain reload because this ctor re-runs.
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            if (_attemptInFlight)
            {
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (now < _nextAttempt)
            {
                return;
            }
            _nextAttempt = now + AttemptIntervalSeconds;

            try
            {
                // Re-checked live every interval, so a bridge that drops (domain reload on Play Mode
                // entry, or a transient disconnect) is reconnected without needing a cached flag reset.
                if (MCPServiceLocator.TransportManager.IsRunning(TransportMode.Http))
                {
                    return;
                }

                // Entering Play Mode reloads the domain and drops the bridge; the built-in reload
                // resume is gated on AutoStartOnLoad, which can be off, so reconnect here in any mode.
                // Skip only while a compile/reload is in flight so we don't fight it — the retry loop
                // picks it up on the next interval.
                if (EditorApplication.isCompiling)
                {
                    return;
                }

                if (!MCPServiceLocator.Server.IsLocalHttpServerReachable())
                {
                    return; // server not up yet; try again next interval
                }

                _attemptInFlight = true;
                _attempts++;
                Diag($"connect attempt #{_attempts} autoStart={SafeAutoStart()} useHttp={SafeUseHttp()} bridgeRunning={SafeBridgeRunning()}");
                _ = ConnectAsync();
            }
            catch (Exception ex)
            {
                _attemptInFlight = false;
                Diag($"tick ex: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static async Task ConnectAsync()
        {
            try
            {
                // Preferred path: the fork's own external-server connect (handles stale-bridge restart).
                bool ok = await HttpAutoStartHandler.TryConnectIfLocalServerReachableAsync();

                // If its internal gate declined but the transport is still down, start the bridge directly.
                if (!ok && !MCPServiceLocator.TransportManager.IsRunning(TransportMode.Http))
                {
                    bool started = await MCPServiceLocator.Bridge.StartAsync();
                    Diag($"TryConnect=false; Bridge.StartAsync -> {started}");
                    ok = started;
                }
                else
                {
                    Diag($"TryConnect -> {ok}");
                }

                if (ok || MCPServiceLocator.TransportManager.IsRunning(TransportMode.Http))
                {
                    Diag("CONNECTED (headless pump)");
                }
            }
            catch (Exception ex)
            {
                Diag($"connect ex: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _attemptInFlight = false;
            }
        }

        private static bool SafeAutoStart()
        {
            try { return HttpAutoStartHandler.IsAutoStartOnLoadEnabled(); } catch { return false; }
        }

        private static bool SafeUseHttp()
        {
            try { return EditorConfigurationCache.Instance.UseHttpTransport; } catch { return false; }
        }

        private static bool SafeBridgeRunning()
        {
            try { return MCPServiceLocator.Bridge.IsRunning; } catch { return false; }
        }

        private static void Diag(string msg)
        {
            if (string.IsNullOrEmpty(_diagPath))
            {
                return;
            }
            try { File.AppendAllText(_diagPath, $"{DateTime.UtcNow:HH:mm:ss.fff} {msg}\n"); }
            catch { /* best-effort */ }
        }
    }
}
