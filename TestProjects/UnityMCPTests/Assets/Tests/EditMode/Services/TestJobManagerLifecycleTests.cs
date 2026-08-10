using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services;
using NUnit.Framework;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.TestTools;

namespace MCPForUnityTests.Editor.Services
{
    internal sealed class ControlledJobBoundTestRunner : ITestRunnerService, IJobBoundTestRunnerService
    {
        private readonly TaskCompletionSource<TestRunResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int InvocationCount { get; private set; }
        public int BindCount { get; private set; }
        public bool AutoNotifyExecuteReturned { get; set; } = true;
        public TestJobIdentity BoundOwner { get; private set; }

        public Task<IReadOnlyList<Dictionary<string, string>>> GetTestsAsync(TestMode? mode)
            => Task.FromResult<IReadOnlyList<Dictionary<string, string>>>(
                Array.Empty<Dictionary<string, string>>());

        public Task<TestRunResult> RunTestsAsync(TestMode mode, TestFilterOptions filterOptions = null)
            => throw new InvalidOperationException("The job-bound entry must be used for MCP test jobs.");

        public Task<TestRunResult> RunTestsForJobAsync(
            TestJobIdentity owner,
            TestMode mode,
            TestFilterOptions filterOptions = null)
        {
            InvocationCount++;
            BoundOwner = owner;
            if (AutoNotifyExecuteReturned)
            {
                TestJobManager.OnExecuteReturned(owner);
            }
            return _completion.Task;
        }

        public void BindPhysicalOwner(TestJobIdentity owner)
        {
            BoundOwner = owner;
            BindCount++;
        }

        public void ClearPhysicalOwner(TestJobIdentity owner)
        {
            if (BoundOwner == owner)
            {
                BoundOwner = default;
            }
        }
    }

    internal sealed class PublicOnlyTestRunner : ITestRunnerService
    {
        private readonly TaskCompletionSource<TestRunResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int InvocationCount { get; private set; }

        public Task<IReadOnlyList<Dictionary<string, string>>> GetTestsAsync(TestMode? mode)
            => Task.FromResult<IReadOnlyList<Dictionary<string, string>>>(
                Array.Empty<Dictionary<string, string>>());

        public Task<TestRunResult> RunTestsAsync(TestMode mode, TestFilterOptions filterOptions = null)
        {
            InvocationCount++;
            return _completion.Task;
        }
    }

    public class TestJobManagerLifecycleTests
    {
        private ControlledJobBoundTestRunner _runner;
        private readonly List<Action> _scheduled = new();
        private long _now;

        [SetUp]
        public void SetUp()
        {
            Environment.SetEnvironmentVariable("UNITY_MCP_SHARED_EDITOR_LOCK", "on");
            MCPServiceLocator.Reset();
            SharedEditorOperationLock.ResetForTests(clearSessionState: true);
            TestJobManager.ResetForTests(clearSessionState: true);
            TestRunStatus.ResetForTests();

            _runner = new ControlledJobBoundTestRunner();
            MCPServiceLocator.Register<ITestRunnerService>(_runner);
            _scheduled.Clear();
            _now = 1_000_000;
            TestJobManager.UnixTimeMillisecondsForTests = () => _now;
            TestJobManager.DelayCallSchedulerForTests = action => _scheduled.Add(action);
        }

        [TearDown]
        public void TearDown()
        {
            TestJobManager.PersistSnapshotForTests = null;
            TestJobManager.DelayCallSchedulerForTests = null;
            TestJobManager.UnixTimeMillisecondsForTests = null;
            TestJobManager.ResetForTests(clearSessionState: true);
            SharedEditorOperationLock.ResetForTests(clearSessionState: true);
            TestRunStatus.ResetForTests();
            MCPServiceLocator.Reset();
            Environment.SetEnvironmentVariable("UNITY_MCP_SHARED_EDITOR_LOCK", null);
        }

