using System;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using MCPForUnityTests.Editor.Services;

namespace MCPForUnityTests.Editor.Tools
{
    /// <summary>
    /// Tests for RunTests tool functionality.
    /// Note: We cannot easily test the full HandleCommand because it would create
    /// recursive test runner calls.
    /// </summary>
    public class RunTestsTests
    {
        [Test]
        public void HandleCommand_WhenTestsAlreadyRunning_ReturnsBusyError()
        {
            // Arrange: Force TestJobManager into a "busy" state without starting a real run.
            // We do this via reflection because TestJobManager is internal.
            var asm = typeof(MCPForUnity.Editor.Services.MCPServiceLocator).Assembly;
            var testJobManagerType = asm.GetType("MCPForUnity.Editor.Services.TestJobManager");
            Assert.NotNull(testJobManagerType, "Could not locate TestJobManager type via reflection");

            var currentJobIdField = testJobManagerType.GetField("_currentJobId", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(currentJobIdField, "Could not locate TestJobManager._currentJobId field");

            var originalJobId = currentJobIdField.GetValue(null) as string;
            currentJobIdField.SetValue(null, "busy-test-job-id");

            try
            {
                var resultObj = MCPForUnity.Editor.Tools.RunTests.HandleCommand(new JObject()).GetAwaiter().GetResult();

                Assert.IsInstanceOf<ErrorResponse>(resultObj);
                var err = (ErrorResponse)resultObj;
                Assert.AreEqual(false, err.Success);
                Assert.AreEqual("tests_running", err.Code);

                var data = err.Data != null ? JObject.FromObject(err.Data) : null;
                Assert.NotNull(data, "Expected data payload on tests_running error");
                Assert.AreEqual("tests_running", data["reason"]?.ToString());
                Assert.GreaterOrEqual(data["retry_after_ms"]?.Value<int>() ?? 0, 500);
            }
            finally
            {
                currentJobIdField.SetValue(null, originalJobId);
            }
        }

        [Test]
        public void HandleCommand_WithInvalidMode_ReturnsError()
        {
            var resultObj = MCPForUnity.Editor.Tools.RunTests.HandleCommand(new JObject
            {
                ["mode"] = "NotARealMode"
            }).GetAwaiter().GetResult();

            Assert.IsInstanceOf<ErrorResponse>(resultObj);
            var err = (ErrorResponse)resultObj;
            Assert.AreEqual(false, err.Success);
            Assert.IsTrue(err.Error.Contains("Unknown test mode", StringComparison.OrdinalIgnoreCase));
        }

        [Test]
        public void HandleCommand_ReturnsQueuedJobBeforeRunnerInvocationAndAttachesAutoLock()
        {
            var scheduled = new System.Collections.Generic.List<Action>();
            var runner = new ControlledJobBoundTestRunner();
            string oldLockMode = Environment.GetEnvironmentVariable("UNITY_MCP_SHARED_EDITOR_LOCK");
            Environment.SetEnvironmentVariable("UNITY_MCP_SHARED_EDITOR_LOCK", "on");
            MCPServiceLocator.Reset();
            TestJobManager.ResetForTests(clearSessionState: true);
            SharedEditorOperationLock.ResetForTests(clearSessionState: true);
            TestRunStatus.ResetForTests();
            MCPServiceLocator.Register<ITestRunnerService>(runner);
            TestJobManager.DelayCallSchedulerForTests = action => scheduled.Add(action);

            var acquired = SharedEditorOperationLock.TryAcquire("auto", "run_tests", isExplicit: false);
            Assert.IsTrue(acquired.Acquired);

            try
            {
                object response = MCPForUnity.Editor.Tools.RunTests.HandleCommand(new JObject
                {
                    ["mode"] = "EditMode",
                    ["testNames"] = new JArray("Queued.Red"),
                    [MCPForUnity.Editor.Tools.RunTests.InternalEditorLockTokenParameter] = acquired.Token
                }).GetAwaiter().GetResult();

                var json = JObject.FromObject(response);
                Assert.IsTrue(json.Value<bool>("success"), json.ToString());
                Assert.AreEqual("queued", json["data"]?.Value<string>("status"));
                Assert.IsNotEmpty(json["data"]?.Value<string>("job_id"));
                Assert.AreEqual(0, runner.InvocationCount,
                    "The tool response must be constructed before Unity TestRunner is invoked.");
                Assert.AreEqual(1, scheduled.Count);

                var lockState = SharedEditorOperationLock.GetState();
                Assert.IsTrue(lockState.IsAttachedToJob);
                Assert.AreEqual(json["data"]?.Value<string>("job_id"), lockState.AttachedJobId);

                scheduled[0]();
                Assert.AreEqual(1, runner.InvocationCount);
            }
            finally
            {
                TestJobManager.DelayCallSchedulerForTests = null;
                TestJobManager.ResetForTests(clearSessionState: true);
                SharedEditorOperationLock.ResetForTests(clearSessionState: true);
                TestRunStatus.ResetForTests();
                MCPServiceLocator.Reset();
                Environment.SetEnvironmentVariable("UNITY_MCP_SHARED_EDITOR_LOCK", oldLockMode);
            }
        }

        [Test]
        public void PublicHandleCommand_ProductionSchedulerSeamsDisabled()
        {
            var runner = new ControlledJobBoundTestRunner();
            string oldLockMode = Environment.GetEnvironmentVariable("UNITY_MCP_SHARED_EDITOR_LOCK");
            Environment.SetEnvironmentVariable("UNITY_MCP_SHARED_EDITOR_LOCK", "on");
            MCPServiceLocator.Reset();
            TestJobManager.ResetForTests(clearSessionState: true);
            SharedEditorOperationLock.ResetForTests(clearSessionState: true);
            TestRunStatus.ResetForTests();
            MCPServiceLocator.Register<ITestRunnerService>(runner);
            TestJobManager.DelayCallSchedulerForTests = null;
            EditorUpdateScheduler.ResetForTests();

            var acquired = SharedEditorOperationLock.TryAcquire("auto", "run_tests", isExplicit: false);
            Assert.IsTrue(acquired.Acquired);

            try
            {
                object response = MCPForUnity.Editor.Tools.RunTests.HandleCommand(new JObject
                {
                    ["mode"] = "EditMode",
                    ["testNames"] = new JArray("ProductionScheduler.Red"),
                    [MCPForUnity.Editor.Tools.RunTests.InternalEditorLockTokenParameter] = acquired.Token
                }).GetAwaiter().GetResult();

                var json = JObject.FromObject(response);
                string jobId = json["data"]?.Value<string>("job_id");
                Assert.IsTrue(json.Value<bool>("success"), json.ToString());
                Assert.AreEqual("queued", json["data"]?.Value<string>("status"));
                Assert.AreEqual(0, runner.InvocationCount);

                EditorUpdateScheduler.PumpForTests();
                TestJobIdentity owner = TestJobManager.PhysicalOwnerForTests.Value;
                Assert.AreEqual(1, runner.InvocationCount);
                Assert.IsTrue(TestJobManager.OnRunStarted(owner, 1));

                runner.Complete();
                Assert.IsTrue(SpinWait.SpinUntil(() => EditorUpdateScheduler.PendingCountForTests > 0, 3_000));
                EditorUpdateScheduler.PumpForTests();

                JObject job = JObject.FromObject(TestJobManager.ToSerializable(
                    TestJobManager.GetJob(jobId), includeDetails: true, includeFailedTests: true));
                Assert.IsTrue(job["receipt"]?.Value<bool>("owner_persisted") ?? false, job.ToString());
                Assert.IsTrue(job["receipt"]?.Value<bool>("run_started") ?? false, job.ToString());
                Assert.IsTrue(job["receipt"]?.Value<bool>("physical_terminal") ?? false, job.ToString());
                Assert.AreEqual(1, job["receipt"]?.Value<int>("cleanup_count"));
                Assert.IsTrue(job["safe_to_start_new_run"]?.Value<bool>() ?? false, job.ToString());
            }
            finally
            {
                EditorUpdateScheduler.ResetForTests();
                TestJobManager.DelayCallSchedulerForTests = null;
                TestJobManager.ResetForTests(clearSessionState: true);
                SharedEditorOperationLock.ResetForTests(clearSessionState: true);
                TestRunStatus.ResetForTests();
                MCPServiceLocator.Reset();
                Environment.SetEnvironmentVariable("UNITY_MCP_SHARED_EDITOR_LOCK", oldLockMode);
            }
        }

        [Test]
        public void GetTestJob_FailedAssertionResult_RemainsPublicAtTerminal()
        {
            var runner = new ControlledJobBoundTestRunner();
            string oldLockMode = Environment.GetEnvironmentVariable("UNITY_MCP_SHARED_EDITOR_LOCK");
            Environment.SetEnvironmentVariable("UNITY_MCP_SHARED_EDITOR_LOCK", "on");
            MCPServiceLocator.Reset();
            TestJobManager.ResetForTests(clearSessionState: true);
            SharedEditorOperationLock.ResetForTests(clearSessionState: true);
            TestRunStatus.ResetForTests();
            MCPServiceLocator.Register<ITestRunnerService>(runner);
            TestJobManager.DelayCallSchedulerForTests = null;
            EditorUpdateScheduler.ResetForTests();

            var acquired = SharedEditorOperationLock.TryAcquire("auto", "run_tests", isExplicit: false);
            Assert.IsTrue(acquired.Acquired);

            try
            {
                object queuedResponse = MCPForUnity.Editor.Tools.RunTests.HandleCommand(new JObject
                {
                    ["mode"] = "EditMode",
                    ["testNames"] = new JArray("Expected.FailingAssertion"),
                    [MCPForUnity.Editor.Tools.RunTests.InternalEditorLockTokenParameter] = acquired.Token,
                }).GetAwaiter().GetResult();
                JObject queued = JObject.FromObject(queuedResponse);
                string jobId = queued["data"]?.Value<string>("job_id");
                Assert.IsTrue(queued.Value<bool>("success"), queued.ToString());

                EditorUpdateScheduler.PumpForTests();
                TestJobIdentity owner = TestJobManager.PhysicalOwnerForTests.Value;
                Assert.IsTrue(TestJobManager.OnRunStarted(owner, 1));
                TestJobManager.OnLeafTestFinished(
                    owner,
                    "Expected.FailingAssertion",
                    isFailure: true,
                    message: "expected assertion failure");

                var failedResult = new TestRunResult(
                    new TestRunSummary(1, 0, 1, 0, 0.01, "Failed"),
                    new[]
                    {
                        new TestRunTestResult(
                            "FailingAssertion",
                            "Expected.FailingAssertion",
                            "Failed",
                            0.01,
                            "expected assertion failure",
                            "stack",
                            string.Empty),
                    });
                runner.Complete(failedResult);
                Assert.IsTrue(SpinWait.SpinUntil(
                    () => EditorUpdateScheduler.PendingCountForTests > 0,
                    3_000));
                EditorUpdateScheduler.PumpForTests();

                object publicResponse = MCPForUnity.Editor.Tools.GetTestJob.HandleCommand(new JObject
                {
                    ["job_id"] = jobId,
                    ["includeDetails"] = true,
                    ["includeFailedTests"] = true,
                });
                JObject response = JObject.FromObject(publicResponse);
                Assert.IsTrue(response.Value<bool>("success"), response.ToString());
                JToken data = response["data"];
                Assert.AreEqual("failed", data?.Value<string>("status"));
                Assert.NotNull(data?["result"], response.ToString());
                Assert.AreEqual(1, data?["result"]?.Value<int>("total"));
                Assert.AreEqual(1, data?["result"]?.Value<int>("matched"));
                Assert.AreEqual(1, data?["result"]?["summary"]?.Value<int>("failed"));
                Assert.AreEqual(
                    "expected assertion failure",
                    data?["result"]?["results"]?[0]?.Value<string>("message"));
                Assert.IsTrue(data?["receipt"]?.Value<bool>("physical_terminal") ?? false);
                Assert.AreEqual(1, data?["receipt"]?.Value<int>("cleanup_count"));
            }
            finally
            {
                EditorUpdateScheduler.ResetForTests();
                TestJobManager.DelayCallSchedulerForTests = null;
                TestJobManager.ResetForTests(clearSessionState: true);
                SharedEditorOperationLock.ResetForTests(clearSessionState: true);
                TestRunStatus.ResetForTests();
                MCPServiceLocator.Reset();
                Environment.SetEnvironmentVariable("UNITY_MCP_SHARED_EDITOR_LOCK", oldLockMode);
            }
        }

    }
}
