using System.Linq;
using System.Collections.Generic;
using NUnit.Framework;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Services;
using UnityEditor;

namespace MCPForUnity.Editor.Tests.EditMode.Services
{
    [TestFixture]
    public class ToolDiscoveryServiceTests
    {
        private const string TestToolName = "test_tool_for_testing";

        [SetUp]
        public void SetUp()
        {
            // Clean up any test preferences
            string testKey = EditorPrefKeys.ToolEnabledPrefix + TestToolName;
            if (EditorPrefs.HasKey(testKey))
            {
                EditorPrefs.DeleteKey(testKey);
            }
        }

        [TearDown]
        public void TearDown()
        {
            // Clean up test preferences after each test
            string testKey = EditorPrefKeys.ToolEnabledPrefix + TestToolName;
            if (EditorPrefs.HasKey(testKey))
            {
                EditorPrefs.DeleteKey(testKey);
            }
        }

        [Test]
        public void SetToolEnabled_WritesToEditorPrefs()
        {
            // Arrange
            var service = new ToolDiscoveryService();

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

            var service = new ToolDiscoveryService();

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
            var service = new ToolDiscoveryService();

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
            var service = new ToolDiscoveryService();

            // Act
            bool result = service.IsToolEnabled(TestToolName);

            // Assert
            Assert.IsTrue(result, "Should return the stored preference value (true)");
        }

        [Test]
        public void ToolToggle_PersistsAcrossServiceInstances()
        {
            // Arrange
            var service1 = new ToolDiscoveryService();
            service1.SetToolEnabled(TestToolName, false);

            // Act - Create a new service instance
            var service2 = new ToolDiscoveryService();
            bool result = service2.IsToolEnabled(TestToolName);

            // Assert - The disabled state should persist
            Assert.IsFalse(result, "Tool state should persist across service instances");
        }

        [Test]
        public void DiscoverAllTools_DoesNotOverrideStoredFalse_ForBuiltInAutoRegisterFalseTool()
        {
            // Arrange
            var service = new ToolDiscoveryService();
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
        public void DiscoverAllTools_MatchesForkBaselineAt9d8751_Exactly36Names()
        {
            var expected = new[]
            {
                "batch_execute", "execute_code", "execute_menu_item", "find_gameobjects",
                "generate_audio", "generate_image", "generate_model", "get_test_job",
                "import_model", "import_model_file", "manage_animation", "manage_asset",
                "manage_build", "manage_camera", "manage_components", "manage_editor",
                "manage_editor_lock", "manage_gameobject", "manage_graphics", "manage_material",
                "manage_packages", "manage_physics", "manage_prefabs", "manage_probuilder",
                "manage_profiler", "manage_scene", "manage_script", "manage_scriptable_object",
                "manage_shader", "manage_texture", "manage_ui", "manage_vfx", "read_console",
                "refresh_unity", "run_tests", "unity_reflect"
            };

            using var service = new ToolDiscoveryService();
            string[] actual = service.DiscoverAllTools()
                .Where(tool => tool.IsBuiltIn)
                .Select(tool => tool.Name)
                .OrderBy(name => name, System.StringComparer.Ordinal)
                .ToArray();

            Assert.AreEqual(36, actual.Length);
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
