using System;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services
{
    public class SharedEditorCommandGuardTests
    {
        private string previousMode;

        [SetUp]
        public void SetUp()
        {
            previousMode = Environment.GetEnvironmentVariable("UNITY_MCP_SHARED_EDITOR_GUARD");
            Environment.SetEnvironmentVariable("UNITY_MCP_SHARED_EDITOR_GUARD", "block");
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("UNITY_MCP_SHARED_EDITOR_GUARD", previousMode);
        }

        [Test]
        public void RunTestsWithoutExplicitFilter_IsBlocked()
        {
            var decision = SharedEditorCommandGuard.Evaluate("run_tests", new JObject
            {
                ["mode"] = "EditMode"
            });

            Assert.IsFalse(decision.Allowed);
            Assert.AreEqual("run_tests", decision.Tool);
            StringAssert.Contains("requires at least one explicit test filter", decision.Reason);
        }

        [TestCase("testNames")]
        [TestCase("groupNames")]
        [TestCase("categoryNames")]
        [TestCase("assemblyNames")]
        [TestCase("test_names")]
        [TestCase("group_names")]
        [TestCase("category_names")]
        [TestCase("assembly_names")]
        public void RunTestsWithAnyExplicitFilter_IsAllowed(string filterKey)
        {
            var decision = SharedEditorCommandGuard.Evaluate("run_tests", new JObject
            {
                ["mode"] = "EditMode",
                [filterKey] = new JArray("GuardSmoke")
            });

            Assert.IsTrue(decision.Allowed);
            Assert.IsFalse(decision.WarnOnly);
        }

        [Test]
        public void RefreshUnityHighRiskVariants_AreBlocked()
        {
            AssertBlocked("refresh_unity", new JObject
            {
                ["mode"] = "if_dirty",
                ["scope"] = "scripts",
                ["compile"] = "request",
                ["wait_for_ready"] = false
            }, "compile=request");

            AssertBlocked("refresh_unity", new JObject
            {
                ["mode"] = "if_dirty",
                ["scope"] = "scripts",
                ["compile"] = "none",
                ["waitForReady"] = true
            }, "wait_for_ready");

            AssertBlocked("refresh_unity", new JObject
            {
                ["mode"] = "force",
                ["scope"] = "scripts",
                ["compile"] = "none"
            }, "mode=force");

            AssertBlocked("refresh_unity", new JObject
            {
                ["mode"] = "if_dirty",
                ["scope"] = "all",
                ["compile"] = "none"
            }, "scope=all");
        }

        [Test]
        public void LightRefreshUnity_IsAllowed()
        {
            var decision = SharedEditorCommandGuard.Evaluate("refresh_unity", new JObject
            {
                ["mode"] = "if_dirty",
                ["scope"] = "scripts",
                ["compile"] = "none",
                ["wait_for_ready"] = false
            });

            Assert.IsTrue(decision.Allowed);
        }

        [Test]
        public void RiskyEditorBuildPackageAndGraphicsActions_AreBlocked()
        {
            AssertBlocked("execute_menu_item", new JObject
            {
                ["menu_path"] = "File/Save Project"
            }, "arbitrary editor operations");

            AssertBlocked("manage_editor", new JObject
            {
                ["action"] = "play"
            }, "manage_editor action=play");

            AssertBlocked("manage_build", new JObject
            {
                ["action"] = "platform",
                ["target"] = "windows64"
            }, "manage_build action=platform");

            AssertBlocked("manage_build", new JObject
            {
                ["action"] = "settings",
                ["property"] = "version",
                ["value"] = "1.2.3"
            }, "settings write");

            AssertBlocked("manage_packages", new JObject
            {
                ["action"] = "resolve_packages"
            }, "resolve_packages");

            AssertBlocked("manage_graphics", new JObject
            {
                ["action"] = "bake_start"
            }, "bake_start");
        }

        [Test]
        public void GuardModeWarn_AllowsButMarksWarnOnly()
        {
            Environment.SetEnvironmentVariable("UNITY_MCP_SHARED_EDITOR_GUARD", "warn");

            var decision = SharedEditorCommandGuard.Evaluate("run_tests", new JObject
            {
                ["mode"] = "EditMode"
            });

            Assert.IsTrue(decision.Allowed);
            Assert.IsTrue(decision.WarnOnly);
            Assert.IsNotNull(decision.Reason);
        }

        [Test]
        public void GuardModeOff_AllowsRiskyCommand()
        {
            Environment.SetEnvironmentVariable("UNITY_MCP_SHARED_EDITOR_GUARD", "off");

            var decision = SharedEditorCommandGuard.Evaluate("run_tests", new JObject
            {
                ["mode"] = "EditMode"
            });

            Assert.IsTrue(decision.Allowed);
            Assert.IsFalse(decision.WarnOnly);
        }

        [Test]
        public void BlockedDecision_ReturnsStructuredErrorResponse()
        {
            var decision = SharedEditorCommandGuard.Evaluate("execute_menu_item", new JObject
            {
                ["menu_path"] = "GameEmpire/GuardProbe"
            });

            var error = JObject.FromObject(decision.ToErrorResponse());

            Assert.IsFalse(error.Value<bool>("success"));
            Assert.AreEqual("shared_editor_guard_blocked", error.Value<string>("code"));
            Assert.AreEqual("execute_menu_item", error["data"]?.Value<string>("tool"));
            Assert.Greater(error["data"]?.Value<int>("retry_after_ms") ?? 0, 0);
        }

        [Test]
        public void BatchExecuteNestedRiskyCommand_IsBlockedAsFailedChild()
        {
            var result = BatchExecute.HandleCommand(new JObject
            {
                ["fail_fast"] = true,
                ["commands"] = new JArray
                {
                    new JObject
                    {
                        ["tool"] = "execute_menu_item",
                        ["params"] = new JObject
                        {
                            ["menu_path"] = "GameEmpire/GuardProbe/ThisShouldNotRun"
                        }
                    }
                }
            }).GetAwaiter().GetResult();

            var resultObject = JObject.FromObject(result);
            Assert.IsFalse(resultObject.Value<bool>("success"), resultObject.ToString());
            Assert.AreEqual("shared_editor_guard_blocked",
                resultObject["data"]?["results"]?[0]?["result"]?.Value<string>("code"));
            Assert.AreEqual(1, resultObject["data"]?.Value<int>("callFailureCount"));
        }

        private static void AssertBlocked(string tool, JObject parameters, string reasonFragment)
        {
            var decision = SharedEditorCommandGuard.Evaluate(tool, parameters);

            Assert.IsFalse(decision.Allowed, $"{tool} should be blocked");
            Assert.IsFalse(decision.WarnOnly);
            StringAssert.Contains(reasonFragment, decision.Reason);
        }
    }
}
