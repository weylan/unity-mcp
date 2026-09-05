using System;
using System.Collections.Generic;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;

namespace MCPForUnityTests.Editor.Services
{
    public class EditorTestLeaseTests
    {
        private readonly List<Action> _scheduled = new();
        private ControlledJobBoundTestRunner _runner;
        private string _oldLockMode;
        private DateTime _now;

        [SetUp]
        public void SetUp()
        {
            _oldLockMode = Environment.GetEnvironmentVariable("UNITY_MCP_SHARED_EDITOR_LOCK");
            Environment.SetEnvironmentVariable("UNITY_MCP_SHARED_EDITOR_LOCK", "on");
            MCPServiceLocator.Reset();
            TestJobManager.ResetForTests(clearSessionState: true);
            SharedEditorOperationLock.ResetForTests(clearSessionState: true);
            TestRunStatus.ResetForTests();
            _scheduled.Clear();
            _runner = new ControlledJobBoundTestRunner();
            MCPServiceLocator.Register<ITestRunnerService>(_runner);
            TestJobManager.DelayCallSchedulerForTests = action => _scheduled.Add(action);
            _now = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
            EditorTestLease.UtcNowForTests = () => _now;
        }

        [TearDown]
        public void TearDown()
        {
            SharedEditorOperationLock.PersistSnapshotForTests = null;
            TestJobManager.PersistSnapshotForTests = null;
            if (TestJobManager.PhysicalOwnerForTests.HasValue)
            {
                TestJobManager.FinalizePhysicalOwnerFromRunFinished(
                    TestJobManager.PhysicalOwnerForTests.Value,
                    null);
            }
            EditorTestLease.UtcNowForTests = null;
            TestJobManager.DelayCallSchedulerForTests = null;
            TestJobManager.ResetForTests(clearSessionState: true);
            SharedEditorOperationLock.ResetForTests(clearSessionState: true);
            TestRunStatus.ResetForTests();
            MCPServiceLocator.Reset();
            Environment.SetEnvironmentVariable("UNITY_MCP_SHARED_EDITOR_LOCK", _oldLockMode);
        }

        [Test]
        public void TryAcquire_MatchingRunningJobCreatesBoundedChildWithoutWeakeningParent()
        {
            TestJobIdentity owner = StartRunningAttachedJob(out string parentToken);
            var before = SharedEditorOperationLock.GetState();

            EditorTestLeaseGrant grant = EditorTestLease.TryAcquire(
                "defer-verify-exact-test",
                "fixture",
                ttlSeconds: 30);

            Assert.NotNull(grant);
            Assert.AreEqual(EditorTestLeaseKind.AttachedChild, grant.Kind);
            Assert.IsNotEmpty(grant.Token);
            Assert.AreNotEqual(parentToken, grant.Token);
            Assert.IsTrue(EditorTestLease.ValidateAndExtend(grant.Token, 15));
            Assert.IsFalse(SharedEditorOperationLock.ValidateToken(grant.Token));
            Assert.IsFalse(SharedEditorOperationLock.Reenter(parentToken, "ordinary-command"));
            Assert.IsFalse(SharedEditorOperationLock.Release(parentToken));
            Assert.AreEqual(owner.JobId, SharedEditorOperationLock.GetState().AttachedJobId);
            Assert.AreEqual(before.HolderHint, SharedEditorOperationLock.GetState().HolderHint);

            Assert.IsTrue(EditorTestLease.Release(grant.Token));
            Assert.IsTrue(SharedEditorOperationLock.GetState().IsAttachedToJob);
        }

        [Test]
        public void TryAcquire_StandaloneAndForeignHolderPreserveExistingLeaseSemantics()
        {
            EditorTestLeaseGrant standalone = EditorTestLease.TryAcquire(
                "defer-verify-standalone",
                "fixture",
                ttlSeconds: 30);

            Assert.NotNull(standalone);
            Assert.AreEqual(EditorTestLeaseKind.OwnedStandalone, standalone.Kind);
            Assert.IsTrue(SharedEditorOperationLock.ValidateToken(standalone.Token));
            Assert.IsTrue(EditorTestLease.Release(standalone.Token));

            var foreign = SharedEditorOperationLock.TryAcquire(
                "foreign",
                "external-owner",
                isExplicit: true,
                ttlSeconds: 60);
            var foreignOwner = new TestJobIdentity("foreign-job", 99);
            Assert.IsTrue(SharedEditorOperationLock.AttachToJob(foreign.Token, foreignOwner));

            Assert.IsNull(EditorTestLease.TryAcquire("defer-verify", "fixture"));
            Assert.AreEqual(foreignOwner.JobId, SharedEditorOperationLock.GetState().AttachedJobId);
            Assert.IsTrue(SharedEditorOperationLock.CompleteAttachedJob(foreignOwner));
            Assert.IsTrue(SharedEditorOperationLock.Release(foreign.Token));
        }

