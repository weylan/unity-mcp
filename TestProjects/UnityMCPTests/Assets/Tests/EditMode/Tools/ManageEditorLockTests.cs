using System;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Tools
{
    public class ManageEditorLockTests
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
        public void ForceRelease_WithAttachedPhysicalOwner_IsRejectedAndReportsFence()
        {
            var owner = new TestJobIdentity("physical-owner", 19);
            var acquired = SharedEditorOperationLock.TryAcquire("client", "test-suite", isExplicit: true);
            Assert.IsTrue(SharedEditorOperationLock.AttachToJob(acquired.Token, owner));

            object response = ManageEditorLock.HandleCommand(new JObject
            {
                ["action"] = "force_release"
            });
            var json = JObject.FromObject(response);

            Assert.IsFalse(json.Value<bool>("success"), json.ToString());
            Assert.AreEqual("editor_lock_attached", json.Value<string>("code"));
            Assert.AreEqual(owner.JobId, json["data"]?.Value<string>("physical_owner_job_id"));
            Assert.IsTrue(json["data"]?.Value<bool>("fence_active") ?? false);
            Assert.IsFalse(json["data"]?.Value<bool>("safe_to_start_new_run") ?? true);
            Assert.IsTrue(SharedEditorOperationLock.GetState().IsAttachedToJob);
        }

        [Test]
        public void Release_WithMatchingAttachedToken_ReportsAttachedFence()
        {
            var owner = new TestJobIdentity("release-owner", 20);
            var acquired = SharedEditorOperationLock.TryAcquire("client", "test-suite", isExplicit: true);
            Assert.IsTrue(SharedEditorOperationLock.AttachToJob(acquired.Token, owner));

            object response = ManageEditorLock.HandleCommand(new JObject
            {
                ["action"] = "release",
                ["token"] = acquired.Token,
            });
            var json = JObject.FromObject(response);

            Assert.IsFalse(json.Value<bool>("success"), json.ToString());
            Assert.AreEqual(SharedEditorOperationLock.AttachedCode, json.Value<string>("code"));
            Assert.AreEqual(owner.JobId, json["data"]?.Value<string>("physical_owner_job_id"));
            Assert.IsTrue(SharedEditorOperationLock.GetState().IsAttachedToJob);
        }
    }
}