        [Test]
        public void Dispatch_ForcePersistsOwnerBeforeRunnerAndPersistenceFailureDoesNotExecute()
        {
            string jobId = TestJobManager.StartJob(TestMode.EditMode, Filter(), 15_000);
            Assert.AreEqual(1, _scheduled.Count);

            int writes = 0;
            TestJobManager.PersistSnapshotForTests = _ => ++writes != 1;
            LogAssert.Expect(LogType.Error,
                new Regex("Critical lifecycle persistence failed; retaining the prior physical fence\\."));

            bool dispatched = TestJobManager.TryDispatchQueuedJobForTests(jobId);

            Assert.IsFalse(dispatched);
            Assert.AreEqual(0, _runner.InvocationCount, "Runner side effects require a durable owner claim.");
            Assert.IsFalse(TestJobManager.PhysicalOwnerForTests.HasValue);
            Assert.AreEqual(TestJobPhase.Queued, TestJobManager.GetJob(jobId).Phase);
        }

        [Test]
        public void LateCallbacks_MutateOnlyMatchingPhysicalOwnerAndFinalizeExactlyOnce()
        {
            string firstId = TestJobManager.StartJob(TestMode.EditMode, Filter(), 15_000);
            InvokeOnlyScheduled();
            TestJobIdentity first = TestJobManager.PhysicalOwnerForTests.Value;
            TestJobManager.OnRunStarted(first, 2);
            TestJobManager.OnLeafTestFinished(first, "First.Test", false, null);
            Assert.IsTrue(TestJobManager.FinalizePhysicalOwnerFromRunFinished(first, null));

            string secondId = TestJobManager.StartJob(TestMode.EditMode, Filter(), 15_000);
            InvokeOnlyScheduled();
            TestJobIdentity second = TestJobManager.PhysicalOwnerForTests.Value;
            TestJobManager.OnRunStarted(second, 3);

            TestJobManager.OnLeafTestFinished(first, "Late.First.Test", true, "late");
            Assert.IsFalse(TestJobManager.FinalizePhysicalOwnerFromRunFinished(first, null));

            TestJob secondJob = TestJobManager.GetJob(secondId);
            Assert.AreEqual(0, secondJob.CompletedTests);
            Assert.AreEqual(0, secondJob.FailuresSoFar.Count);
            Assert.AreEqual(second, TestJobManager.PhysicalOwnerForTests.Value);

            Assert.IsTrue(TestJobManager.FinalizePhysicalOwnerFromRunFinished(second, null));
            Assert.IsFalse(TestJobManager.FinalizePhysicalOwnerFromRunFinished(second, null),
                "Matching terminal transition must be idempotent.");
            Assert.AreEqual(TestJobPhase.Terminal, TestJobManager.GetJob(firstId).Phase);
        }

        [Test]
        public void RunFinished_WhenCriticalPersistFails_PreservesOwnerStatusAndLockAndDoesNotDispatchNext()
        {
            var acquired = SharedEditorOperationLock.TryAcquire("auto", "run_tests", isExplicit: false);
            Assert.IsTrue(acquired.Acquired);
            string jobId = TestJobManager.StartJob(TestMode.EditMode, Filter(), 15_000, acquired.Token);
            InvokeOnlyScheduled();
            TestJobIdentity owner = TestJobManager.PhysicalOwnerForTests.Value;
            TestJobManager.OnRunStarted(owner, 1);

            TestJobManager.PersistSnapshotForTests = _ => false;
            LogAssert.Expect(LogType.Error,
                new Regex("Critical lifecycle persistence failed; retaining the prior physical fence\\."));
            Assert.IsFalse(TestJobManager.FinalizePhysicalOwnerFromRunFinished(owner, null));

            Assert.AreEqual(owner, TestJobManager.PhysicalOwnerForTests.Value);
            Assert.AreEqual(TestJobPhase.Running, TestJobManager.GetJob(jobId).Phase);
            Assert.IsTrue(TestRunStatus.IsRunning);
            Assert.IsTrue(SharedEditorOperationLock.GetState().IsAttachedToJob);

            TestJobManager.PersistSnapshotForTests = _ => true;
            Assert.IsTrue(TestJobManager.FinalizePhysicalOwnerFromRunFinished(owner, null));
            Assert.IsFalse(TestRunStatus.IsRunning);
            Assert.IsFalse(SharedEditorOperationLock.GetState().Locked);
        }