        [Test]
        public void ChildLifecycle_SecondAcquireExpiryReloadAndParentTerminalFailClosed()
        {
            TestJobIdentity owner = StartRunningAttachedJob(out _);
            EditorTestLeaseGrant first = EditorTestLease.TryAcquire(
                "defer-verify-first",
                "fixture",
                ttlSeconds: 10);
            Assert.NotNull(first);
            Assert.IsNull(EditorTestLease.TryAcquire("defer-verify-second", "fixture"));

            _now = _now.AddSeconds(11);
            Assert.IsFalse(EditorTestLease.ValidateAndExtend(first.Token));

            EditorTestLeaseGrant replacement = EditorTestLease.TryAcquire(
                "defer-verify-replacement",
                "fixture",
                ttlSeconds: 30);
            Assert.NotNull(replacement);
            SharedEditorOperationLock.ClearInMemoryForTests();
            SharedEditorOperationLock.EnsureInitialized();
            Assert.IsTrue(EditorTestLease.ValidateAndExtend(replacement.Token, 10));

            Assert.IsTrue(TestJobManager.FinalizePhysicalOwnerFromRunFinished(owner, null));
            Assert.IsFalse(EditorTestLease.ValidateAndExtend(replacement.Token));
            Assert.IsFalse(SharedEditorOperationLock.GetState().Locked);
        }

        [Test]
        public void PersistenceFailure_RollsBackChildMutations()
        {
            StartRunningAttachedJob(out _);
            SharedEditorOperationLock.PersistSnapshotForTests = _ => false;
            Assert.IsNull(EditorTestLease.TryAcquire("defer-verify", "fixture"));

            SharedEditorOperationLock.PersistSnapshotForTests = _ => true;
            EditorTestLeaseGrant grant = EditorTestLease.TryAcquire("defer-verify", "fixture", ttlSeconds: 10);
            Assert.NotNull(grant, "A failed child persist must not leave a phantom child behind.");

            SharedEditorOperationLock.PersistSnapshotForTests = _ => false;
            Assert.IsFalse(EditorTestLease.Release(grant.Token));
            Assert.IsTrue(EditorTestLease.ValidateAndExtend(grant.Token));
            Assert.IsFalse(EditorTestLease.ValidateAndExtend(grant.Token, 10));

            _now = _now.AddSeconds(11);
            Assert.IsFalse(EditorTestLease.ValidateAndExtend(grant.Token),
                "A failed extension must retain the previous expiry.");
            Assert.IsNull(EditorTestLease.TryAcquire("replacement", "fixture"),
                "A failed expiry persist must not issue another child.");

            SharedEditorOperationLock.PersistSnapshotForTests = _ => true;
            EditorTestLeaseGrant replacement = EditorTestLease.TryAcquire("replacement", "fixture");
            Assert.NotNull(replacement);
            Assert.IsTrue(EditorTestLease.Release(replacement.Token));
        }

        [Test]
        public void ChildToken_IsRejectedByOrdinaryManageEditorLock()
        {
            StartRunningAttachedJob(out _);
            EditorTestLeaseGrant grant = EditorTestLease.TryAcquire("defer-verify", "fixture");
            Assert.NotNull(grant);

            object response = MCPForUnity.Editor.Tools.ManageEditorLock.HandleCommand(new JObject
            {
                ["action"] = "release",
                ["token"] = grant.Token,
            });

            Assert.IsInstanceOf<ErrorResponse>(response);
            Assert.AreEqual(SharedEditorOperationLock.InvalidTokenCode, ((ErrorResponse)response).Code);
            Assert.IsFalse(SharedEditorOperationLock.ValidateToken(grant.Token));
            Assert.IsFalse(SharedEditorOperationLock.Reenter(grant.Token, "ordinary-command"));
            Assert.IsTrue(SharedEditorOperationLock.GetState().IsAttachedToJob);
        }

