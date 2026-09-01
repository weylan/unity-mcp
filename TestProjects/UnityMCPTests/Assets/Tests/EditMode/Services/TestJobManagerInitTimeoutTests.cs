using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using MCPForUnity.Editor.Services;
using NUnit.Framework;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.TestTools;

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

        [Test]
        public void GetTestJobHandler_AfterDeadline_IsReadOnlyUntilWatchdogTicks()
        {
            string jobId = TestJobManager.StartJob(
                TestMode.EditMode,
                new TestFilterOptions { TestNames = new[] { "Getter.Must.Be.Pure" } },
                initTimeoutMs: 100);
            _scheduled[0]();
            TestJobIdentity owner = TestJobManager.PhysicalOwnerForTests.Value;
            _now += 101;

            int persistenceAttempts = 0;
            TestJobManager.PersistSnapshotForTests = _ =>
            {
                persistenceAttempts++;
                return true;
            };

            TestJob first = TestJobManager.GetJob(jobId);
            TestJob second = TestJobManager.GetJob(jobId);

            Assert.AreEqual(TestJobStatus.Running, first.Status);
            Assert.AreEqual(TestJobStatus.Running, second.Status);
            Assert.AreEqual(TestJobPhase.AwaitingRunStarted, second.Phase);
            Assert.AreEqual(0, persistenceAttempts, "A getter must not advance or persist lifecycle state.");
            Assert.AreEqual(owner, TestJobManager.PhysicalOwnerForTests.Value);

            Assert.IsTrue(TestJobManager.LifecycleWatchdogTickForTests());
            Assert.AreEqual(1, persistenceAttempts);
            Assert.AreEqual(TestJobStatus.Failed, TestJobManager.GetJob(jobId).Status);
            Assert.AreEqual(owner, TestJobManager.PhysicalOwnerForTests.Value);
        }

        [Test]
        public void LifecycleWatchdog_AwaitingTimeout_PersistFirstRollbackAndRetainsPhysicalOwner()
        {
            string jobId = TestJobManager.StartJob(
                TestMode.EditMode,
                new TestFilterOptions { TestNames = new[] { "Awaiting.Rollback" } },
                initTimeoutMs: 100);
            _scheduled[0]();
            TestJobIdentity owner = TestJobManager.PhysicalOwnerForTests.Value;
            _now += 101;

            TestJobManager.PersistSnapshotForTests = _ => false;
            LogAssert.Expect(LogType.Error,
                new Regex("Critical lifecycle persistence failed; retaining the prior physical fence\\."));
            Assert.IsFalse(TestJobManager.LifecycleWatchdogTickForTests());
            TestJob rolledBack = TestJobManager.GetJob(jobId);
            Assert.AreEqual(TestJobStatus.Running, rolledBack.Status);
            Assert.IsNull(rolledBack.FinishedUnixMs);
            Assert.AreEqual(owner, TestJobManager.PhysicalOwnerForTests.Value);

            TestJobManager.PersistSnapshotForTests = _ => true;
            Assert.IsTrue(TestJobManager.LifecycleWatchdogTickForTests());
            TestJob durable = TestJobManager.GetJob(jobId);
            Assert.AreEqual(TestJobStatus.Failed, durable.Status);
            Assert.AreEqual(TestJobPhase.AwaitingRunStarted, durable.Phase);
            Assert.IsNotNull(durable.FinishedUnixMs);
            Assert.AreEqual(owner, TestJobManager.PhysicalOwnerForTests.Value,
                "Logical timeout cannot release Unity's physical callback owner.");
        }
    }
}
