using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine.TestTools;
using RunState = UnityEditor.TestTools.TestRunner.Api.RunState;
using TestMode = UnityEditor.TestTools.TestRunner.Api.TestMode;
using TestStatus = UnityEditor.TestTools.TestRunner.Api.TestStatus;

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

        [UnityTest]
        public IEnumerator BoundErrorCallback_FinalizesOnlyItsPhysicalOwner()
        {
            TestJobManager.DelayCallSchedulerForTests = null;
            EditorUpdateScheduler.ResetForTests();
            TestRunnerService.ExecuteOverrideForTests = _ => { };
            var service = new TestRunnerService();
            MCPServiceLocator.Register<ITestRunnerService>(service);

            try
            {
                string firstJobId = TestJobManager.StartJob(TestMode.EditMode);
                EditorUpdateScheduler.PumpForTests();
                for (int i = 0; i < 120 && TestJobManager.GetJob(firstJobId).Phase == TestJobPhase.Dispatching; i++)
                {
                    yield return null;
                }

                var callbacksField = typeof(TestRunnerService).GetField(
                    "_registeredCallbacks",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(callbacksField);
                var firstCallbacks = callbacksField.GetValue(service) as IErrorCallbacks;
                Assert.NotNull(firstCallbacks, "The owner-bound callback must receive TestRunner initialization errors.");

                firstCallbacks.OnError("Prebuild setup failed");
                Assert.AreEqual(TestJobStatus.Failed, TestJobManager.GetJob(firstJobId).Status);
                Assert.AreEqual(TestJobPhase.Terminal, TestJobManager.GetJob(firstJobId).Phase);
                Assert.AreEqual("Prebuild setup failed", TestJobManager.GetJob(firstJobId).Error);
                Assert.IsFalse(TestJobManager.PhysicalOwnerForTests.HasValue);

                string secondJobId = TestJobManager.StartJob(TestMode.EditMode);
                EditorUpdateScheduler.PumpForTests();
                for (int i = 0; i < 120 && TestJobManager.GetJob(secondJobId).Phase == TestJobPhase.Dispatching; i++)
                {
                    yield return null;
                }

                TestJobIdentity secondOwner = TestJobManager.PhysicalOwnerForTests.Value;
                firstCallbacks.OnError("late error from the first run");
                Assert.AreEqual(secondOwner, TestJobManager.PhysicalOwnerForTests.Value);
                Assert.AreEqual(TestJobStatus.Running, TestJobManager.GetJob(secondJobId).Status);

                var secondCallbacks = callbacksField.GetValue(service) as IErrorCallbacks;
                Assert.NotNull(secondCallbacks);
                secondCallbacks.OnError("second cleanup");
                Assert.IsFalse(TestJobManager.PhysicalOwnerForTests.HasValue);
            }
            finally
            {
                service.Dispose();
            }
        }

        [Test]
        public void ResultTree_RehydratesLeavesAndExcludesSuites()
        {
            var emptySuite = new ResultStub("Fixture.EmptySuite", "Failed") { IsSuite = true };
            var leaf = new ResultStub("Fixture.Test", "Passed");

            var result = TestRunResult.Create(
                ResultStub.Suite(emptySuite, leaf),
                new ITestResultAdaptor[] { emptySuite });

            Assert.AreEqual(1, result.Results.Count);
            Assert.AreEqual("Fixture.Test", result.Results[0].FullName);
        }

        private sealed class ResultStub : ITestResultAdaptor
        {
            private readonly ITestResultAdaptor[] _children;

            public ResultStub(string name, string state, params ITestResultAdaptor[] children)
            {
                Name = FullName = name;
                ResultState = state;
                _children = children;
            }

            public static ResultStub Suite(params ITestResultAdaptor[] children)
                => new ResultStub("Suite", "Failed", children);

            public bool IsSuite { get; set; }
            public ITestAdaptor Test => new TestStub(FullName, IsSuite || HasChildren);
            public string Name { get; }
            public string FullName { get; }
            public string ResultState { get; }
            public TestStatus TestStatus => ResultState == "Passed" ? TestStatus.Passed : TestStatus.Failed;
            public double Duration => 0.1;
            public DateTime StartTime => DateTime.UtcNow;
            public DateTime EndTime => DateTime.UtcNow;
            public string Message => null;
            public string StackTrace => null;
            public int AssertCount => 1;
            public int FailCount => HasChildren ? _children.Sum(c => c.FailCount) : ResultState == "Failed" ? 1 : 0;
            public int PassCount => HasChildren ? _children.Sum(c => c.PassCount) : ResultState == "Passed" ? 1 : 0;
            public int SkipCount => 0;
            public int InconclusiveCount => 0;
            public bool HasChildren => _children.Length > 0;
            public IEnumerable<ITestResultAdaptor> Children => _children;
            public string Output => null;
            public TNode ToXml() => new TNode("test-case");
        }

        private sealed class TestStub : ITestAdaptor
        {
            public TestStub(string name, bool isSuite)
            {
                Name = name;
                IsSuite = isSuite;
            }

            public string Id => Name;
            public string Name { get; }
            public string FullName => Name;
            public int TestCaseCount => IsSuite ? 0 : 1;
            public bool HasChildren => false;
            public bool IsSuite { get; }
            public IEnumerable<ITestAdaptor> Children => Array.Empty<ITestAdaptor>();
            public ITestAdaptor Parent => null;
            public int TestCaseTimeout => 0;
            public ITypeInfo TypeInfo => null;
            public IMethodInfo Method => null;
            public object[] Arguments => Array.Empty<object>();
            public string[] Categories => Array.Empty<string>();
            public bool IsTestAssembly => false;
            public RunState RunState => RunState.Runnable;
            public string Description => null;
            public string SkipReason => null;
            public string ParentId => null;
            public string ParentFullName => null;
            public string UniqueName => Name;
            public string ParentUniqueName => null;
            public int ChildIndex => 0;
            public TestMode TestMode => TestMode.EditMode;
        }

    }
}
