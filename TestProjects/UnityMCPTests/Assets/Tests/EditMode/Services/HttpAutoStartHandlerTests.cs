using System.Threading.Tasks;
using NUnit.Framework;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport;
using UnityEditor;

namespace MCPForUnityTests.Editor.Services
{
    /// <summary>
    /// Tests for the auto-start tick decisions (#1229): the session latch must only be
    /// written when the deferred work actually dispatches, so a domain reload that wipes
    /// the pending tick can no longer consume the once-per-session auto-start.
    /// TickCore is a pure decision — it never writes the latch and never spawns servers.
    /// The TryBeginReconnect tests only exercise its deliberate-drop paths, which never
    /// dispatch the async connect.
    /// </summary>
    public class HttpAutoStartHandlerTests
    {
        private FakeTransportClient _fakeClient;
        private TransportManager _savedManager;
        private bool _savedLatch;
        private bool _savedConnectPending;
        private bool _savedResumeFlag;
        private bool _savedAutoStart;
        private bool _savedUseHttpTransport;

        [SetUp]
        public void SetUp()
        {
            _savedLatch = SessionState.GetBool(HttpAutoStartHandler.SessionInitKey, false);
            _savedConnectPending = SessionState.GetBool(HttpAutoStartHandler.ConnectPendingKey, false);
            _savedResumeFlag = SessionState.GetBool(HttpBridgeReloadHandler.ResumeSessionKey, false);
            _savedAutoStart = EditorPrefs.GetBool(EditorPrefKeys.AutoStartOnLoad, false);
            _savedUseHttpTransport = EditorPrefs.GetBool(EditorPrefKeys.UseHttpTransport, true);
            _savedManager = MCPServiceLocator.TransportManager;

            SessionState.EraseBool(HttpAutoStartHandler.SessionInitKey);
            SessionState.EraseBool(HttpAutoStartHandler.ConnectPendingKey);
            SessionState.EraseBool(HttpBridgeReloadHandler.ResumeSessionKey);
            EditorPrefs.SetBool(EditorPrefKeys.AutoStartOnLoad, false);
            EditorPrefs.SetBool(EditorPrefKeys.UseHttpTransport, true);
            EditorConfigurationCache.Instance.Refresh();

            _fakeClient = new FakeTransportClient();
            var manager = new TransportManager();
            manager.Configure(() => _fakeClient, () => _fakeClient);
            MCPServiceLocator.Register(manager);
        }

        [TearDown]
        public void TearDown()
        {
            // Stored-false and absent are indistinguishable: every read uses GetBool(key, false).
            SessionState.SetBool(HttpAutoStartHandler.SessionInitKey, _savedLatch);
            SessionState.SetBool(HttpAutoStartHandler.ConnectPendingKey, _savedConnectPending);
            SessionState.SetBool(HttpBridgeReloadHandler.ResumeSessionKey, _savedResumeFlag);
            EditorPrefs.SetBool(EditorPrefKeys.AutoStartOnLoad, _savedAutoStart);
            EditorPrefs.SetBool(EditorPrefKeys.UseHttpTransport, _savedUseHttpTransport);
            EditorConfigurationCache.Instance.Refresh();
            MCPServiceLocator.Register(_savedManager);
        }

        private static bool LatchSet =>
            SessionState.GetBool(HttpAutoStartHandler.SessionInitKey, false);

        private static bool ConnectPendingSet =>
            SessionState.GetBool(HttpAutoStartHandler.ConnectPendingKey, false);

        [Test]
        public void TickCore_EditorBusy_Defers()
        {
            EditorPrefs.SetBool(EditorPrefKeys.AutoStartOnLoad, true);

            Assert.AreEqual(
                HttpAutoStartHandler.TickDecision.DeferBusy,
                HttpAutoStartHandler.TickCore(editorBusy: true));
            Assert.IsFalse(LatchSet);
        }

