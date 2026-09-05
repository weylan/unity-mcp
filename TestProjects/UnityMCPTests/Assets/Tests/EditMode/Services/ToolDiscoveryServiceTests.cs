using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using NUnit.Framework;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using MCPForUnity.Editor.Services;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tests.EditMode.Services
{
    [TestFixture]
    public class ToolDiscoveryServiceTests
    {
        private const string TestToolName = "test_tool_for_testing";
        private readonly Dictionary<string, bool?> _originalPreferences = new();
        private string _shadowConfigPath;
        private byte[] _originalConfigBytes;
        private bool _configCaptured;

        [SetUp]
        public void SetUp()
        {
            // Discovery initializes preferences. Capture before constructing/discovering any service.
            _originalPreferences.Clear();
            var toolTypes = TypeCache.GetTypesWithAttribute<McpForUnityToolAttribute>()
                .Concat(AppDomain.CurrentDomain.GetAssemblies()
                    .Where(assembly => !assembly.IsDynamic)
                    .SelectMany(GetLoadableTypes))
                .Distinct();
            foreach (var type in toolTypes)
            {
                var attribute = type.GetCustomAttribute<McpForUnityToolAttribute>();
                if (attribute == null) continue;
                string name = string.IsNullOrEmpty(attribute.Name)
                    ? StringCaseUtility.ToSnakeCase(type.Name.Replace("Tool", ""))
                    : attribute.Name;
                CapturePreference(name);
            }
            CapturePreference(TestToolName);

            // Shadow only this test project's configuration, never its parent/repository config.
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            Assert.AreEqual("UnityMCPTests", new DirectoryInfo(projectRoot).Name,
                "This fixture requires its own UnityMCPTests project.");
            _shadowConfigPath = Path.Combine(projectRoot, EditorPrefKeys.ProjectConfigFileName);
            _originalConfigBytes = File.Exists(_shadowConfigPath) ? File.ReadAllBytes(_shadowConfigPath) : null;
            _configCaptured = true;
            File.WriteAllText(_shadowConfigPath, "{\"version\":1,\"tools\":{}}");
            ProjectToolConfig.Instance.LoadOrDefault();
            Assert.AreEqual(_shadowConfigPath, ProjectToolConfig.Instance.ConfigPath,
                "Test config must resolve to the project-local shadow before any writes.");
            EditorPrefs.DeleteKey(EditorPrefKeys.ToolEnabledPrefix + TestToolName);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (_configCaptured)
                {
                    if (_originalConfigBytes == null) File.Delete(_shadowConfigPath);
                    else File.WriteAllBytes(_shadowConfigPath, _originalConfigBytes);
                    ProjectToolConfig.Instance.LoadOrDefault();
                }
            }
            finally
            {
                foreach (var entry in _originalPreferences)
                {
                    if (entry.Value.HasValue) EditorPrefs.SetBool(entry.Key, entry.Value.Value);
                    else EditorPrefs.DeleteKey(entry.Key);
                }
                _configCaptured = false;
            }
            foreach (var entry in _originalPreferences)
            {
                Assert.AreEqual(entry.Value.HasValue, EditorPrefs.HasKey(entry.Key), entry.Key);
                if (entry.Value.HasValue) Assert.AreEqual(entry.Value.Value, EditorPrefs.GetBool(entry.Key), entry.Key);
            }
            Assert.AreEqual(_originalConfigBytes != null, File.Exists(_shadowConfigPath));
            if (_originalConfigBytes != null) CollectionAssert.AreEqual(_originalConfigBytes, File.ReadAllBytes(_shadowConfigPath));
            TestContext.Out.WriteLine($"Fixture restored {_originalPreferences.Count} preferences and the original config existence/bytes.");
        }

        private void CapturePreference(string toolName)
        {
            string key = EditorPrefKeys.ToolEnabledPrefix + toolName;
            _originalPreferences[key] = EditorPrefs.HasKey(key) ? EditorPrefs.GetBool(key) : (bool?)null;
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException exception) { return exception.Types.Where(type => type != null); }
        }

        [Test]
        public void SetToolEnabled_WritesToEditorPrefs()
        {
            // Arrange
            using var service = new ToolDiscoveryService();

            // Act
            service.SetToolEnabled(TestToolName, false);

            // Assert
            string key = EditorPrefKeys.ToolEnabledPrefix + TestToolName;
            Assert.IsTrue(EditorPrefs.HasKey(key), "Preference key should exist after SetToolEnabled");
            Assert.IsFalse(EditorPrefs.GetBool(key, true), "Preference should be set to false");
        }

        [Test]
        public void IsToolEnabled_ReturnsFalse_WhenToolDoesNotExist()
        {
            // Arrange - Ensure no preference exists
            string key = EditorPrefKeys.ToolEnabledPrefix + TestToolName;
            if (EditorPrefs.HasKey(key))
            {
                EditorPrefs.DeleteKey(key);
            }

            using var service = new ToolDiscoveryService();

            // Act - For a non-existent tool, IsToolEnabled should return false
            // (since metadata.AutoRegister defaults to false for non-existent tools)
            bool result = service.IsToolEnabled(TestToolName);

            // Assert - Non-existent tools return false (no metadata found)
            Assert.IsFalse(result, "Non-existent tool should return false");
        }

        [Test]
        public void IsToolEnabled_ReturnsStoredValue_WhenPreferenceExists()
        {
            // Arrange
            string key = EditorPrefKeys.ToolEnabledPrefix + TestToolName;
            EditorPrefs.SetBool(key, false);  // Store false value
            using var service = new ToolDiscoveryService();

            // Act
            bool result = service.IsToolEnabled(TestToolName);

            // Assert
            Assert.IsFalse(result, "Should return the stored preference value (false)");
        }

        [Test]
        public void IsToolEnabled_ReturnsTrue_WhenPreferenceSetToTrue()
        {
            // Arrange
            string key = EditorPrefKeys.ToolEnabledPrefix + TestToolName;
            EditorPrefs.SetBool(key, true);
            using var service = new ToolDiscoveryService();

            // Act
            bool result = service.IsToolEnabled(TestToolName);

            // Assert
            Assert.IsTrue(result, "Should return the stored preference value (true)");
        }

        [Test]
        public void ToolToggle_PersistsAcrossServiceInstances()
        {
            // Arrange
            using var service1 = new ToolDiscoveryService();
            service1.SetToolEnabled(TestToolName, false);

            // Act - Create a new service instance
            using var service2 = new ToolDiscoveryService();
            bool result = service2.IsToolEnabled(TestToolName);

            // Assert - The disabled state should persist
            Assert.IsFalse(result, "Tool state should persist across service instances");
        }

        [Test]
        public void DiscoverAllTools_DoesNotOverrideStoredFalse_ForBuiltInAutoRegisterFalseTool()
        {
            // Arrange
            using var service = new ToolDiscoveryService();
            var builtInTool = service.DiscoverAllTools()
                .FirstOrDefault(tool => tool.IsBuiltIn && !tool.AutoRegister);

            Assert.IsNotNull(builtInTool, "Expected at least one built-in tool with AutoRegister=false.");

            string key = EditorPrefKeys.ToolEnabledPrefix + builtInTool.Name;
            bool hadOriginalKey = EditorPrefs.HasKey(key);
            bool originalValue = hadOriginalKey && EditorPrefs.GetBool(key, true);

            try
            {
                EditorPrefs.SetBool(key, false);
                service.InvalidateCache();

                // Act
                service.DiscoverAllTools();
                bool enabled = service.IsToolEnabled(builtInTool.Name);

                // Assert
                Assert.IsFalse(enabled, $"Built-in tool '{builtInTool.Name}' should remain disabled when preference is false.");
            }
            finally
            {
                if (hadOriginalKey)
                {
                    EditorPrefs.SetBool(key, originalValue);
                }
                else
                {
                    EditorPrefs.DeleteKey(key);
                }
            }
        }

        [Test]
        public void DiscoverAllTools_MatchesPublishedForkBaseline_Exactly38Names()
        {
            var expected = new[]
            {
                "batch_execute", "execute_code", "execute_menu_item", "find_gameobjects",
                "generate_audio", "generate_image", "generate_model", "get_test_job",
                "import_model", "import_model_file", "manage_animation", "manage_asset",
                "manage_build", "manage_camera", "manage_components", "manage_editor",
                "manage_editor_lock", "manage_gameobject", "manage_graphics", "manage_material",
                "manage_packages", "manage_physics", "manage_playmode_test", "manage_prefabs", "manage_probuilder",
                "manage_profiler", "manage_scene", "manage_script", "manage_scriptable_object",
                "manage_shader", "manage_texture", "manage_ui", "manage_vfx", "read_console",
                "refresh_unity", "run_tests", "simulate_input", "unity_reflect"
            };

            using var service = new ToolDiscoveryService();
            string[] actual = service.DiscoverAllTools()
                .Where(tool => tool.IsBuiltIn)
                .Select(tool => tool.Name)
                .OrderBy(name => name, System.StringComparer.Ordinal)
                .ToArray();

            Assert.AreEqual(38, actual.Length);
            CollectionAssert.AreEqual(expected, actual,
                "The local lock/visibility adaptation must not add, hide, or rename a built-in tool.");
        }

        [Test]
        public void UnlistedBuiltInTool_UsesEditorPrefsThenMetadataFallback()
        {
            using var service = new ToolDiscoveryService();
            var tool = service.DiscoverAllTools()
                .FirstOrDefault(item => item.IsBuiltIn
                                        && !item.AutoRegister
                                        && !ProjectToolConfig.Instance.HasOverride(item.Name));
            Assert.NotNull(tool, "Expected an unlisted built-in tool with no project override.");

            string key = EditorPrefKeys.ToolEnabledPrefix + tool.Name;
            bool hadKey = EditorPrefs.HasKey(key);
            bool original = hadKey && EditorPrefs.GetBool(key, true);
            try
            {
                EditorPrefs.SetBool(key, true);
                Assert.IsTrue(service.IsToolEnabled(tool.Name), "EditorPrefs must be used when present.");

                EditorPrefs.DeleteKey(key);
                Assert.AreEqual(tool.AutoRegister, service.IsToolEnabled(tool.Name),
                    "Without a project override or EditorPrefs value, metadata is the fallback.");
            }
            finally
            {
                if (hadKey) EditorPrefs.SetBool(key, original);
                else EditorPrefs.DeleteKey(key);
            }
        }
    }
}
