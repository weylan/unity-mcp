using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;
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

        public void Complete(TestRunResult result = null)
        {
            _completion.TrySetResult(result);
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
            EditorUpdateScheduler.ResetForTests();
            TestJobManager.PersistSnapshotForTests = null;
            TestJobManager.QueuePlayerLoopUpdateForTests = null;
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
        public void UpdatePumpClaimsOwnerPersistsReceiptAndDoesNotUseDirectDispatchSeam()
        {
            TestJobManager.DelayCallSchedulerForTests = null;
            EditorUpdateScheduler.ResetForTests();

            string jobId = TestJobManager.StartJob(TestMode.EditMode, Filter(), 15_000);
            Assert.AreEqual(0, _runner.InvocationCount);
            Assert.AreEqual(1, EditorUpdateScheduler.PendingCountForTests);

            EditorUpdateScheduler.PumpForTests();

            TestJobIdentity owner = TestJobManager.PhysicalOwnerForTests.Value;
            TestJob job = TestJobManager.GetJob(jobId);
            Assert.AreEqual(1, _runner.InvocationCount);
            Assert.AreEqual(owner, _runner.BoundOwner);
            Assert.IsTrue(job.OwnerPersisted);
            Assert.AreEqual(TestJobPhase.AwaitingRunStarted, job.Phase);

            Assert.IsTrue(TestJobManager.OnRunStarted(owner, 1));
            Assert.IsTrue(TestJobManager.GetJob(jobId).RunStarted);
            Assert.IsTrue(TestJobManager.FinalizePhysicalOwnerFromRunFinished(owner, null));
        }

        [Test]
        public void TaskContinuation_IsQueuedFromWorkerAndFinalizedOnEditorUpdate()
        {
            TestJobManager.DelayCallSchedulerForTests = null;
            EditorUpdateScheduler.ResetForTests();

            string jobId = TestJobManager.StartJob(TestMode.EditMode, Filter(), 15_000);
            EditorUpdateScheduler.PumpForTests();
            TestJobIdentity owner = TestJobManager.PhysicalOwnerForTests.Value;
            Assert.IsTrue(TestJobManager.OnRunStarted(owner, 1));

            _runner.Complete();
            Assert.IsTrue(SpinWait.SpinUntil(() => EditorUpdateScheduler.PendingCountForTests > 0, 3_000),
                "Worker completion must enqueue terminal cleanup for the editor update pump.");
            Assert.IsTrue(TestJobManager.PhysicalOwnerForTests.HasValue,
                "The worker continuation must not finalize the physical owner directly.");

            EditorUpdateScheduler.PumpForTests();

            TestJob terminal = TestJobManager.GetJob(jobId);
            Assert.AreEqual(TestJobPhase.Terminal, terminal.Phase);
            Assert.AreEqual(1, terminal.CleanupCount);
            Assert.IsFalse(TestJobManager.PhysicalOwnerForTests.HasValue);
        }

        [Test]
        public void TerminalReceipt_DoesNotClaimForeignAttachedLockWasReleased()
        {
            var acquired = SharedEditorOperationLock.TryAcquire("auto", "foreign-test", isExplicit: false);
            Assert.IsTrue(acquired.Acquired);
            var foreignOwner = new TestJobIdentity("foreign-job", 73);
            Assert.IsTrue(SharedEditorOperationLock.AttachToJob(acquired.Token, foreignOwner));

            string jobId = TestJobManager.StartJob(TestMode.EditMode, Filter(), 15_000);
            InvokeOnlyScheduled();
            TestJobIdentity owner = TestJobManager.PhysicalOwnerForTests.Value;
            Assert.IsTrue(TestJobManager.OnRunStarted(owner, 1));

            LogAssert.Expect(LogType.Warning, new Regex("attached lock belongs to a different owner"));
            Assert.IsTrue(TestJobManager.FinalizePhysicalOwnerFromRunFinished(owner, null));

            TestJob terminal = TestJobManager.GetJob(jobId);
            Assert.IsFalse(terminal.AttachedLockReleased);
            Assert.IsTrue(SharedEditorOperationLock.GetState().IsAttachedToJob);
            JObject serialized = JObject.FromObject(TestJobManager.ToSerializable(
                terminal, includeDetails: true, includeFailedTests: true));
            Assert.IsFalse(serialized["receipt"]?.Value<bool>("physical_terminal") ?? true,
                serialized.ToString());
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
            Assert.IsTrue(TestJobManager.LifecycleWatchdogTickForTests());
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
            Assert.IsTrue(TestJobManager.LifecycleWatchdogTickForTests());
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

        [Test]
        public void StartJob_PersistsQueuedDeadlineFromCreatedTimeIndependentOfInitTimeoutAndReloadsV3()
        {
            foreach (long initTimeout in new[] { 100L, 90_000L })
            {
                TestJobManager.ResetForTests(clearSessionState: true);
                _scheduled.Clear();
                _now += 100_000;
                TestJobManager.UnixTimeMillisecondsForTests = () => _now;
                TestJobManager.DelayCallSchedulerForTests = action => _scheduled.Add(action);

                string persisted = null;
                TestJobManager.PersistSnapshotForTests = json =>
                {
                    persisted = json;
                    return true;
                };
                string jobId = TestJobManager.StartJob(TestMode.EditMode, Filter(), initTimeout);
                TestJob queued = TestJobManager.GetJob(jobId);

                Assert.AreEqual(_now, queued.CreatedUnixMs);
                Assert.AreEqual(queued.CreatedUnixMs + 60_000, queued.QueuedDeadlineUnixMs);
                StringAssert.Contains("\"schema_version\":3", persisted);
                StringAssert.Contains($"\"created_unix_ms\":{queued.CreatedUnixMs}", persisted);
                StringAssert.Contains($"\"queued_deadline_unix_ms\":{queued.QueuedDeadlineUnixMs}", persisted);

                TestJobManager.PersistSnapshotForTests = null;
                TestJobManager.ClearInMemoryForTests();
                _scheduled.Clear();
                TestJobManager.EnsureInitialized();
                TestJob restored = TestJobManager.GetJob(jobId);
                Assert.AreEqual(queued.CreatedUnixMs, restored.CreatedUnixMs);
                Assert.AreEqual(queued.QueuedDeadlineUnixMs, restored.QueuedDeadlineUnixMs);
                Assert.AreEqual(initTimeout, restored.InitTimeoutMs);
            }
        }

        [Test]
        public void QueuedWatchdog_ExpiredOwnerlessJob_PersistFirstRollbackAndLateDispatchNoOp()
        {
            string jobId = TestJobManager.StartJob(TestMode.EditMode, Filter(), 250_000);
            _now += 60_001;

            TestJobManager.PersistSnapshotForTests = _ => false;
            LogAssert.Expect(LogType.Error,
                new Regex("Critical lifecycle persistence failed; retaining the prior physical fence\\."));
            Assert.IsFalse(TestJobManager.LifecycleWatchdogTickForTests());
            Assert.AreEqual(TestJobPhase.Queued, TestJobManager.GetJob(jobId).Phase);
            Assert.AreEqual(jobId, TestJobManager.CurrentJobId);
            Assert.IsFalse(TestJobManager.PhysicalOwnerForTests.HasValue);

            TestJobManager.PersistSnapshotForTests = _ => true;
            Assert.IsTrue(TestJobManager.LifecycleWatchdogTickForTests());
            TestJob terminal = TestJobManager.GetJob(jobId);
            Assert.AreEqual(TestJobStatus.Failed, terminal.Status);
            Assert.AreEqual(TestJobPhase.Terminal, terminal.Phase);
            Assert.IsNull(TestJobManager.CurrentJobId);
            Assert.IsFalse(TestJobManager.PhysicalOwnerForTests.HasValue);
            Assert.IsFalse(TestJobManager.TryDispatchQueuedJobForTests(jobId),
                "A delayCall captured before timeout must not replay Execute after the durable terminal.");
            Assert.AreEqual(0, _runner.InvocationCount);
        }

        [Test]
        public void LifecycleWatchdog_SubscriptionAndPlayerLoopWakeup_RemainSingleAndConditional()
        {
            TestJobManager.EnsureInitialized();
            TestJobManager.EnsureLifecycleWatchdogForTests();
            TestJobManager.EnsureLifecycleWatchdogForTests();
            Assert.AreEqual(1, TestJobManager.WatchdogSubscriptionCountForTests);

            int wakeups = 0;
            TestJobManager.QueuePlayerLoopUpdateForTests = () => wakeups++;
            TestJobManager.LifecycleWatchdogUpdateForTests();
            Assert.AreEqual(0, wakeups, "Idle watchdog updates must not force the Editor loop.");

            string jobId = TestJobManager.StartJob(TestMode.EditMode, Filter(), 500_000);
            TestJobManager.LifecycleWatchdogUpdateForTests();
            Assert.AreEqual(1, wakeups, "A live queued deadline needs one conditional wakeup.");

            _now += 60_001;
            TestJobManager.LifecycleWatchdogUpdateForTests();
            Assert.AreEqual(1, wakeups, "A terminal watchdog result has no remaining work to wake.");
            Assert.AreEqual(TestJobPhase.Terminal, TestJobManager.GetJob(jobId).Phase);
            Assert.AreEqual(1, TestJobManager.WatchdogSubscriptionCountForTests);
        }

        [Test]
        public void RestoreLifecycle_V3V2V1AndFutureSchema_AreStrictAndNeverReplayUnsafeWork()
        {
            const string v3 = "{\"schema_version\":3,\"current_job_id\":\"v3\",\"physical_owner\":null,\"next_generation\":2,\"jobs\":[{\"job_id\":\"v3\",\"generation\":1,\"status\":\"running\",\"phase\":\"queued\",\"mode\":\"EditMode\",\"created_unix_ms\":1000,\"queued_deadline_unix_ms\":61000,\"started_unix_ms\":1000,\"last_update_unix_ms\":1000}]}";
            ReloadPersistedSnapshot(v3, storageVersion: 3);
            Assert.AreEqual(61_000, TestJobManager.GetJob("v3").QueuedDeadlineUnixMs);

            const string runningTerminal = "{\"schema_version\":3,\"current_job_id\":null,\"physical_owner\":null,\"next_generation\":2,\"jobs\":[{\"job_id\":\"running-terminal\",\"generation\":1,\"status\":\"running\",\"phase\":\"terminal\",\"mode\":\"EditMode\",\"created_unix_ms\":1000,\"queued_deadline_unix_ms\":61000,\"started_unix_ms\":1000,\"last_update_unix_ms\":1000}]}";
            AssertV3RestoreBlocked(runningTerminal,
                "terminal phase requires a terminal logical status");

            const string queuedWithOwner = "{\"schema_version\":3,\"current_job_id\":\"queued-owner\",\"physical_owner\":{\"job_id\":\"queued-owner\",\"generation\":1},\"next_generation\":2,\"jobs\":[{\"job_id\":\"queued-owner\",\"generation\":1,\"status\":\"running\",\"phase\":\"queued\",\"mode\":\"EditMode\",\"created_unix_ms\":1000,\"queued_deadline_unix_ms\":61000,\"started_unix_ms\":1000,\"last_update_unix_ms\":1000}]}";
            AssertV3RestoreBlocked(queuedWithOwner,
                "queued phase cannot have a physical owner");

            const string ownerWithoutCurrent = "{\"schema_version\":3,\"current_job_id\":null,\"physical_owner\":{\"job_id\":\"owner-without-current\",\"generation\":1},\"next_generation\":2,\"jobs\":[{\"job_id\":\"owner-without-current\",\"generation\":1,\"status\":\"running\",\"phase\":\"running\",\"mode\":\"EditMode\",\"created_unix_ms\":1000,\"queued_deadline_unix_ms\":61000,\"started_unix_ms\":1000,\"last_update_unix_ms\":1000}]}";
            AssertV3RestoreBlocked(ownerWithoutCurrent,
                "physical owner requires matching current_job_id");

            const string runningWithoutOwner = "{\"schema_version\":3,\"current_job_id\":\"running-ownerless\",\"physical_owner\":null,\"next_generation\":2,\"jobs\":[{\"job_id\":\"running-ownerless\",\"generation\":1,\"status\":\"running\",\"phase\":\"running\",\"mode\":\"EditMode\",\"created_unix_ms\":1000,\"queued_deadline_unix_ms\":61000,\"started_unix_ms\":1000,\"last_update_unix_ms\":1000}]}";
            AssertV3RestoreBlocked(runningWithoutOwner,
                "physical phase requires matching physical_owner");

            const string orphanedQueued = "{\"schema_version\":3,\"current_job_id\":null,\"physical_owner\":null,\"next_generation\":2,\"jobs\":[{\"job_id\":\"orphaned-queued\",\"generation\":1,\"status\":\"running\",\"phase\":\"queued\",\"mode\":\"EditMode\",\"created_unix_ms\":1000,\"queued_deadline_unix_ms\":61000,\"started_unix_ms\":1000,\"last_update_unix_ms\":1000}]}";
            AssertV3RestoreBlocked(orphanedQueued,
                "nonterminal phase requires current_job_id");

            const string failedQueued = "{\"schema_version\":3,\"current_job_id\":\"failed-queued\",\"physical_owner\":null,\"next_generation\":2,\"jobs\":[{\"job_id\":\"failed-queued\",\"generation\":1,\"status\":\"failed\",\"phase\":\"queued\",\"mode\":\"EditMode\",\"created_unix_ms\":1000,\"queued_deadline_unix_ms\":61000,\"started_unix_ms\":1000,\"last_update_unix_ms\":1000}]}";
            AssertV3RestoreBlocked(failedQueued,
                "queued phase requires running logical status");

            const string failedAwaitingOwner = "{\"schema_version\":3,\"current_job_id\":\"failed-awaiting\",\"physical_owner\":{\"job_id\":\"failed-awaiting\",\"generation\":1},\"next_generation\":2,\"jobs\":[{\"job_id\":\"failed-awaiting\",\"generation\":1,\"status\":\"failed\",\"phase\":\"awaiting_run_started\",\"mode\":\"EditMode\",\"created_unix_ms\":1000,\"queued_deadline_unix_ms\":61000,\"started_unix_ms\":1000,\"awaiting_run_started_since_unix_ms\":1100,\"finished_unix_ms\":1200,\"last_update_unix_ms\":1200}]}";
            ReloadPersistedSnapshot(failedAwaitingOwner, storageVersion: 3);
            Assert.IsFalse(TestJobManager.IsRestoreBlockedForTests);
            Assert.AreEqual(TestJobStatus.Failed, TestJobManager.GetJob("failed-awaiting").Status);
            Assert.AreEqual(TestJobPhase.AwaitingRunStarted,
                TestJobManager.GetJob("failed-awaiting").Phase);
            Assert.AreEqual(new TestJobIdentity("failed-awaiting", 1),
                TestJobManager.PhysicalOwnerForTests.Value);

            const string succeededRunningOwner = "{\"schema_version\":3,\"current_job_id\":\"succeeded-running\",\"physical_owner\":{\"job_id\":\"succeeded-running\",\"generation\":1},\"next_generation\":2,\"jobs\":[{\"job_id\":\"succeeded-running\",\"generation\":1,\"status\":\"succeeded\",\"phase\":\"running\",\"mode\":\"EditMode\",\"created_unix_ms\":1000,\"queued_deadline_unix_ms\":61000,\"started_unix_ms\":1000,\"finished_unix_ms\":1200,\"last_update_unix_ms\":1200}]}";
            ReloadPersistedSnapshot(succeededRunningOwner, storageVersion: 3);
            Assert.IsFalse(TestJobManager.IsRestoreBlockedForTests);
            Assert.AreEqual(TestJobStatus.Succeeded,
                TestJobManager.GetJob("succeeded-running").Status);
            Assert.AreEqual(TestJobPhase.Running,
                TestJobManager.GetJob("succeeded-running").Phase);
            Assert.AreEqual(new TestJobIdentity("succeeded-running", 1),
                TestJobManager.PhysicalOwnerForTests.Value);

            const string invalidV3 = "{\"schema_version\":3,\"current_job_id\":\"invalid-v3\",\"physical_owner\":null,\"next_generation\":2,\"jobs\":[{\"job_id\":\"invalid-v3\",\"generation\":1,\"status\":\"running\",\"phase\":\"queued\",\"mode\":\"EditMode\",\"queued_deadline_unix_ms\":61000,\"started_unix_ms\":1000,\"last_update_unix_ms\":1000}]}";
            LogAssert.Expect(LogType.Error,
                new Regex("Restore blocked to preserve an unreadable lifecycle snapshot: Invalid V3 queued deadline"));
            ReloadPersistedSnapshot(invalidV3, storageVersion: 3);
            Assert.IsTrue(TestJobManager.IsRestoreBlockedForTests);
            Assert.AreEqual(invalidV3, TestJobManager.RestoreBlockedRawSnapshotForTests);
            Assert.AreEqual(0, _scheduled.Count);

            const string v2 = "{\"schema_version\":2,\"current_job_id\":\"v2\",\"physical_owner\":null,\"next_generation\":2,\"jobs\":[{\"job_id\":\"v2\",\"generation\":1,\"status\":\"running\",\"phase\":\"queued\",\"mode\":\"EditMode\",\"started_unix_ms\":2000,\"last_update_unix_ms\":2000}]}";
            ReloadPersistedSnapshot(v2, storageVersion: 2, failMigrationPersist: true);
            Assert.IsTrue(TestJobManager.IsRestoreBlockedForTests);
            Assert.AreEqual(v2, TestJobManager.RestoreBlockedRawSnapshotForTests);
            Assert.AreEqual(0, _scheduled.Count, "A failed migration must not replay queued work.");
            Assert.Throws<InvalidOperationException>(() =>
                TestJobManager.StartJob(TestMode.EditMode, Filter(), 100));

            ReloadPersistedSnapshot(v2, storageVersion: 2);
            TestJob migratedV2 = TestJobManager.GetJob("v2");
            Assert.AreEqual(2_000, migratedV2.CreatedUnixMs);
            Assert.AreEqual(62_000, migratedV2.QueuedDeadlineUnixMs);
            StringAssert.Contains("\"schema_version\":3", TestJobManager.LastPersistedSnapshotForTests);

            TestJobManager.ResetForTests(clearSessionState: true);
            SharedEditorOperationLock.ResetForTests(clearSessionState: true);
            TestJobManager.SeedLegacyV1StateForTests("v1", TestMode.PlayMode, 3_000);
            TestJobManager.ClearInMemoryForTests();
            _scheduled.Clear();
            TestJobManager.EnsureInitialized();
            TestJob migratedV1 = TestJobManager.GetJob("v1");
            Assert.AreEqual(3_000, migratedV1.CreatedUnixMs);
            Assert.AreEqual(63_000, migratedV1.QueuedDeadlineUnixMs);
            Assert.AreEqual(TestJobPhase.Running, migratedV1.Phase);
            Assert.AreEqual(0, _runner.InvocationCount, "Conservative V1 migration must not replay Execute.");

            const string future = "{\"schema_version\":4,\"current_job_id\":\"future\",\"physical_owner\":{\"job_id\":\"future\",\"generation\":9},\"next_generation\":10,\"jobs\":[{\"job_id\":\"future\",\"generation\":9,\"phase\":\"dispatching\"}],\"future_field\":{\"preserve\":true}}";
            ReloadPersistedSnapshot(future, storageVersion: 3);
            Assert.IsTrue(TestJobManager.IsRestoreBlockedForTests);
            Assert.AreEqual(future, TestJobManager.RestoreBlockedRawSnapshotForTests);
            Assert.AreEqual(new TestJobIdentity("future", 9), TestJobManager.PhysicalOwnerForTests.Value);
            Assert.AreEqual(0, _scheduled.Count, "Unknown schemas must not dispatch or replay work.");
            Assert.Throws<InvalidOperationException>(() =>
                TestJobManager.StartJob(TestMode.EditMode, Filter(), 100));
            Assert.AreEqual(future, TestJobManager.RestoreBlockedRawSnapshotForTests,
                "Blocked bytes must remain untouched after rejected work.");
        }

        [Test]
        public void UnknownV3SnapshotPreservesMigrationFenceAndDoesNotReplay()
        {
            const string future = "{\"schema_version\":4,\"current_job_id\":\"future\",\"physical_owner\":{\"job_id\":\"future\",\"generation\":9},\"next_generation\":10,\"jobs\":[{\"job_id\":\"future\",\"generation\":9,\"phase\":\"dispatching\"}],\"future_field\":{\"preserve\":true}}";

            ReloadPersistedSnapshot(future, storageVersion: 3);

            Assert.IsTrue(TestJobManager.IsRestoreBlockedForTests);
            Assert.AreEqual(future, TestJobManager.RestoreBlockedRawSnapshotForTests);
            Assert.AreEqual(new TestJobIdentity("future", 9), TestJobManager.PhysicalOwnerForTests.Value);
            Assert.AreEqual(0, _scheduled.Count, "An unknown snapshot must never replay queued work.");
            Assert.Throws<InvalidOperationException>(() =>
                TestJobManager.StartJob(TestMode.EditMode, Filter(), 100));
        }

        private void AssertV3RestoreBlocked(string snapshot, string reason)
        {
            LogAssert.Expect(LogType.Error,
                new Regex($"Restore blocked to preserve an unreadable lifecycle snapshot: {Regex.Escape(reason)}"));
            ReloadPersistedSnapshot(snapshot, storageVersion: 3);
            Assert.IsTrue(TestJobManager.IsRestoreBlockedForTests);
            Assert.AreEqual(snapshot, TestJobManager.RestoreBlockedRawSnapshotForTests);
            Assert.AreEqual(0, _scheduled.Count);
        }

        private void ReloadPersistedSnapshot(
            string json,
            int storageVersion,
            bool failMigrationPersist = false)
        {
            TestJobManager.ResetForTests(clearSessionState: true);
            SharedEditorOperationLock.ResetForTests(clearSessionState: true);
            TestJobManager.SeedPersistedSnapshotForTests(json, storageVersion);
            TestJobManager.ClearInMemoryForTests();
            _scheduled.Clear();
            TestJobManager.DelayCallSchedulerForTests = action => _scheduled.Add(action);
            TestJobManager.UnixTimeMillisecondsForTests = () => _now;
            if (failMigrationPersist)
            {
                TestJobManager.PersistSnapshotForTests = _ => false;
                LogAssert.Expect(LogType.Error,
                    new Regex("Critical lifecycle persistence failed; retaining the prior physical fence\\."));
            }
            TestJobManager.EnsureInitialized();
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