        [Test]
        public void TickCore_ResumePending_YieldsToReloadHandler()
        {
            EditorPrefs.SetBool(EditorPrefKeys.AutoStartOnLoad, true);
            SessionState.SetBool(HttpBridgeReloadHandler.ResumeSessionKey, true);

            Assert.AreEqual(
                HttpAutoStartHandler.TickDecision.DeferToResume,
                HttpAutoStartHandler.TickCore(editorBusy: false));
            Assert.IsFalse(LatchSet);
        }

        [Test]
        public void TickCore_ResumePendingButNothingToDo_SkipsWithoutWaiting()
        {
            // Auto-start disabled and not latched: a pending resume must not keep the
            // tick alive when the only possible outcome is Skip.
            SessionState.SetBool(HttpBridgeReloadHandler.ResumeSessionKey, true);

            Assert.AreEqual(
                HttpAutoStartHandler.TickDecision.Skip,
                HttpAutoStartHandler.TickCore(editorBusy: false));
        }

        [Test]
        public void TickCore_AutoStartDisabled_SkipsWithoutLatch()
        {
            Assert.AreEqual(
                HttpAutoStartHandler.TickDecision.Skip,
                HttpAutoStartHandler.TickCore(editorBusy: false));
            Assert.IsFalse(LatchSet, "no latch when disabled — the pref is re-read on the next domain load");
        }

        [Test]
        public void TickCore_AutoStartEnabled_ShouldStartWithoutWritingLatch()
        {
            EditorPrefs.SetBool(EditorPrefKeys.AutoStartOnLoad, true);

            Assert.AreEqual(
                HttpAutoStartHandler.TickDecision.ShouldStart,
                HttpAutoStartHandler.TickCore(editorBusy: false));
            Assert.IsFalse(LatchSet, "the caller latches only after the start work actually dispatches");
        }

        [Test]
        public void TickCore_Latched_Skips()
        {
            EditorPrefs.SetBool(EditorPrefKeys.AutoStartOnLoad, true);
            SessionState.SetBool(HttpAutoStartHandler.SessionInitKey, true);

            Assert.AreEqual(
                HttpAutoStartHandler.TickDecision.Skip,
                HttpAutoStartHandler.TickCore(editorBusy: false));
        }

        [Test]
        public void TickCore_LatchedWithConnectPending_Reconnects()
        {
            SessionState.SetBool(HttpAutoStartHandler.SessionInitKey, true);
            SessionState.SetBool(HttpAutoStartHandler.ConnectPendingKey, true);

            Assert.AreEqual(
                HttpAutoStartHandler.TickDecision.ShouldReconnect,
                HttpAutoStartHandler.TickCore(editorBusy: false),
                "a reload that killed the in-flight connect should finish connect-only, never re-spawn");
        }

        [Test]
        public void TickCore_ResumePendingBeatsReconnect()
        {
            SessionState.SetBool(HttpAutoStartHandler.SessionInitKey, true);
            SessionState.SetBool(HttpAutoStartHandler.ConnectPendingKey, true);
            SessionState.SetBool(HttpBridgeReloadHandler.ResumeSessionKey, true);

            Assert.AreEqual(
                HttpAutoStartHandler.TickDecision.DeferToResume,
                HttpAutoStartHandler.TickCore(editorBusy: false),
                "the reload handler owns bridge revival while a resume is pending");
        }

        [Test]
        public void TryBeginReconnect_AutoStartDisabled_DropsPendingReconnect()
        {
            SessionState.SetBool(HttpAutoStartHandler.ConnectPendingKey, true);

            Assert.IsTrue(HttpAutoStartHandler.TryBeginReconnect());
            Assert.IsFalse(ConnectPendingSet, "a deliberate drop must consume the pending marker");
        }

