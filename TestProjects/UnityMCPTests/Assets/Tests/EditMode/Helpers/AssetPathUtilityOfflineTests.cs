using NUnit.Framework;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Constants;
using UnityEditor;
using System.IO;

namespace MCPForUnityTests.Editor.Helpers
{
    public class AssetPathUtilityOfflineTests
    {
        private bool _originalForceRefresh;

        [SetUp]
        public void SetUp()
        {
            _originalForceRefresh = EditorPrefs.GetBool(EditorPrefKeys.DevModeForceServerRefresh, false);
        }

        [TearDown]
        public void TearDown()
        {
            EditorPrefs.SetBool(EditorPrefKeys.DevModeForceServerRefresh, _originalForceRefresh);
        }

        [Test]
        public void ShouldUseUvxOffline_WhenForceRefreshEnabled_ReturnsFalse()
        {
            EditorPrefs.SetBool(EditorPrefKeys.DevModeForceServerRefresh, true);
            Assert.IsFalse(AssetPathUtility.ShouldUseUvxOffline());
        }

        [Test]
        public void ShouldUseUvxOffline_DoesNotThrow()
        {
            EditorPrefs.SetBool(EditorPrefKeys.DevModeForceServerRefresh, false);
            Assert.DoesNotThrow(() => AssetPathUtility.ShouldUseUvxOffline());
        }

        [Test]
        public void ResolveServerPackageSourceWithLocalFallback_RemoteUnavailableUsesLocalServer()
        {
            string remoteSource = "git+https://github.com/weylan/unity-mcp.git@gameempire-mcp-v20260509.3#subdirectory=Server";
            string localServer = CreateTempServerPath();

            string result = AssetPathUtility.ResolveServerPackageSourceWithLocalFallback(
                remoteSource,
                localServer,
                remoteReachable: false);

            Assert.AreEqual(localServer, result);
        }

        [Test]
        public void ResolveServerPackageSourceWithLocalFallback_RemoteReachableKeepsRemote()
        {
            string remoteSource = "git+https://github.com/weylan/unity-mcp.git@gameempire-mcp-v20260509.3#subdirectory=Server";
            string localServer = CreateTempServerPath();

            string result = AssetPathUtility.ResolveServerPackageSourceWithLocalFallback(
                remoteSource,
                localServer,
                remoteReachable: true);

            Assert.AreEqual(remoteSource, result);
        }

        [Test]
        public void ResolveServerPackageSourceWithLocalFallback_MissingLocalServerKeepsRemote()
        {
            string remoteSource = "git+https://github.com/weylan/unity-mcp.git@gameempire-mcp-v20260509.3#subdirectory=Server";
            string missingLocalServer = Path.Combine(Path.GetTempPath(), "unity-mcp-missing-server");

            string result = AssetPathUtility.ResolveServerPackageSourceWithLocalFallback(
                remoteSource,
                missingLocalServer,
                remoteReachable: false);

            Assert.AreEqual(remoteSource, result);
        }

        [Test]
        public void GetBetaServerFromArgs_RemoteOverrideUsesResolvedPackageSource()
        {
            string remoteSource = "git+https://github.com/weylan/unity-mcp.git@gameempire-mcp-v20260509.4#subdirectory=Server";
            string localServer = CreateTempServerPath();

            string result = AssetPathUtility.GetBetaServerFromArgs(
                gitUrlOverride: remoteSource,
                packageSource: localServer,
                quoteFromPath: false);

            Assert.AreEqual($"--from {localServer}", result);
            Assert.That(result, Does.Not.Contain("github.com"));
        }

        [Test]
        public void TryFindLocalServerFallbackPath_LocalPackageLayoutFindsSiblingServer()
        {
            string root = CreateTempRoot();
            string packageRoot = Path.Combine(root, "unity-mcp", "MCPForUnity");
            string serverPath = Path.Combine(root, "unity-mcp", "Server");
            Directory.CreateDirectory(packageRoot);
            WriteServerPyproject(serverPath);

            bool found = AssetPathUtility.TryFindLocalServerFallbackPath(
                packageRoot,
                null,
                out string result);

            Assert.IsTrue(found);
            Assert.AreEqual(Path.GetFullPath(serverPath), result);
        }

        [Test]
        public void TryFindLocalServerFallbackPath_ProjectSiblingCheckoutFindsUnityMcpServer()
        {
            string root = CreateTempRoot();
            string projectAssetsPath = Path.Combine(root, "unity", "UnityProject", "Assets");
            string serverPath = Path.Combine(root, "unity-mcp", "Server");
            Directory.CreateDirectory(projectAssetsPath);
            WriteServerPyproject(serverPath);

            bool found = AssetPathUtility.TryFindLocalServerFallbackPath(
                null,
                projectAssetsPath,
                out string result);

            Assert.IsTrue(found);
            Assert.AreEqual(Path.GetFullPath(serverPath), result);
        }

        [Test]
        public void TryGetLocalServerPythonLaunch_LocalVenvReturnsPythonAndEntrypoint()
        {
            string serverPath = CreateTempServerPath();
            string entrypoint = Path.Combine(serverPath, "src", "main.py");
            string python = Path.Combine(
                serverPath,
                ".venv",
                UnityEngine.Application.platform == UnityEngine.RuntimePlatform.WindowsEditor ? "Scripts" : "bin",
                UnityEngine.Application.platform == UnityEngine.RuntimePlatform.WindowsEditor ? "python.exe" : "python");
            Directory.CreateDirectory(Path.GetDirectoryName(entrypoint));
            Directory.CreateDirectory(Path.GetDirectoryName(python));
            File.WriteAllText(entrypoint, "print('ok')\n");
            File.WriteAllText(python, string.Empty);

            bool found = AssetPathUtility.TryGetLocalServerPythonLaunch(
                serverPath,
                out string pythonPath,
                out string entrypointPath);

            Assert.IsTrue(found);
            Assert.AreEqual(Path.GetFullPath(python), pythonPath);
            Assert.AreEqual(Path.GetFullPath(entrypoint), entrypointPath);
        }

        private static string CreateTempServerPath()
        {
            string serverPath = Path.Combine(CreateTempRoot(), "Server");
            WriteServerPyproject(serverPath);
            return Path.GetFullPath(serverPath);
        }

        private static string CreateTempRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "unity-mcp-tests", Path.GetRandomFileName());
            Directory.CreateDirectory(root);
            return root;
        }

        private static void WriteServerPyproject(string serverPath)
        {
            Directory.CreateDirectory(serverPath);
            File.WriteAllText(
                Path.Combine(serverPath, "pyproject.toml"),
                "[project]\nname = \"mcpforunityserver\"\n");
        }
    }
}
