using System;
using UnityEditor.TestTools.TestRunner.Api;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Thread-safe, minimal shared status for Unity Test Runner execution.
    /// Used by editor readiness snapshots so callers can avoid starting overlapping runs.
    /// </summary>
    internal static class TestRunStatus
    {
        internal readonly struct Snapshot
        {
            public Snapshot(
                bool isRunning,
                TestMode? mode,
                long? startedUnixMs,
                long? finishedUnixMs)
            {
                IsRunning = isRunning;
                Mode = mode;
                StartedUnixMs = startedUnixMs;
                FinishedUnixMs = finishedUnixMs;
            }

            public bool IsRunning { get; }
            public TestMode? Mode { get; }
            public long? StartedUnixMs { get; }
            public long? FinishedUnixMs { get; }
        }

        private static readonly object LockObj = new();

        private static bool _isRunning;
        private static TestMode? _mode;
        private static long? _startedUnixMs;
        private static long? _finishedUnixMs;

        public static bool IsRunning
        {
            get => GetSnapshot().IsRunning;
        }

        public static TestMode? Mode
        {
            get => GetSnapshot().Mode;
        }

        public static long? StartedUnixMs
        {
            get => GetSnapshot().StartedUnixMs;
        }

        public static long? FinishedUnixMs
        {
            get => GetSnapshot().FinishedUnixMs;
        }

        internal static Snapshot GetSnapshot()
        {
            TestJobManager.EnsureInitialized();
            lock (LockObj)
            {
                return new Snapshot(_isRunning, _mode, _startedUnixMs, _finishedUnixMs);
            }
        }

        public static void MarkStarted(TestMode mode)
        {
            MarkStarted(mode, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        internal static void MarkStarted(TestMode mode, long startedUnixMs)
        {
            lock (LockObj)
            {
                _isRunning = true;
                _mode = mode;
                _startedUnixMs = startedUnixMs;
                _finishedUnixMs = null;
            }
        }

        internal static void RehydrateRunning(TestMode mode, long startedUnixMs)
        {
            MarkStarted(mode, startedUnixMs);
        }

        public static void MarkFinished()
        {
            lock (LockObj)
            {
                _isRunning = false;
                _finishedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _mode = null;
            }
        }

        internal static void ResetForTests()
        {
            lock (LockObj)
            {
                _isRunning = false;
                _mode = null;
                _startedUnixMs = null;
                _finishedUnixMs = null;
            }
        }
    }
}


