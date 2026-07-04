using System.Collections.Generic;
using MCPForUnity.Editor.Services.Transport;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services
{
    public class TransportCommandDispatcherTests
    {
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
    }
}