        [Test]
        public void TimeoutAndClear_PreservePhysicalOwnerLockUntilMatchingRunFinished()
        {
            var acquired = SharedEditorOperationLock.TryAcquire("auto", "run_tests", isExplicit: false);
            string jobId = TestJobManager.StartJob(TestMode.EditMode, Filter(), 100, acquired.Token);
            InvokeOnlyScheduled();
            TestJobIdentity owner = TestJobManager.PhysicalOwnerForTests.Value;
            Assert.AreEqual(TestJobPhase.AwaitingRunStarted, TestJobManager.GetJob(jobId).Phase);

            _now += 101;
            TestJob timedOut = TestJobManager.GetJob(jobId);
            Assert.AreEqual(TestJobStatus.Failed, timedOut.Status);
            Assert.AreEqual(TestJobPhase.AwaitingRunStarted, timedOut.Phase);
            Assert.AreEqual(owner, TestJobManager.PhysicalOwnerForTests.Value);
            Assert.IsTrue(SharedEditorOperationLock.GetState().IsAttachedToJob);

            TestJobClearResult cleared = TestJobManager.ClearStuckJob();
            Assert.IsTrue(cleared.Cleared);
            Assert.IsFalse(cleared.SafeToStartNewRun);
            Assert.IsTrue(cleared.PhysicalOwnerRetained);
            Assert.IsTrue(cleared.RestartRequiredIfOrphaned);
            Assert.Throws<InvalidOperationException>(() =>
                TestJobManager.StartJob(TestMode.EditMode, Filter(), 100));

            Assert.IsTrue(TestJobManager.FinalizePhysicalOwnerFromRunFinished(owner, null));
            Assert.IsFalse(SharedEditorOperationLock.GetState().Locked);
            Assert.IsFalse(TestJobManager.HasRunningJob);
        }

        [Test]
        public void FirstReadinessConsumerAfterReload_RehydratesOwnerLockStatusAndCallbacksBeforeObservation()
        {
            var acquired = SharedEditorOperationLock.TryAcquire("auto", "run_tests", isExplicit: false);
            string jobId = TestJobManager.StartJob(TestMode.EditMode, Filter(), 15_000, acquired.Token);
            InvokeOnlyScheduled();
            TestJobIdentity owner = TestJobManager.PhysicalOwnerForTests.Value;
            TestJobManager.OnRunStarted(owner, 1);

            TestJobManager.ClearInMemoryForTests();
            SharedEditorOperationLock.ClearInMemoryForTests();
            TestRunStatus.ResetForTests();
            _runner = new ControlledJobBoundTestRunner();
            MCPServiceLocator.Register<ITestRunnerService>(_runner);

            var snapshot = EditorStateCache.GetSnapshot();

            Assert.IsTrue(snapshot["data"]?["tests"]?.Value<bool>("is_running")
                          ?? snapshot["tests"]?.Value<bool>("is_running")
                          ?? TestRunStatus.IsRunning);
            Assert.AreEqual(owner, TestJobManager.PhysicalOwnerForTests.Value);
            Assert.IsTrue(SharedEditorOperationLock.GetState().IsAttachedToJob);
            Assert.AreEqual(owner, _runner.BoundOwner);
            Assert.GreaterOrEqual(_runner.BindCount, 1);
            Assert.AreEqual(jobId, TestJobManager.CurrentJobId);
        }