        [TestCase("child_expires_unix_ms", "not-a-time")]
        [TestCase("child_token", "{\"invalid\":true}")]
        [TestCase("child_expires_unix_ms", "253402300799000")]
        public void ChildRestore_InvalidChildRetainsParentFence(string field, string invalidJson)
        {
            TestJobIdentity owner = StartRunningAttachedJob(out string parentToken);
            EditorTestLeaseGrant grant = EditorTestLease.TryAcquire("defer-verify", "fixture");
            Assert.NotNull(grant);
            const string stateKey = "MCPForUnity.SharedEditorOperationLockV2";
            JObject persisted = JObject.Parse(SessionState.GetString(stateKey, string.Empty));
            persisted[field] = invalidJson == "not-a-time" ? new JValue(invalidJson) : JToken.Parse(invalidJson);
            SessionState.SetString(stateKey, persisted.ToString());

            SharedEditorOperationLock.ClearInMemoryForTests();
            SharedEditorOperationLock.EnsureInitialized();

            Assert.IsTrue(SharedEditorOperationLock.ValidateAttachedToken(parentToken, owner),
                "Invalid child state must never discard the valid parent fence.");
            Assert.IsFalse(EditorTestLease.ValidateAndExtend(grant.Token),
                "An invalid or unbounded restored child must be rejected.");
            Assert.IsFalse(SharedEditorOperationLock.TryAcquire("foreign", "ordinary", true).Acquired);
            Assert.IsFalse(SharedEditorOperationLock.Release(parentToken));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TryAcquire_RunningOwnerMustMatchAttachedJobAndGeneration(bool changeGeneration)
        {
            StartRunningAttachedJob(out _);
            const string stateKey = "MCPForUnity.SharedEditorOperationLockV2";
            JObject persisted = JObject.Parse(SessionState.GetString(stateKey, string.Empty));
            if (changeGeneration)
                persisted["attached_job_generation"] = persisted.Value<long>("attached_job_generation") + 1;
            else
                persisted["attached_job_id"] = "foreign-job";
            SessionState.SetString(stateKey, persisted.ToString());
            SharedEditorOperationLock.ClearInMemoryForTests();

            Assert.IsNull(EditorTestLease.TryAcquire("defer-verify", "fixture"));
            var snapshot = SharedEditorOperationLock.GetState();
            Assert.IsTrue(snapshot.IsAttachedToJob);
            Assert.AreEqual(persisted.Value<string>("attached_job_id"), snapshot.AttachedJobId);
            Assert.AreEqual(persisted.Value<long>("attached_job_generation"), snapshot.AttachedJobGeneration);
        }

        [Test]
        public void ChildTerminal_PreservesUnexpiredExplicitParentLease()
        {
            TestJobIdentity owner = StartRunningAttachedJob(out string parentToken, explicitParent: true);
            EditorTestLeaseGrant grant = EditorTestLease.TryAcquire("defer-verify", "fixture");
            Assert.NotNull(grant);

            Assert.IsTrue(TestJobManager.FinalizePhysicalOwnerFromRunFinished(owner, null));

            Assert.IsFalse(EditorTestLease.ValidateAndExtend(grant.Token));
            Assert.IsFalse(SharedEditorOperationLock.GetState().IsAttachedToJob);
            Assert.IsTrue(SharedEditorOperationLock.ValidateToken(parentToken));
            Assert.IsTrue(SharedEditorOperationLock.Release(parentToken));
        }

        private TestJobIdentity StartRunningAttachedJob(out string parentToken, bool explicitParent = false)
        {
            var acquired = SharedEditorOperationLock.TryAcquire(
                "auto",
                "run_tests",
                isExplicit: explicitParent);
            Assert.IsTrue(acquired.Acquired);
            parentToken = acquired.Token;

            string jobId = TestJobManager.StartJob(
                TestMode.EditMode,
                new TestFilterOptions { TestNames = new[] { "Lease.Red" } },
                15_000,
                parentToken);
            Assert.AreEqual(1, _scheduled.Count);
            _scheduled[0]();
            TestJobIdentity owner = TestJobManager.PhysicalOwnerForTests.Value;
            Assert.AreEqual(jobId, owner.JobId);
            Assert.IsTrue(TestJobManager.OnRunStarted(owner, 1));
            return owner;
        }
    }
}