        [Test]
        public void TryBeginReconnect_StdioSelected_DropsPendingReconnect()
        {
            EditorPrefs.SetBool(EditorPrefKeys.AutoStartOnLoad, true);
            EditorPrefs.SetBool(EditorPrefKeys.UseHttpTransport, false);
            EditorConfigurationCache.Instance.Refresh();
            SessionState.SetBool(HttpAutoStartHandler.ConnectPendingKey, true);

            Assert.IsTrue(HttpAutoStartHandler.TryBeginReconnect());
            Assert.IsFalse(ConnectPendingSet);
        }

        [Test]
        public void TryBeginReconnect_BridgeAlreadyRunning_DropsPendingReconnect()
        {
            EditorPrefs.SetBool(EditorPrefKeys.AutoStartOnLoad, true);
            var start = MCPServiceLocator.TransportManager.StartAsync(TransportMode.Http);
            Assert.IsTrue(start.IsCompleted && start.Result, "fake bridge should start synchronously");
            SessionState.SetBool(HttpAutoStartHandler.ConnectPendingKey, true);

            Assert.IsTrue(HttpAutoStartHandler.TryBeginReconnect());
            Assert.IsFalse(ConnectPendingSet);
            Assert.AreEqual(1, _fakeClient.StartCalls, "an already-running bridge must not be restarted");
        }
    }

    /// <summary>
    /// Tests for the private fork feature: connecting to an already-running local HTTP server
    /// (TryConnectIfLocalServerReachableAsync), the default-ON auto-start preference, manual-stop
    /// suppression, and the failed-connect cooldown. Kept as a separate fixture from the upstream
    /// TickCore suite so each keeps its own service-locator / EditorPrefs setup.
    /// </summary>
    [TestFixture]
    public class HttpAutoStartHandlerExternalConnectTests
    {
        private bool _hadAutoStartOnLoad;
        private bool _originalAutoStartOnLoad;
        private bool _hadUseHttpTransport;
        private bool _originalUseHttpTransport;
        private bool _hadHttpTransportScope;
        private string _originalHttpTransportScope;
        private IBridgeControlService _originalBridge;
        private IServerManagementService _originalServer;

        [SetUp]
        public void SetUp()
        {
            _hadAutoStartOnLoad = EditorPrefs.HasKey(EditorPrefKeys.AutoStartOnLoad);
            _originalAutoStartOnLoad = EditorPrefs.GetBool(EditorPrefKeys.AutoStartOnLoad, EditorPrefDefaults.AutoStartOnLoad);
            _hadUseHttpTransport = EditorPrefs.HasKey(EditorPrefKeys.UseHttpTransport);
            _originalUseHttpTransport = EditorPrefs.GetBool(EditorPrefKeys.UseHttpTransport, true);
            _hadHttpTransportScope = EditorPrefs.HasKey(EditorPrefKeys.HttpTransportScope);
            _originalHttpTransportScope = EditorPrefs.GetString(EditorPrefKeys.HttpTransportScope, string.Empty);
            _originalBridge = MCPServiceLocator.Bridge;
            _originalServer = MCPServiceLocator.Server;

            EditorPrefs.SetBool(EditorPrefKeys.AutoStartOnLoad, EditorPrefDefaults.AutoStartOnLoad);
            EditorConfigurationCache.Instance.SetUseHttpTransport(true);
            EditorConfigurationCache.Instance.SetHttpTransportScope("local");
            HttpAutoStartHandler.ClearManualSessionStopSuppression();
            HttpAutoStartHandler.ClearExternalConnectFailureCooldown();
        }

        [TearDown]
        public void TearDown()
        {
            RestoreBool(EditorPrefKeys.AutoStartOnLoad, _hadAutoStartOnLoad, _originalAutoStartOnLoad);
            RestoreBool(EditorPrefKeys.UseHttpTransport, _hadUseHttpTransport, _originalUseHttpTransport);
            RestoreString(EditorPrefKeys.HttpTransportScope, _hadHttpTransportScope, _originalHttpTransportScope);
            EditorConfigurationCache.Instance.Refresh();
            HttpAutoStartHandler.ClearManualSessionStopSuppression();
            HttpAutoStartHandler.ClearExternalConnectFailureCooldown();

            MCPServiceLocator.Register<IBridgeControlService>(_originalBridge ?? new BridgeControlService());
            MCPServiceLocator.Register<IServerManagementService>(_originalServer ?? new ServerManagementService());
        }