        [Test]
        public void LegacyV1CurrentRunning_RestoresOwnerWithoutAnchorOrExecuteReplay()
        {
            string legacyId = "legacy-running-job";
            TestJobManager.SeedLegacyV1StateForTests(legacyId, TestMode.PlayMode, _now - 5_000);
            TestJobManager.ClearInMemoryForTests();
            _scheduled.Clear();

            TestJobManager.EnsureInitialized();

            TestJob restored = TestJobManager.GetJob(legacyId);
            Assert.NotNull(restored);
            Assert.AreEqual(TestJobPhase.Running, restored.Phase);
            Assert.IsNull(restored.AwaitingRunStartedSinceUnixMs);
            Assert.Greater(restored.Generation, 0);
            Assert.AreEqual(new TestJobIdentity(legacyId, restored.Generation),
                TestJobManager.PhysicalOwnerForTests.Value);
            Assert.IsTrue(SharedEditorOperationLock.GetState().IsAttachedToJob,
                "A migrated physical owner must regain an operation-lock fence.");
            Assert.AreEqual(0, _runner.InvocationCount);
            Assert.AreEqual(0, _scheduled.Count, "A legacy physical run must never replay Execute.");
        }

        [Test]
        public void PublicOnlyRunner_RemainsCompatibleWithDurableJobDispatch()
        {
            var publicRunner = new PublicOnlyTestRunner();
            MCPServiceLocator.Register<ITestRunnerService>(publicRunner);

            string jobId = TestJobManager.StartJob(TestMode.EditMode, Filter(), 15_000);
            InvokeOnlyScheduled();

            Assert.AreEqual(1, publicRunner.InvocationCount);
            Assert.AreEqual(TestJobPhase.Running, TestJobManager.GetJob(jobId).Phase);
            Assert.IsTrue(TestJobManager.PhysicalOwnerForTests.HasValue);
            Assert.IsTrue(TestJobManager.FinalizePhysicalOwnerFromRunFinished(
                TestJobManager.PhysicalOwnerForTests.Value,
                null));
        }

        [Test]
        public void LogicalFinishTime_RemainsStableWhenPhysicalTerminalArrivesLater()
        {
            var acquired = SharedEditorOperationLock.TryAcquire("auto", "run_tests", isExplicit: false);
            string jobId = TestJobManager.StartJob(TestMode.EditMode, Filter(), 100, acquired.Token);
            InvokeOnlyScheduled();
            TestJobIdentity owner = TestJobManager.PhysicalOwnerForTests.Value;

            _now += 101;
            long logicalFinished = TestJobManager.GetJob(jobId).FinishedUnixMs.Value;
            _now += 500;
            Assert.IsTrue(TestJobManager.FinalizePhysicalOwnerFromRunFinished(owner, null));

            TestJob terminal = TestJobManager.GetJob(jobId);
            Assert.AreEqual(logicalFinished, terminal.FinishedUnixMs);
            Assert.AreEqual(_now, terminal.PhysicalFinishedUnixMs);
        }

        [Test]
        public void TerminalHistory_IsBoundedInMemory()
        {
            for (int i = 0; i < 12; i++)
            {
                string jobId = TestJobManager.StartJob(TestMode.EditMode, Filter(), 15_000);
                InvokeOnlyScheduled();
                TestJobIdentity owner = TestJobManager.PhysicalOwnerForTests.Value;
                TestJobManager.OnRunStarted(owner, 0);
                _now++;
                Assert.IsTrue(TestJobManager.FinalizePhysicalOwnerFromRunFinished(owner, null), jobId);
            }

            Assert.LessOrEqual(TestJobManager.JobCountForTests, 10);
        }

        private static TestFilterOptions Filter() => new()
        {
            TestNames = new[] { "Lifecycle.Red" }
        };

        private void InvokeOnlyScheduled()
        {
            Assert.AreEqual(1, _scheduled.Count, "Expected one queued main-thread dispatch.");
            Action action = _scheduled[0];
            _scheduled.Clear();
            action();
        }
    }
}
