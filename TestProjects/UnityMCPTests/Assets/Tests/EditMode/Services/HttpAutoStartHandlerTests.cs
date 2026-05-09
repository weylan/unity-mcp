using System.Threading.Tasks;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport;
using NUnit.Framework;
using UnityEditor;

namespace MCPForUnityTests.Editor.Services
{
    [TestFixture]
    public class HttpAutoStartHandlerTests
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
        public async Task TryConnectIfLocalServerReachableAsync_StartsSession_WhenExternalLocalServerIsReachable()
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
        public async Task TryConnectIfLocalServerReachableAsync_StartsSession_WhenAutoStartPreferenceIsUnset()
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
        public async Task TryConnectIfLocalServerReachableAsync_DoesNotStartSession_WhenAutoStartDisabled()
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
        public async Task TryConnectIfLocalServerReachableAsync_DoesNotStartSession_WhenManualStopIsSuppressed()
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
        public async Task TryConnectIfLocalServerReachableAsync_EntersCooldown_WhenStartFails()
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
        public async Task TryConnectIfLocalServerReachableAsync_DoesNotProbe_WhenBridgeAlreadyRunning()
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
            public int StartAsyncCalls { get; private set; }
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
                IsRunningValue = false;
                return Task.CompletedTask;
            }

            public BridgeVerificationResult Verify(int port)
            {
                return new BridgeVerificationResult { Success = IsRunningValue, PingSucceeded = IsRunningValue, HandshakeValid = true };
            }

            public Task<BridgeVerificationResult> VerifyAsync()
            {
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
