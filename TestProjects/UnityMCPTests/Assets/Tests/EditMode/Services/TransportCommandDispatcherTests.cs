using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Services.Transport;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace MCPForUnityTests.Editor.Services
{
    public class TransportCommandDispatcherTests
    {
        private bool _hadMaxCommandsPref;
        private int _oldMaxCommandsPref;
        private bool _hadBudgetPref;
        private int _oldBudgetPref;

        [SetUp]
        public void SetUp()
        {
            _hadMaxCommandsPref = EditorPrefs.HasKey("MCPForUnity.PlayModeMaxCommandsPerPump");
            _oldMaxCommandsPref = EditorPrefs.GetInt("MCPForUnity.PlayModeMaxCommandsPerPump", 16);
            _hadBudgetPref = EditorPrefs.HasKey("MCPForUnity.PlayModePumpBudgetMs");
            _oldBudgetPref = EditorPrefs.GetInt("MCPForUnity.PlayModePumpBudgetMs", 4);
            TransportCommandDispatcher.ResetForTests();
            TransportCommandDispatcher.SlicingActiveOverrideForTests = null;
            TransportCommandDispatcher.DelayCallRegistrarForTests = null;
        }

        [TearDown]
        public void TearDown()
        {
            if (_hadMaxCommandsPref)
                EditorPrefs.SetInt("MCPForUnity.PlayModeMaxCommandsPerPump", _oldMaxCommandsPref);
            else
                EditorPrefs.DeleteKey("MCPForUnity.PlayModeMaxCommandsPerPump");

            if (_hadBudgetPref)
                EditorPrefs.SetInt("MCPForUnity.PlayModePumpBudgetMs", _oldBudgetPref);
            else
                EditorPrefs.DeleteKey("MCPForUnity.PlayModePumpBudgetMs");

            TransportCommandDispatcher.DelayCallRegistrarForTests = null;
            TransportCommandDispatcher.SlicingActiveOverrideForTests = null;
            TransportCommandDispatcher.ResetForTests();
        }

        [Test]
        public void ResolveReadyLimit_EditMode_DoesNotThrottle()
        {
            Assert.AreEqual(10,
                TransportCommandDispatcher.ResolveReadyLimitForTests(
                    pendingCount: 10,
                    sliceInPlayMode: false,
                    maxCommandsPerPump: 4));
        }

        [Test]
        public void ResolveReadyLimit_PlayMode_CapsBatchSize()
        {
            Assert.AreEqual(4,
                TransportCommandDispatcher.ResolveReadyLimitForTests(
                    pendingCount: 10,
                    sliceInPlayMode: true,
                    maxCommandsPerPump: 4));
        }

        [Test]
        public void ResolveReadyLimit_PlayMode_PreservesFifoProgress()
        {
            Assert.AreEqual(1,
                TransportCommandDispatcher.ResolveReadyLimitForTests(
                    pendingCount: 10,
                    sliceInPlayMode: true,
                    maxCommandsPerPump: 0));
        }

        [Test]
        public void ResolveReadyLimit_PlayMode_DoesNotExceedPendingCount()
        {
            Assert.AreEqual(2,
                TransportCommandDispatcher.ResolveReadyLimitForTests(
                    pendingCount: 2,
                    sliceInPlayMode: true,
                    maxCommandsPerPump: 4));
        }

        [Test]
        public void SelectReadyIdsForTests_UsesQueuedOrder_NotDictionaryOrder()
        {
            var selected = TransportCommandDispatcher.SelectReadyIdsForTests(
                queuedIds: new[] { "cmd-3", "cmd-1", "cmd-2" },
                pendingIds: new HashSet<string> { "cmd-1", "cmd-2", "cmd-3" },
                executingIds: new HashSet<string>(),
                readyLimit: 2);

            CollectionAssert.AreEqual(new[] { "cmd-3", "cmd-1" }, selected);
        }

        [Test]
        public void SelectReadyIdsForTests_SkipsExecutingAndStaleEntries()
        {
            var selected = TransportCommandDispatcher.SelectReadyIdsForTests(
                queuedIds: new[] { "stale", "executing", "ready-1", "ready-2" },
                pendingIds: new HashSet<string> { "executing", "ready-1", "ready-2" },
                executingIds: new HashSet<string> { "executing" },
                readyLimit: 2);

            CollectionAssert.AreEqual(new[] { "ready-1", "ready-2" }, selected);
        }

        [Test]
        public void ExecuteCommandJsonAsync_ExecuteCodeArgs_UsesRealDispatcherPath()
        {
            TransportCommandDispatcher.SlicingActiveOverrideForTests = () => true;

            var task = TransportCommandDispatcher.ExecuteCommandJsonAsync(
                CommandJson("execute_code", new JObject
                {
                    ["action"] = "execute",
                    ["code"] = "return string.Join(\"|\", __mcpArgs);",
                    ["args"] = new JArray("via", "dispatcher")
                }),
                CancellationToken.None);

            Assert.IsFalse(task.IsCompleted, "Slicing override should defer execution until ProcessQueueForTests pumps.");
            TransportCommandDispatcher.ProcessQueueForTests();

            var response = JObject.Parse(WaitForTask(task));
            Assert.AreEqual("success", response.Value<string>("status"), response.ToString());
            Assert.AreEqual("via|dispatcher", response["result"]["data"]["result"].Value<string>());
        }

        [Test]
        public void ProcessQueueForTests_SlicingProcessesBatchInFifoOrder()
        {
            TransportCommandDispatcher.SlicingActiveOverrideForTests = () => true;
            EditorPrefs.SetInt("MCPForUnity.PlayModeMaxCommandsPerPump", 2);

            var first = TransportCommandDispatcher.ExecuteCommandJsonAsync("ping", CancellationToken.None);
            var second = TransportCommandDispatcher.ExecuteCommandJsonAsync("ping", CancellationToken.None);
            var third = TransportCommandDispatcher.ExecuteCommandJsonAsync("ping", CancellationToken.None);

            TransportCommandDispatcher.ProcessQueueForTests();

            Assert.IsTrue(first.IsCompleted, "First queued command should be processed in the first batch.");
            Assert.IsTrue(second.IsCompleted, "Second queued command should be processed in the first batch.");
            Assert.IsFalse(third.IsCompleted, "Third queued command should remain deferred by the batch cap.");

            TransportCommandDispatcher.ProcessQueueForTests();
            Assert.IsTrue(third.IsCompleted, "Deferred command should run on the next pump.");
        }

        [Test]
        public void ProcessQueueForTests_BudgetYield_ResetsSkippedCommands()
        {
            TransportCommandDispatcher.SlicingActiveOverrideForTests = () => true;
            EditorPrefs.SetInt("MCPForUnity.PlayModeMaxCommandsPerPump", 16);
            EditorPrefs.SetInt("MCPForUnity.PlayModePumpBudgetMs", 1);

            var slow = TransportCommandDispatcher.ExecuteCommandJsonAsync(
                CommandJson("test_dispatcher_slow", new JObject { ["sleepMs"] = 20 }),
                CancellationToken.None);
            var fast = TransportCommandDispatcher.ExecuteCommandJsonAsync("ping", CancellationToken.None);

            TransportCommandDispatcher.ProcessQueueForTests();

            Assert.IsTrue(slow.IsCompleted, "The first command always gets one progress slot.");
            Assert.IsFalse(fast.IsCompleted, "Budget should defer the second command after the slow first command.");

            TransportCommandDispatcher.ProcessQueueForTests();
            Assert.IsTrue(fast.IsCompleted,
                "Deferred commands must have IsExecuting reset so the next pump can process them.");
        }

        [Test]
        public void CancelPending_RemovesOnlyCanceledIdFromPendingOrder()
        {
            TransportCommandDispatcher.SlicingActiveOverrideForTests = () => true;
            using var cts = new CancellationTokenSource();

            var canceled = TransportCommandDispatcher.ExecuteCommandJsonAsync("ping", cts.Token);
            var survivor = TransportCommandDispatcher.ExecuteCommandJsonAsync("ping", CancellationToken.None);
            var before = TransportCommandDispatcher.PendingOrderSnapshotForTests();
            Assert.AreEqual(2, before.Count, "Test requires a survivor so count==0 Clear cannot mask stale order entries.");

            cts.Cancel();
            Assert.IsTrue(WaitUntil(() => canceled.IsCanceled || canceled.IsCompleted),
                "Canceled command should complete after cancellation is requested.");

            var after = TransportCommandDispatcher.PendingOrderSnapshotForTests();
            CollectionAssert.DoesNotContain(after, before[0], "Canceled command id must be removed from PendingOrder.");
            CollectionAssert.Contains(after, before[1], "Survivor command id must stay queued.");
            Assert.IsFalse(survivor.IsCompleted);
        }

        [Test]
        public void RemovePending_RemovesCompletedIdFromPendingOrderWhileSurvivorRemains()
        {
            TransportCommandDispatcher.SlicingActiveOverrideForTests = () => true;
            EditorPrefs.SetInt("MCPForUnity.PlayModeMaxCommandsPerPump", 1);

            var completed = TransportCommandDispatcher.ExecuteCommandJsonAsync("ping", CancellationToken.None);
            var survivor = TransportCommandDispatcher.ExecuteCommandJsonAsync("ping", CancellationToken.None);
            var before = TransportCommandDispatcher.PendingOrderSnapshotForTests();
            Assert.AreEqual(2, before.Count);

            TransportCommandDispatcher.ProcessQueueForTests();

            Assert.IsTrue(completed.IsCompleted);
            Assert.IsFalse(survivor.IsCompleted);
            var after = TransportCommandDispatcher.PendingOrderSnapshotForTests();
            CollectionAssert.DoesNotContain(after, before[0], "Completed command id must be removed from PendingOrder.");
            CollectionAssert.Contains(after, before[1], "Survivor command id must stay queued.");
        }

        [Test]
        public void AsyncCleanup_WhenDelayCallRegistrationThrows_RemovesPendingAndWarns()
        {
            TransportCommandDispatcher.SlicingActiveOverrideForTests = () => true;
            TransportCommandDispatcher.DelayCallRegistrarForTests = _ => throw new InvalidOperationException("registrar failed");
            LogAssert.Expect(LogType.Warning, new Regex("Async cleanup.*registrar failed"));

            var task = TransportCommandDispatcher.ExecuteCommandJsonAsync(
                CommandJson("test_dispatcher_async", new JObject { ["delayMs"] = 0 }),
                CancellationToken.None);

            TransportCommandDispatcher.ProcessQueueForTests();

            Assert.IsTrue(WaitUntil(() => task.IsCompleted), "Async command should complete its TCS.");
            Assert.IsTrue(WaitUntil(() => TransportCommandDispatcher.PendingCountForTests == 0),
                "Cleanup fallback must remove pending even if delayCall registration throws.");
        }

        private static string CommandJson(string type, JObject parameters)
        {
            return new JObject
            {
                ["type"] = type,
                ["params"] = parameters ?? new JObject()
            }.ToString(Formatting.None);
        }

        private static bool WaitUntil(Func<bool> condition, int timeoutMs = 1000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                    return true;
                Thread.Sleep(10);
            }
            return condition();
        }

        private static T WaitForTask<T>(Task<T> task, int timeoutMs = 5000)
        {
            Assert.IsTrue(task.Wait(timeoutMs), "Task did not complete within the timeout.");
            return task.GetAwaiter().GetResult();
        }
    }

    [McpForUnityTool("test_dispatcher_slow")]
    public static class TestDispatcherSlowTool
    {
        public static object HandleCommand(JObject parameters)
        {
            Thread.Sleep(parameters?.Value<int?>("sleepMs") ?? 20);
            return new { ok = true };
        }
    }

    [McpForUnityTool("test_dispatcher_async")]
    public static class TestDispatcherAsyncTool
    {
        public static Task<object> HandleCommand(JObject parameters)
        {
            int delayMs = parameters?.Value<int?>("delayMs") ?? 1;
            if (delayMs <= 0)
            {
                return Task.FromResult<object>(new { ok = true });
            }

            return Task.Delay(delayMs).ContinueWith<object>(_ => new { ok = true });
        }
    }
}
