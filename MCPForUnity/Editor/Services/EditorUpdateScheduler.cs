using System;
using System.Collections.Concurrent;
using System.Threading;
using MCPForUnity.Editor.Helpers;
using UnityEditor;

namespace MCPForUnity.Editor.Services
{
    /// <summary>
    /// Delivers work that originated off the editor thread on a later editor update.
    /// Unlike delayCall, the update callback remains live in headless editors that
    /// do not consume the delay-call queue.
    /// </summary>
    [InitializeOnLoad]
    internal static class EditorUpdateScheduler
    {
        private const int DefaultMaxActionsPerTick = 32;

        private static readonly ConcurrentQueue<Action> Pending = new();
        private static int _isDraining;

        internal static int MaxActionsPerTickForTests { get; set; } = DefaultMaxActionsPerTick;
        internal static int PendingCountForTests => Pending.Count;

        static EditorUpdateScheduler()
        {
            EditorApplication.update += Drain;
        }

        internal static void Enqueue(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            Pending.Enqueue(action);
        }

        internal static void PumpForTests()
        {
            Drain();
        }

        internal static void ResetForTests()
        {
            while (Pending.TryDequeue(out _))
            {
            }
            MaxActionsPerTickForTests = DefaultMaxActionsPerTick;
            Interlocked.Exchange(ref _isDraining, 0);
        }

        private static void Drain()
        {
            if (Interlocked.Exchange(ref _isDraining, 1) != 0) return;

            try
            {
                int remaining = Math.Max(1, MaxActionsPerTickForTests);
                while (remaining-- > 0 && Pending.TryDequeue(out Action action))
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        McpLog.Warn($"[EditorUpdateScheduler] Update action failed: {ex.Message}");
                    }
                }
            }
            finally
            {
                Volatile.Write(ref _isDraining, 0);
            }
        }
    }
}
