using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services;
using NUnit.Framework;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine.TestTools;

namespace MCPForUnityTests.Editor.Services
{
    public class TestRunnerServiceJobBindingTests
    {
        private readonly List<Action> _scheduled = new();

        [SetUp]
        public void SetUp()
        {
            _scheduled.Clear();
            MCPServiceLocator.Reset();
            TestJobManager.ResetForTests(clearSessionState: true);
            SharedEditorOperationLock.ResetForTests(clearSessionState: true);
            TestRunStatus.ResetForTests();
            TestJobManager.DelayCallSchedulerForTests = action => _scheduled.Add(action);
            TestRunnerService.BeforeExecuteForTests = null;
            TestRunnerService.ExecuteOverrideForTests = null;
        }

        [TearDown]
        public void TearDown()
        {
            EditorUpdateScheduler.ResetForTests();
            TestRunnerService.BeforeExecuteForTests = null;
            TestRunnerService.ExecuteOverrideForTests = null;
            TestJobManager.DelayCallSchedulerForTests = null;
            TestJobManager.ResetForTests(clearSessionState: true);
            SharedEditorOperationLock.ResetForTests(clearSessionState: true);
            TestRunStatus.ResetForTests();
            MCPServiceLocator.Reset();
        }

        [UnityTest]
        public IEnumerator OperationLockContended_DoesNotEnterAwaitingUntilExecuteActuallyReturns()
        {
            var releaseExecute = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var executeCalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            TestRunnerService.BeforeExecuteForTests = () => releaseExecute.Task;
            TestRunnerService.ExecuteOverrideForTests = _ => executeCalled.TrySetResult(true);

            var service = new TestRunnerService();
            MCPServiceLocator.Register<ITestRunnerService>(service);
            int scheduledBefore = _scheduled.Count;
            string jobId = TestJobManager.StartJob(
                TestMode.EditMode,
                new TestFilterOptions { TestNames = new[] { "Binding.Red" } },
                15_000);

            Assert.Greater(_scheduled.Count, scheduledBefore,
                "StartJob must schedule a dispatch action.");
            for (int index = scheduledBefore;
                 index < _scheduled.Count && TestJobManager.GetJob(jobId).Phase == TestJobPhase.Queued;
                 index++)
            {
                _scheduled[index]();
            }
            Assert.AreEqual(TestJobPhase.Dispatching, TestJobManager.GetJob(jobId).Phase,
                "Semaphore/pre-execute delay must not start the initialization clock.");
            Assert.IsNull(TestJobManager.GetJob(jobId).AwaitingRunStartedSinceUnixMs);

            releaseExecute.TrySetResult(true);
            for (int i = 0; i < 120 && !executeCalled.Task.IsCompleted; i++)
            {
                yield return null;
            }
            Assert.IsTrue(executeCalled.Task.IsCompleted, "The controlled Execute seam was not reached.");
            for (int i = 0; i < 120 && TestJobManager.GetJob(jobId).Phase == TestJobPhase.Dispatching; i++)
            {
                yield return null;
            }

            Assert.AreEqual(TestJobPhase.AwaitingRunStarted, TestJobManager.GetJob(jobId).Phase);
            Assert.IsNotNull(TestJobManager.GetJob(jobId).AwaitingRunStartedSinceUnixMs);

            service.RunFinished(null);
            for (int i = 0; i < 120 && TestJobManager.PhysicalOwnerForTests.HasValue; i++)
            {
                yield return null;
            }
            Assert.IsFalse(TestJobManager.PhysicalOwnerForTests.HasValue);
            service.Dispose();
        }

        [UnityTest]
        public IEnumerator StartFailure_ClearsCompletionAndCallbackOwnershipForTheNextRun()
        {
            var service = new TestRunnerService();
            TestRunnerService.BeforeExecuteForTests = () =>
                Task.FromException(new InvalidOperationException("injected start failure"));
            TestRunnerService.ExecuteOverrideForTests = _ => { };

            Task<TestRunResult> firstRun = service.RunTestsAsync(TestMode.EditMode);
            for (int i = 0; i < 120 && !firstRun.IsCompleted; i++)
            {
                yield return null;
            }
            Assert.IsTrue(firstRun.IsFaulted, "The injected start failure should propagate.");
            StringAssert.Contains("injected start failure", firstRun.Exception?.GetBaseException().Message);

            TestRunnerService.BeforeExecuteForTests = null;
            Task<TestRunResult> secondRun = service.RunTestsAsync(TestMode.EditMode);
            Assert.IsFalse(secondRun.IsFaulted,
                "A failed start must not poison later runs with an already-in-progress error.");

            service.RunFinished(null);
            for (int i = 0; i < 120 && !secondRun.IsCompleted; i++)
            {
                yield return null;
            }
            Assert.IsTrue(secondRun.IsCompleted, "The next run should consume the matching completion callback.");
            service.Dispose();
        }

        [UnityTest]
        public IEnumerator ExecuteReturnAndRunStarted_PreservePhysicalOwnerIdentity()
        {
            TestJobManager.DelayCallSchedulerForTests = null;
            EditorUpdateScheduler.ResetForTests();
            TestRunnerService.ExecuteOverrideForTests = _ => { };
            var service = new TestRunnerService();
            MCPServiceLocator.Register<ITestRunnerService>(service);
            string jobId = TestJobManager.StartJob(
                TestMode.EditMode,
                new TestFilterOptions { TestNames = new[] { "Binding.ProductionScheduler" } },
                15_000);

            try
            {
                EditorUpdateScheduler.PumpForTests();
                for (int i = 0; i < 120 && TestJobManager.GetJob(jobId).Phase == TestJobPhase.Dispatching; i++)
                {
                    yield return null;
                }

                TestJobIdentity owner = TestJobManager.PhysicalOwnerForTests.Value;
                Assert.AreEqual(TestJobPhase.AwaitingRunStarted, TestJobManager.GetJob(jobId).Phase);

                service.RunStarted(null);

                TestJob running = TestJobManager.GetJob(jobId);
                Assert.AreEqual(TestJobPhase.Running, running.Phase);
                Assert.IsTrue(running.RunStarted);
                Assert.AreEqual(owner, TestJobManager.PhysicalOwnerForTests.Value);

                service.RunFinished(null);
                Assert.IsFalse(TestJobManager.PhysicalOwnerForTests.HasValue);
            }
            finally
            {
                service.Dispose();
            }
        }

    }
}
