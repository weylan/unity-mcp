using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Tools;
using MCPForUnityTests.Editor.Services;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Tools
{
    public class BatchExecuteTests
    {
        private readonly List<Action> _scheduled = new();
        private ControlledJobBoundTestRunner _runner;
        private string _oldLock;
        private string _oldGuard;

        [OneTimeSetUp]
        public void OneTimeSetUp() => CommandRegistry.Initialize();

        [SetUp]
        public void SetUp()
        {
            _oldLock = Environment.GetEnvironmentVariable("UNITY_MCP_SHARED_EDITOR_LOCK");
            _oldGuard = Environment.GetEnvironmentVariable("UNITY_MCP_SHARED_EDITOR_GUARD");
            Environment.SetEnvironmentVariable("UNITY_MCP_SHARED_EDITOR_LOCK", "on");
            Environment.SetEnvironmentVariable("UNITY_MCP_SHARED_EDITOR_GUARD", "block");
            MCPServiceLocator.Reset();
            TestJobManager.ResetForTests(clearSessionState: true);
            SharedEditorOperationLock.ResetForTests(clearSessionState: true);
            TestRunStatus.ResetForTests();
            _runner = new ControlledJobBoundTestRunner();
            MCPServiceLocator.Register<ITestRunnerService>(_runner);
            _scheduled.Clear();
            TestJobManager.DelayCallSchedulerForTests = action => _scheduled.Add(action);
            TestBatchAfterRunTestsTool.CallCount = 0;
        }

        [TearDown]
        public void TearDown()
        {
            TestJobManager.DelayCallSchedulerForTests = null;
            TestJobManager.ResetForTests(clearSessionState: true);
            SharedEditorOperationLock.ResetForTests(clearSessionState: true);
            TestRunStatus.ResetForTests();
            MCPServiceLocator.Reset();
            Environment.SetEnvironmentVariable("UNITY_MCP_SHARED_EDITOR_LOCK", _oldLock);
            Environment.SetEnvironmentVariable("UNITY_MCP_SHARED_EDITOR_GUARD", _oldGuard);
        }

        [Test]
        public void RunTestsStart_MustBeLastAndTransfersOuterLockWithoutExecutingLaterCommands()
        {
            object rejected = BatchExecute.HandleCommand(new JObject
            {
                ["commands"] = new JArray
                {
                    RunTestsCommand("Batch.Invalid"),
                    new JObject
                    {
                        ["tool"] = "test_batch_after_run_tests",
                        ["params"] = new JObject()
                    }
                }
            }).GetAwaiter().GetResult();

            var rejectedJson = JObject.FromObject(rejected);
            Assert.IsFalse(rejectedJson.Value<bool>("success"), rejectedJson.ToString());
            StringAssert.Contains("last executable", rejectedJson.Value<string>("error"));
            Assert.AreEqual(0, TestBatchAfterRunTestsTool.CallCount,
                "The batch must reject the shape before executing any later command.");
            Assert.AreEqual(0, _runner.InvocationCount);
            Assert.IsFalse(SharedEditorOperationLock.GetState().Locked);

            object accepted = BatchExecute.HandleCommand(new JObject
            {
                ["commands"] = new JArray { RunTestsCommand("Batch.Valid") }
            }).GetAwaiter().GetResult();

            var acceptedJson = JObject.FromObject(accepted);
            Assert.IsTrue(acceptedJson.Value<bool>("success"), acceptedJson.ToString());
            Assert.AreEqual("queued",
                acceptedJson["data"]?["results"]?[0]?["result"]?["data"]?.Value<string>("status"));
            Assert.AreEqual(0, _runner.InvocationCount);
            Assert.AreEqual(1, _scheduled.Count);
            Assert.IsTrue(SharedEditorOperationLock.GetState().IsAttachedToJob,
                "The outer batch lock must transfer to the queued test job.");
        }

        [Test]
        public void AttachedCamelCaseToken_ReturnsBusyPhysicalFence()
        {
            var acquired = SharedEditorOperationLock.TryAcquire("client", "suite", isExplicit: true);
            Assert.IsTrue(SharedEditorOperationLock.AttachToJob(
                acquired.Token,
                new TestJobIdentity("batch-owner", 5)));

            object response = BatchExecute.HandleCommand(new JObject
            {
                ["editorLockToken"] = acquired.Token,
                ["commands"] = new JArray { RunTestsCommand("Batch.Fenced") },
            }).GetAwaiter().GetResult();
            var json = JObject.FromObject(response);

            Assert.IsFalse(json.Value<bool>("success"), json.ToString());
            Assert.AreEqual(SharedEditorOperationLock.BusyCode, json.Value<string>("code"));
            Assert.IsTrue(json["data"]?.Value<bool>("fence_active") ?? false);
        }

        private static JObject RunTestsCommand(string testName) => new()
        {
            ["tool"] = "run_tests",
            ["params"] = new JObject
            {
                ["mode"] = "EditMode",
                ["testNames"] = new JArray(testName)
            }
        };
    }

    [McpForUnityTool("test_batch_after_run_tests")]
    public static class TestBatchAfterRunTestsTool
    {
        public static int CallCount { get; set; }

        public static object HandleCommand(JObject parameters)
        {
            CallCount++;
            return new { ok = true };
        }
    }
}
