using System;
using System.Collections.Generic;
using MCPForUnity.Editor.Services;
using NUnit.Framework;

namespace MCPForUnityTests.Editor.Services
{
    public class EditorUpdateSchedulerTests
    {
        [SetUp]
        public void SetUp()
        {
            EditorUpdateScheduler.ResetForTests();
        }

        [TearDown]
        public void TearDown()
        {
            EditorUpdateScheduler.ResetForTests();
        }

        [Test]
        public void UpdateQueue_DrainsFifoBoundedAndExceptionIsolated()
        {
            EditorUpdateScheduler.MaxActionsPerTickForTests = 2;
            var observed = new List<int>();

            EditorUpdateScheduler.Enqueue(() => observed.Add(1));
            EditorUpdateScheduler.Enqueue(() => throw new InvalidOperationException("injected scheduler failure"));
            EditorUpdateScheduler.Enqueue(() => observed.Add(3));
            EditorUpdateScheduler.Enqueue(() => observed.Add(4));

            EditorUpdateScheduler.PumpForTests();
            CollectionAssert.AreEqual(new[] { 1 }, observed);
            Assert.AreEqual(2, EditorUpdateScheduler.PendingCountForTests,
                "The update pump must enforce its per-tick bound.");

            EditorUpdateScheduler.PumpForTests();
            CollectionAssert.AreEqual(new[] { 1, 3, 4 }, observed);
            Assert.AreEqual(0, EditorUpdateScheduler.PendingCountForTests,
                "One failing action must not strand later FIFO work.");
        }
    }
}