        [Test]
        public void TryConnectIfLocalServerReachableAsync_StartsSession_WhenExternalLocalServerIsReachable()
            => TryConnectIfLocalServerReachableAsync_StartsSession_WhenExternalLocalServerIsReachableAsync().GetAwaiter().GetResult();

        private async Task TryConnectIfLocalServerReachableAsync_StartsSession_WhenExternalLocalServerIsReachableAsync()
        {
            var bridge = new FakeBridgeControlService { IsRunningValue = false, StartResult = true };
            var server = new FakeServerManagementService { LocalHttpServerReachable = true };
            MCPServiceLocator.Register<IBridgeControlService>(bridge);
            MCPServiceLocator.Register<IServerManagementService>(server);

            bool connected = await HttpAutoStartHandler.TryConnectIfLocalServerReachableAsync();

            Assert.IsTrue(connected);
            Assert.AreEqual(1, bridge.StartAsyncCalls);
            Assert.AreEqual(1, server.IsLocalHttpServerReachableCalls);
        }

        [Test]
        public void TryConnectIfLocalServerReachableAsync_StartsSession_WhenAutoStartPreferenceIsUnset()
            => TryConnectIfLocalServerReachableAsync_StartsSession_WhenAutoStartPreferenceIsUnsetAsync().GetAwaiter().GetResult();

        private async Task TryConnectIfLocalServerReachableAsync_StartsSession_WhenAutoStartPreferenceIsUnsetAsync()
        {
            EditorPrefs.DeleteKey(EditorPrefKeys.AutoStartOnLoad);
            var bridge = new FakeBridgeControlService { IsRunningValue = false, StartResult = true };
            var server = new FakeServerManagementService { LocalHttpServerReachable = true };
            MCPServiceLocator.Register<IBridgeControlService>(bridge);
            MCPServiceLocator.Register<IServerManagementService>(server);

            bool connected = await HttpAutoStartHandler.TryConnectIfLocalServerReachableAsync();

            Assert.IsTrue(connected);
            Assert.AreEqual(1, bridge.StartAsyncCalls);
            Assert.AreEqual(1, server.IsLocalHttpServerReachableCalls);
        }

        [Test]
        public void IsAutoStartOnLoadEnabled_ReturnsDefaultTrue_WhenPreferenceIsUnset()
        {
            EditorPrefs.DeleteKey(EditorPrefKeys.AutoStartOnLoad);

            Assert.IsTrue(HttpAutoStartHandler.IsAutoStartOnLoadEnabled());
        }

        [Test]
        public void TryConnectIfLocalServerReachableAsync_DoesNotStartSession_WhenAutoStartDisabled()
            => TryConnectIfLocalServerReachableAsync_DoesNotStartSession_WhenAutoStartDisabledAsync().GetAwaiter().GetResult();

        private async Task TryConnectIfLocalServerReachableAsync_DoesNotStartSession_WhenAutoStartDisabledAsync()
        {
            EditorPrefs.SetBool(EditorPrefKeys.AutoStartOnLoad, false);
            var bridge = new FakeBridgeControlService { IsRunningValue = false, StartResult = true };
            var server = new FakeServerManagementService { LocalHttpServerReachable = true };
            MCPServiceLocator.Register<IBridgeControlService>(bridge);
            MCPServiceLocator.Register<IServerManagementService>(server);

            bool connected = await HttpAutoStartHandler.TryConnectIfLocalServerReachableAsync();

            Assert.IsFalse(connected);
            Assert.AreEqual(0, bridge.StartAsyncCalls);
            Assert.AreEqual(0, server.IsLocalHttpServerReachableCalls);
        }

