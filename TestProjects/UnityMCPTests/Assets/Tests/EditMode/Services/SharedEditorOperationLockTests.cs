using System;
using MCPForUnity.Editor.Services;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services
{
    public class SharedEditorOperationLockTests
    {
        private string _oldMode;

        [SetUp]
        public void SetUp()
        {
            _oldMode = Environment.GetEnvironmentVariable("UNITY_MCP_SHARED_EDITOR_LOCK");
            Environment.SetEnvironmentVariable("UNITY_MCP_SHARED_EDITOR_LOCK", "on");
            SharedEditorOperationLock.ResetForTests(clearSessionState: true);
        }

        [TearDown]
        public void TearDown()
        {
            SharedEditorOperationLock.ResetForTests(clearSessionState: true);
            Environment.SetEnvironmentVariable("UNITY_MCP_SHARED_EDITOR_LOCK", _oldMode);
        }

        [Test]
        public void AttachedAutoAndExplicitLocks_ReleaseOnlyAtMatchingJobTerminal()
        {
            var autoOwner = new TestJobIdentity("auto-job", 1);
            var autoLock = SharedEditorOperationLock.TryAcquire("auto", "run_tests", isExplicit: false);
            Assert.IsTrue(SharedEditorOperationLock.AttachToJob(autoLock.Token, autoOwner));
            Assert.IsFalse(SharedEditorOperationLock.ReleaseIfAutoLock(autoLock.Token));
            Assert.IsFalse(SharedEditorOperationLock.CompleteAttachedJob(new TestJobIdentity("other", 1)));
            Assert.IsTrue(SharedEditorOperationLock.GetState().Locked);
            Assert.IsTrue(SharedEditorOperationLock.CompleteAttachedJob(autoOwner));
            Assert.IsFalse(SharedEditorOperationLock.GetState().Locked);

            var explicitOwner = new TestJobIdentity("explicit-job", 2);
            var explicitLock = SharedEditorOperationLock.TryAcquire("client", "suite", isExplicit: true);
            Assert.IsTrue(SharedEditorOperationLock.AttachToJob(explicitLock.Token, explicitOwner));
            Assert.IsFalse(SharedEditorOperationLock.Release(explicitLock.Token));
            Assert.IsTrue(SharedEditorOperationLock.CompleteAttachedJob(explicitOwner));

            var state = SharedEditorOperationLock.GetState();
            Assert.IsTrue(state.Locked, "An unexpired explicit caller lease remains after detaching the job.");
            Assert.IsFalse(state.IsAttachedToJob);
            Assert.IsTrue(SharedEditorOperationLock.Release(explicitLock.Token));
        }

        [Test]
        public void AttachedExplicitToken_CannotReenterAnotherCommandUntilPhysicalTerminal()
        {
            var owner = new TestJobIdentity("owner", 7);
            var acquired = SharedEditorOperationLock.TryAcquire("client", "suite", isExplicit: true);
            Assert.IsTrue(SharedEditorOperationLock.AttachToJob(acquired.Token, owner));

            Assert.IsFalse(SharedEditorOperationLock.Reenter(acquired.Token, "refresh_unity"));
            Assert.IsFalse(SharedEditorOperationLock.Release(acquired.Token));
            Assert.IsTrue(SharedEditorOperationLock.ValidateAttachedToken(acquired.Token, owner));

            Assert.IsTrue(SharedEditorOperationLock.CompleteAttachedJob(owner));
            Assert.IsTrue(SharedEditorOperationLock.Reenter(acquired.Token, "next-command"));
        }

        [Test]
        public void ExplicitLock_SurvivesPreflightDomainReload_AndAttachesToJob()
        {
            var owner = new TestJobIdentity("reload-owner", 11);
            var acquired = SharedEditorOperationLock.TryAcquire(
                "client", "dirty-preflight", isExplicit: true, ttlSeconds: 120);
            Assert.IsTrue(acquired.Acquired);

            SharedEditorOperationLock.ClearInMemoryForTests();
            SharedEditorOperationLock.EnsureInitialized();

            Assert.IsTrue(SharedEditorOperationLock.ValidateToken(acquired.Token));
            Assert.IsTrue(SharedEditorOperationLock.AttachToJob(acquired.Token, owner));
            Assert.IsTrue(SharedEditorOperationLock.GetState().IsAttachedToJob);
            Assert.AreEqual(owner.JobId, SharedEditorOperationLock.GetState().AttachedJobId);
        }

        [Test]
        public void ManageGameObject_RemainsOutsideHighRiskClassifier()
        {
            Assert.IsFalse(SharedEditorOperationLock.IsHighRiskTool("manage_gameobject", "create"));
            Assert.IsFalse(SharedEditorOperationLock.IsHighRiskTool("manage_gameobject", "modify"));
            Assert.IsTrue(SharedEditorOperationLock.IsHighRiskTool("run_tests", null));
        }

        [Test]
        public void AutomaticLease_DoesNotWriteAnEmptyPersistenceProjection()
        {
            int writes = 0;
            SharedEditorOperationLock.PersistSnapshotForTests = _ =>
            {
                writes++;
                return true;
            };

            var acquired = SharedEditorOperationLock.TryAcquire("auto", "refresh_unity", isExplicit: false);
            Assert.IsTrue(acquired.Acquired);
            Assert.IsTrue(SharedEditorOperationLock.ReleaseIfAutoLock(acquired.Token));
            Assert.AreEqual(0, writes);
        }

        [Test]
        public void PersistenceFailure_RollsBackEveryDurableMutationFamily()
        {
            SharedEditorOperationLock.PersistSnapshotForTests = _ => false;
            var failedAcquire = SharedEditorOperationLock.TryAcquire("client", "suite", isExplicit: true);
            Assert.IsFalse(failedAcquire.Acquired);
            Assert.IsTrue(failedAcquire.PersistenceFailed);
            Assert.IsFalse(SharedEditorOperationLock.GetState().Locked);

            SharedEditorOperationLock.PersistSnapshotForTests = _ => true;
            var acquired = SharedEditorOperationLock.TryAcquire("client", "suite", isExplicit: true);
            Assert.IsTrue(acquired.Acquired);
            var before = SharedEditorOperationLock.GetState();

            SharedEditorOperationLock.PersistSnapshotForTests = _ => false;
            Assert.IsFalse(SharedEditorOperationLock.Reenter(acquired.Token, "changed"));
            Assert.AreEqual(before.Reason, SharedEditorOperationLock.GetState().Reason);
            Assert.IsFalse(SharedEditorOperationLock.Extend(acquired.Token, 30));
            Assert.AreEqual(before.ExpiresAtUtc, SharedEditorOperationLock.GetState().ExpiresAtUtc);
            Assert.IsFalse(SharedEditorOperationLock.Release(acquired.Token));
            Assert.IsTrue(SharedEditorOperationLock.GetState().Locked);
            Assert.IsFalse(SharedEditorOperationLock.TryForceRelease(out var retained));
            Assert.IsTrue(retained.Locked);

            var owner = new TestJobIdentity("persist-owner", 9);
            Assert.IsFalse(SharedEditorOperationLock.AttachToJob(acquired.Token, owner));
            Assert.IsFalse(SharedEditorOperationLock.GetState().IsAttachedToJob);

            SharedEditorOperationLock.PersistSnapshotForTests = _ => true;
            Assert.IsTrue(SharedEditorOperationLock.AttachToJob(acquired.Token, owner));
            SharedEditorOperationLock.PersistSnapshotForTests = _ => false;
            Assert.IsFalse(SharedEditorOperationLock.CompleteAttachedJob(owner));
            Assert.IsTrue(SharedEditorOperationLock.GetState().IsAttachedToJob);

            SharedEditorOperationLock.PersistSnapshotForTests = _ => true;
            Assert.IsTrue(SharedEditorOperationLock.CompleteAttachedJob(owner));
            Assert.IsTrue(SharedEditorOperationLock.Release(acquired.Token));
        }
    }
}
