using System;
using System.Collections.Generic;
using MCPForUnity.Editor.Services;
using NUnit.Framework;
using UnityEditor.TestTools.TestRunner.Api;

namespace MCPForUnityTests.Editor.Services
{
    public class TestJobManagerInitTimeoutTests
    {
        private readonly List<Action> _scheduled = new();
        private ControlledJobBoundTestRunner _runner;
        private long _now;

        [SetUp]
        public void SetUp()
        {
            MCPServiceLocator.Reset();
            TestJobManager.ResetForTests(clearSessionState: true);
            SharedEditorOperationLock.ResetForTests(clearSessionState: true);
            TestRunStatus.ResetForTests();
            _scheduled.Clear();
            _runner = new ControlledJobBoundTestRunner();
            MCPServiceLocator.Register<ITestRunnerService>(_runner);
            _now = 2_000_000;
            TestJobManager.UnixTimeMillisecondsForTests = () => _now;
            TestJobManager.DelayCallSchedulerForTests = action => _scheduled.Add(action);
        }

        [TearDown]
        public void TearDown()
        {
            TestJobManager.DelayCallSchedulerForTests = null;
            TestJobManager.UnixTimeMillisecondsForTests = null;
            TestJobManager.ResetForTests(clearSessionState: true);
            SharedEditorOperationLock.ResetForTests(clearSessionState: true);
            TestRunStatus.ResetForTests();
            MCPServiceLocator.Reset();
        }

        [Test]
        public void InitializationTimeout_UsesAwaitingAnchorAndUnknownTotalTransitionsToRunning()
        {
            string jobId = TestJobManager.StartJob(
                TestMode.EditMode,
                new TestFilterOptions { TestNames = new[] { "Init.Red" } },
                initTimeoutMs: 100);

            _now += 30_000;
            Assert.AreEqual(TestJobStatus.Running, TestJobManager.GetJob(jobId).Status,
                "Queued time must not consume the initialization budget.");

            Assert.AreEqual(1, _scheduled.Count);
            _scheduled[0]();
            TestJobIdentity owner = TestJobManager.PhysicalOwnerForTests.Value;
            TestJob awaiting = TestJobManager.GetJob(jobId);
            Assert.AreEqual(TestJobPhase.AwaitingRunStarted, awaiting.Phase);
            Assert.AreEqual(_now, awaiting.AwaitingRunStartedSinceUnixMs);

            _now += 99;
            Assert.AreEqual(TestJobStatus.Running, TestJobManager.GetJob(jobId).Status);

            TestJobManager.OnRunStarted(owner, totalTests: null);
            _now += 10_000;
            TestJob running = TestJobManager.GetJob(jobId);
            Assert.AreEqual(TestJobPhase.Running, running.Phase,
                "RunStarted with an unknown total still proves the physical run started.");
            Assert.AreEqual(TestJobStatus.Running, running.Status);
            Assert.IsNull(running.TotalTests);
        }

        [Test]
        public void InitTimeoutAndPhase_SurvivePersistAndRestore()
        {
            string jobId = TestJobManager.StartJob(
                TestMode.PlayMode,
                new TestFilterOptions { TestNames = new[] { "Persist.Red" } },
                initTimeoutMs: 90_000);
            _scheduled[0]();
            TestJob before = TestJobManager.GetJob(jobId);

            TestJobManager.ClearInMemoryForTests();
            TestJobManager.EnsureInitialized();

            TestJob restored = TestJobManager.GetJob(jobId);
            Assert.AreEqual(90_000, restored.InitTimeoutMs);
            Assert.AreEqual(before.Generation, restored.Generation);
            Assert.AreEqual(TestJobPhase.AwaitingRunStarted, restored.Phase);
            Assert.AreEqual(before.AwaitingRunStartedSinceUnixMs, restored.AwaitingRunStartedSinceUnixMs);
        }
    }
}