        [Test]
        public void TryConnectIfLocalServerReachableAsync_DoesNotStartSession_WhenManualStopIsSuppressed()
            => TryConnectIfLocalServerReachableAsync_DoesNotStartSession_WhenManualStopIsSuppressedAsync().GetAwaiter().GetResult();

        private async Task TryConnectIfLocalServerReachableAsync_DoesNotStartSession_WhenManualStopIsSuppressedAsync()
        {
            var bridge = new FakeBridgeControlService { IsRunningValue = false, StartResult = true };
            var server = new FakeServerManagementService { LocalHttpServerReachable = false };
            MCPServiceLocator.Register<IBridgeControlService>(bridge);
            MCPServiceLocator.Register<IServerManagementService>(server);

            HttpAutoStartHandler.RecordManualSessionStop();
            bool disconnected = await HttpAutoStartHandler.TryConnectIfLocalServerReachableAsync();
            server.LocalHttpServerReachable = true;
            bool reconnected = await HttpAutoStartHandler.TryConnectIfLocalServerReachableAsync();

            Assert.IsFalse(disconnected);
            Assert.IsFalse(reconnected);
            Assert.AreEqual(0, bridge.StartAsyncCalls);
            Assert.AreEqual(0, server.IsLocalHttpServerReachableCalls);
        }

        [Test]
        public void TryConnectIfLocalServerReachableAsync_EntersCooldown_WhenStartFails()
            => TryConnectIfLocalServerReachableAsync_EntersCooldown_WhenStartFailsAsync().GetAwaiter().GetResult();

        private async Task TryConnectIfLocalServerReachableAsync_EntersCooldown_WhenStartFailsAsync()
        {
            var bridge = new FakeBridgeControlService { IsRunningValue = false, StartResult = false };
            var server = new FakeServerManagementService { LocalHttpServerReachable = true };
            MCPServiceLocator.Register<IBridgeControlService>(bridge);
            MCPServiceLocator.Register<IServerManagementService>(server);

            bool first = await HttpAutoStartHandler.TryConnectIfLocalServerReachableAsync();
            bool second = await HttpAutoStartHandler.TryConnectIfLocalServerReachableAsync();

            Assert.IsFalse(first);
            Assert.IsFalse(second);
            Assert.AreEqual(1, bridge.StartAsyncCalls);
            Assert.AreEqual(1, server.IsLocalHttpServerReachableCalls);
        }

        [Test]
        public void TryConnectIfLocalServerReachableAsync_DoesNotProbe_WhenBridgeAlreadyRunning()
            => TryConnectIfLocalServerReachableAsync_DoesNotProbe_WhenBridgeAlreadyRunningAsync().GetAwaiter().GetResult();

        private async Task TryConnectIfLocalServerReachableAsync_DoesNotProbe_WhenBridgeAlreadyRunningAsync()
        {
            var bridge = new FakeBridgeControlService { IsRunningValue = true, StartResult = true };
            var server = new FakeServerManagementService { LocalHttpServerReachable = true };
            MCPServiceLocator.Register<IBridgeControlService>(bridge);
            MCPServiceLocator.Register<IServerManagementService>(server);

            bool connected = await HttpAutoStartHandler.TryConnectIfLocalServerReachableAsync();

            Assert.IsFalse(connected);
            Assert.AreEqual(0, bridge.StartAsyncCalls);
            Assert.AreEqual(0, server.IsLocalHttpServerReachableCalls);
        }

        [Test]
        public void TryConnectIfLocalServerReachableAsync_RestartsStaleRunningBridge()
            => TryConnectIfLocalServerReachableAsync_RestartsStaleRunningBridgeAsync().GetAwaiter().GetResult();

        private async Task TryConnectIfLocalServerReachableAsync_RestartsStaleRunningBridgeAsync()
        {
            var bridge = new FakeBridgeControlService
            {
                IsRunningValue = true,
                StartResult = true,
                VerifyResult = new BridgeVerificationResult
                {
                    Success = false,
                    PingSucceeded = false,
                    HandshakeValid = false,
                    Message = "stale connection"
                }
            };
            var server = new FakeServerManagementService { LocalHttpServerReachable = true };
            MCPServiceLocator.Register<IBridgeControlService>(bridge);
            MCPServiceLocator.Register<IServerManagementService>(server);

            bool connected = await HttpAutoStartHandler.TryConnectIfLocalServerReachableAsync();

            Assert.IsTrue(connected);
            Assert.AreEqual(1, bridge.VerifyAsyncCalls);
            Assert.AreEqual(1, bridge.StopAsyncCalls);
            Assert.AreEqual(1, bridge.StartAsyncCalls);
            Assert.AreEqual(1, server.IsLocalHttpServerReachableCalls);
        }

        private static void RestoreBool(string key, bool hadKey, bool value)
        {
            if (hadKey)
            {
                EditorPrefs.SetBool(key, value);
            }
            else
            {
                EditorPrefs.DeleteKey(key);
            }
        }

        private static void RestoreString(string key, bool hadKey, string value)
        {
            if (hadKey)
            {
                EditorPrefs.SetString(key, value);
            }
            else
            {
                EditorPrefs.DeleteKey(key);
            }
        }

        private sealed class FakeBridgeControlService : IBridgeControlService
        {
            public bool IsRunningValue { get; set; }
            public bool StartResult { get; set; }
            public BridgeVerificationResult VerifyResult { get; set; }
            public int StartAsyncCalls { get; private set; }
            public int StopAsyncCalls { get; private set; }
            public int VerifyAsyncCalls { get; private set; }
            public bool IsRunning => IsRunningValue;
            public int CurrentPort => 8080;
            public bool IsAutoConnectMode => false;
            public TransportMode? ActiveMode => TransportMode.Http;

            public Task<bool> StartAsync()
            {
                StartAsyncCalls++;
                IsRunningValue = StartResult;
                return Task.FromResult(StartResult);
            }

            public Task StopAsync()
            {
                StopAsyncCalls++;
                IsRunningValue = false;
                return Task.CompletedTask;
            }

            public BridgeVerificationResult Verify(int port)
            {
                return VerifyResult ?? new BridgeVerificationResult { Success = IsRunningValue, PingSucceeded = IsRunningValue, HandshakeValid = true };
            }

            public Task<BridgeVerificationResult> VerifyAsync()
            {
                VerifyAsyncCalls++;
                return Task.FromResult(Verify(CurrentPort));
            }
        }

        private sealed class FakeServerManagementService : IServerManagementService
        {
            public bool LocalHttpServerReachable { get; set; }
            public int IsLocalHttpServerReachableCalls { get; private set; }

            public bool ClearUvxCache() => true;
            public bool StartLocalHttpServer(bool quiet = false) => true;
            public bool StopLocalHttpServer() => true;
            public bool StopManagedLocalHttpServer() => true;
            public bool IsLocalHttpServerRunning() => LocalHttpServerReachable;

            // These IServerManagementService members post-date this fake; the HttpAutoStart tests
            // do not exercise them, so inert defaults are sufficient to satisfy the interface.
            public string GetLocalHttpServerLaunchLogPath() => null;
            public bool IsManagedServerLaunchProcessAlive() => false;
            public bool HasManagedServerLaunchHandle => false;
            public void LogLocalHttpServerLaunchFailure() { }

            public bool IsLocalHttpServerReachable()
            {
                IsLocalHttpServerReachableCalls++;
                return LocalHttpServerReachable;
            }

            public bool TryGetLocalHttpServerCommand(out string command, out string error)
            {
                command = "unity-mcp server";
                error = null;
                return true;
            }

            public bool IsLocalUrl() => true;
            public bool CanStartLocalServer() => true;
        }
    }
}
