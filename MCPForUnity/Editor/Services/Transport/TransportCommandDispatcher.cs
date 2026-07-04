using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Models;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace MCPForUnity.Editor.Services.Transport
{
    /// <summary>
    /// Centralised command execution pipeline shared by all transport implementations.
    /// Guarantees that MCP commands are executed on the Unity main thread while preserving
    /// the legacy response format expected by the server.
    /// </summary>
    [InitializeOnLoad]
    internal static class TransportCommandDispatcher
    {
        private static SynchronizationContext _mainThreadContext;
        private static int _mainThreadId;
        private static int _processingFlag;

        // Opt-in diagnostic (default off): when true, skip the per-command QueuePlayerLoopUpdate nudge
        // while in Play Mode (the player loop already runs every frame there). Lets a run A/B whether
        // the forced tick contributes to per-call stutter. Toggle via EditorPrefs "MCPForUnity.GatePlayerLoopInPlay".
        private static bool GatePlayerLoopInPlay => EditorPrefs.GetBool("MCPForUnity.GatePlayerLoopInPlay", false);
        private const int DefaultPlayModeMaxCommandsPerPump = 16;
        private const int DefaultPlayModePumpBudgetMs = 4;
        private static bool SlicePendingCommandsInPlayMode =>
            EditorPrefs.GetBool("MCPForUnity.SlicePendingCommandsInPlayMode", true);
        private static int PlayModeMaxCommandsPerPump =>
            Math.Max(1, EditorPrefs.GetInt("MCPForUnity.PlayModeMaxCommandsPerPump", DefaultPlayModeMaxCommandsPerPump));
        private static int PlayModePumpBudgetMs =>
            Math.Max(1, EditorPrefs.GetInt("MCPForUnity.PlayModePumpBudgetMs", DefaultPlayModePumpBudgetMs));

        private sealed class PendingCommand
        {
            public PendingCommand(
                string commandJson,
                TaskCompletionSource<string> completionSource,
                CancellationToken cancellationToken,
                CancellationTokenRegistration registration)
            {
                CommandJson = commandJson;
                CompletionSource = completionSource;
                CancellationToken = cancellationToken;
                CancellationRegistration = registration;
                QueuedAt = DateTime.UtcNow;
            }

            public string CommandJson { get; }
            public TaskCompletionSource<string> CompletionSource { get; }
            public CancellationToken CancellationToken { get; }
            public CancellationTokenRegistration CancellationRegistration { get; }
            public bool IsExecuting { get; set; }
            public DateTime QueuedAt { get; }

            public void Dispose()
            {
                CancellationRegistration.Dispose();
            }

            public void TrySetResult(string payload)
            {
                CompletionSource.TrySetResult(payload);
            }

            public void TrySetCanceled()
            {
                CompletionSource.TrySetCanceled(CancellationToken);
            }
        }

        private static readonly Dictionary<string, PendingCommand> Pending = new();
        private static readonly List<string> PendingOrder = new();
        private static readonly object PendingLock = new();
        private static bool updateHooked;
        private static bool initialised;

        static TransportCommandDispatcher()
        {
            // Ensure this runs on the Unity main thread at editor load.
            _mainThreadContext = SynchronizationContext.Current;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;

            EnsureInitialised();

            // Always keep the update hook installed so commands arriving from background
            // websocket tasks don't depend on a background-thread event subscription.
            if (!updateHooked)
            {
                updateHooked = true;
                EditorApplication.update += ProcessQueue;
            }
        }

        /// <summary>
        /// Schedule a command for execution on the Unity main thread and await its JSON response.
        /// </summary>
        public static Task<string> ExecuteCommandJsonAsync(string commandJson, CancellationToken cancellationToken)
        {
            if (commandJson is null)
            {
                throw new ArgumentNullException(nameof(commandJson));
            }

            EnsureInitialised();

            var id = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            var registration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(() => CancelPending(id, cancellationToken))
                : default;

            var pending = new PendingCommand(commandJson, tcs, cancellationToken, registration);

            lock (PendingLock)
            {
                Pending[id] = pending;
                PendingOrder.Add(id);
            }

            // Proactively wake up the main thread execution loop. This improves responsiveness
            // in scenarios where EditorApplication.update is throttled or temporarily not firing
            // (e.g., Unity unfocused, compiling, or during domain reload transitions).
            RequestMainThreadPump();

            return tcs.Task;
        }

        internal static Task<T> RunOnMainThreadAsync<T>(Func<T> func, CancellationToken cancellationToken)
        {
            if (func is null)
            {
                throw new ArgumentNullException(nameof(func));
            }

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

            var registration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken))
                : default;

            void Invoke()
            {
                try
                {
                    if (tcs.Task.IsCompleted)
                    {
                        return;
                    }

                    var result = func();
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    registration.Dispose();
                }
            }

            // Best-effort nudge: if we're posting from a background thread (e.g., websocket receive),
            // encourage Unity to run a loop iteration so the posted callback can execute even when unfocused.
            try { EditorApplication.QueuePlayerLoopUpdate(); } catch { }

            if (_mainThreadContext != null && Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                _mainThreadContext.Post(_ => Invoke(), null);
                return tcs.Task;
            }

            Invoke();
            return tcs.Task;
        }

        private static void RequestMainThreadPump()
        {
            void Pump()
            {
                try
                {
                    // Hint Unity to run a loop iteration soon. In Play Mode the player loop already
                    // runs every frame, so this per-command nudge can perturb frame pacing; the opt-in
                    // GatePlayerLoopInPlay diagnostic (default off) lets a run A/B its contribution.
                    if (!(GatePlayerLoopInPlay && EditorApplication.isPlaying))
                        EditorApplication.QueuePlayerLoopUpdate();
                }
                catch
                {
                    // Best-effort only.
                }

                // In Play Mode slicing, let the installed update hook consume one batch per frame.
                if (!IsPlayModeQueueSlicingActive())
                {
                    ProcessQueue();
                }
            }

            if (_mainThreadContext != null && Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                _mainThreadContext.Post(_ => Pump(), null);
                return;
            }

            Pump();
        }

        private static void EnsureInitialised()
        {
            if (initialised)
            {
                return;
            }

            CommandRegistry.Initialize();
            initialised = true;
        }

        private static void HookUpdate()
        {
            // Deprecated: we keep the update hook installed permanently (see static ctor).
            if (updateHooked) return;
            updateHooked = true;
            EditorApplication.update += ProcessQueue;
        }

        private static void UnhookUpdateIfIdle()
        {
            // Intentionally no-op: keep update hook installed so background commands always process.
            // This avoids "must focus Unity to re-establish contact" edge cases.
            return;
        }

        private static void ProcessQueue()
        {
            if (Interlocked.Exchange(ref _processingFlag, 1) == 1)
            {
                return;
            }

            try
            {
                List<(string id, PendingCommand pending)> ready;
                bool sliceInPlayMode = IsPlayModeQueueSlicingActive();

                lock (PendingLock)
                {
                    // Early exit inside lock to prevent per-frame List allocations (GitHub issue #577)
                    if (Pending.Count == 0)
                    {
                        PendingOrder.Clear();
                        return;
                    }

                    int readyLimit = ResolveReadyLimit(Pending.Count, sliceInPlayMode, PlayModeMaxCommandsPerPump);
                    ready = new List<(string, PendingCommand)>(Math.Min(Pending.Count, readyLimit));
                    PrunePendingOrderLocked();
                    for (int i = 0; i < PendingOrder.Count && ready.Count < readyLimit; i++)
                    {
                        var id = PendingOrder[i];
                        if (!Pending.TryGetValue(id, out var pending) || pending.IsExecuting)
                        {
                            continue;
                        }

                        pending.IsExecuting = true;
                        ready.Add((id, pending));
                    }

                    if (ready.Count == 0)
                    {
                        UnhookUpdateIfIdle();
                        return;
                    }
                }

                var pumpSw = sliceInPlayMode ? System.Diagnostics.Stopwatch.StartNew() : null;
                for (int i = 0; i < ready.Count; i++)
                {
                    if (ShouldYieldPlayModePump(i, pumpSw))
                    {
                        ResetExecuting(ready, i);
                        break;
                    }

                    var (id, pending) = ready[i];
                    ProcessCommand(id, pending);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _processingFlag, 0);
            }
        }

        private static bool IsPlayModeQueueSlicingActive()
        {
            return SlicePendingCommandsInPlayMode && EditorApplication.isPlaying;
        }

        private static void PrunePendingOrderLocked()
        {
            for (int i = PendingOrder.Count - 1; i >= 0; i--)
            {
                if (!Pending.ContainsKey(PendingOrder[i]))
                {
                    PendingOrder.RemoveAt(i);
                }
            }
        }

        private static int ResolveReadyLimit(int pendingCount, bool sliceInPlayMode, int maxCommandsPerPump)
        {
            if (pendingCount <= 0)
            {
                return 0;
            }

            if (!sliceInPlayMode)
            {
                return pendingCount;
            }

            return Math.Min(pendingCount, Math.Max(1, maxCommandsPerPump));
        }

        private static bool ShouldYieldPlayModePump(int processedCount, System.Diagnostics.Stopwatch pumpSw)
        {
            if (pumpSw == null || processedCount <= 0)
            {
                return false;
            }

            return processedCount >= PlayModeMaxCommandsPerPump
                   || pumpSw.ElapsedMilliseconds >= PlayModePumpBudgetMs;
        }

        private static void ResetExecuting(List<(string id, PendingCommand pending)> ready, int startIndex)
        {
            lock (PendingLock)
            {
                for (int i = startIndex; i < ready.Count; i++)
                {
                    var (id, pending) = ready[i];
                    if (Pending.TryGetValue(id, out var current) && ReferenceEquals(current, pending))
                    {
                        current.IsExecuting = false;
                    }
                }
            }
        }

        internal static int ResolveReadyLimitForTests(int pendingCount, bool sliceInPlayMode, int maxCommandsPerPump)
        {
            return ResolveReadyLimit(pendingCount, sliceInPlayMode, maxCommandsPerPump);
        }

        internal static IReadOnlyList<string> SelectReadyIdsForTests(
            IReadOnlyList<string> queuedIds,
            ISet<string> pendingIds,
            ISet<string> executingIds,
            int readyLimit)
        {
            var selected = new List<string>();
            if (queuedIds == null || pendingIds == null || readyLimit <= 0)
            {
                return selected;
            }

            executingIds ??= new HashSet<string>();
            foreach (var id in queuedIds)
            {
                if (selected.Count >= readyLimit)
                {
                    break;
                }

                if (!pendingIds.Contains(id) || executingIds.Contains(id))
                {
                    continue;
                }

                selected.Add(id);
            }

            return selected;
        }

        private static void ProcessCommand(string id, PendingCommand pending)
        {
            if (pending.CancellationToken.IsCancellationRequested)
            {
                RemovePending(id, pending);
                pending.TrySetCanceled();
                return;
            }

            string commandText = pending.CommandJson?.Trim();
            if (string.IsNullOrEmpty(commandText))
            {
                pending.TrySetResult(SerializeError("Empty command received"));
                RemovePending(id, pending);
                return;
            }

            if (string.Equals(commandText, "ping", StringComparison.OrdinalIgnoreCase))
            {
                var pingResponse = new
                {
                    status = "success",
                    result = new { message = "pong" }
                };
                pending.TrySetResult(JsonConvert.SerializeObject(pingResponse));
                RemovePending(id, pending);
                return;
            }

            try
            {
                Command command;
                try
                {
                    command = JsonConvert.DeserializeObject<Command>(commandText);
                }
                catch (JsonException)
                {
                    // Single-parse fast path: malformed JSON surfaces here instead of a separate
                    // IsValidJson -> JToken.Parse pre-pass that double-parsed every command on the
                    // Unity main thread. Error contract (message + receivedText preview) preserved.
                    var invalidJsonResponse = new
                    {
                        status = "error",
                        error = "Invalid JSON format",
                        receivedText = commandText.Length > 50 ? commandText[..50] + "..." : commandText
                    };
                    pending.TrySetResult(JsonConvert.SerializeObject(invalidJsonResponse));
                    RemovePending(id, pending);
                    return;
                }

                if (command == null)
                {
                    pending.TrySetResult(SerializeError("Command deserialized to null", "Unknown", commandText));
                    RemovePending(id, pending);
                    return;
                }

                if (string.IsNullOrWhiteSpace(command.type))
                {
                    pending.TrySetResult(SerializeError("Command type cannot be empty"));
                    RemovePending(id, pending);
                    return;
                }

                if (string.Equals(command.type, "ping", StringComparison.OrdinalIgnoreCase))
                {
                    var pingResponse = new
                    {
                        status = "success",
                        result = new { message = "pong" }
                    };
                    pending.TrySetResult(JsonConvert.SerializeObject(pingResponse));
                    RemovePending(id, pending);
                    return;
                }

                var parameters = command.@params ?? new JObject();

                // Block execution of disabled resources
                var resourceMeta = MCPServiceLocator.ResourceDiscovery.GetResourceMetadata(command.type);
                if (resourceMeta != null && !MCPServiceLocator.ResourceDiscovery.IsResourceEnabled(command.type))
                {
                    pending.TrySetResult(SerializeError(
                        $"Resource '{command.type}' is disabled in the Unity Editor."));
                    RemovePending(id, pending);
                    return;
                }

                // Block execution of disabled tools
                var toolMeta = MCPServiceLocator.ToolDiscovery.GetToolMetadata(command.type);
                if (toolMeta != null && !MCPServiceLocator.ToolDiscovery.IsToolEnabled(command.type))
                {
                    pending.TrySetResult(SerializeError(
                        $"Tool '{command.type}' is disabled in the Unity Editor."));
                    RemovePending(id, pending);
                    return;
                }

                var logType = resourceMeta != null ? "resource" : toolMeta != null ? "tool" : "unknown";
                var sw = McpLogRecord.IsEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
                // Queue latency: how long this command waited in Pending before the main-thread pump
                // ran it. High queueMs with low handler ms => commands backing up on the main thread.
                long queueMs = McpLogRecord.IsEnabled
                    ? (long)(DateTime.UtcNow - pending.QueuedAt).TotalMilliseconds
                    : -1;

                string autoLockToken = null;
                string lockAction = parameters?.Value<string>("action");
                bool isHighRiskTool = SharedEditorOperationLock.IsHighRiskTool(command.type, lockAction);
                string clientToken = parameters?.Value<string>("editor_lock_token")
                                  ?? parameters?.Value<string>("editorLockToken");
                bool hasValidEditorLockToken = false;

                if (isHighRiskTool && !string.IsNullOrEmpty(clientToken))
                {
                    hasValidEditorLockToken = SharedEditorOperationLock.Reenter(
                        clientToken,
                        $"{command.type}:{lockAction}");
                    if (!hasValidEditorLockToken)
                    {
                        var tokenErrResp = SharedEditorOperationLock.BuildTokenInvalidResponse(clientToken, command.type);
                        var tokenErrResponse = new { status = "success", result = tokenErrResp };
                        pending.TrySetResult(JsonConvert.SerializeObject(tokenErrResponse));
                        RemovePending(id, pending);
                        return;
                    }
                }

                var guardDecision = SharedEditorCommandGuard.Evaluate(command.type, parameters, hasValidEditorLockToken);
                if (!guardDecision.Allowed)
                {
                    SharedEditorCommandGuard.LogDecision(guardDecision, parameters, logType, sw?.ElapsedMilliseconds ?? 0);
                    var blockedResponse = new { status = "success", result = guardDecision.ToErrorResponse() };
                    pending.TrySetResult(JsonConvert.SerializeObject(blockedResponse));
                    RemovePending(id, pending);
                    return;
                }

                if (guardDecision.WarnOnly)
                {
                    SharedEditorCommandGuard.LogDecision(guardDecision, parameters, logType, sw?.ElapsedMilliseconds ?? 0);
                }

                if (isHighRiskTool)
                {
                    if (!hasValidEditorLockToken)
                    {
                        var lockResult = SharedEditorOperationLock.TryAcquire(
                            "auto", $"{command.type}:{lockAction}", isExplicit: false);

                        if (!lockResult.Acquired)
                        {
                            var busyResp = SharedEditorOperationLock.BuildBusyResponse(lockResult.BusyHolder, command.type);
                            var busyResponse = new { status = "success", result = busyResp };
                            pending.TrySetResult(JsonConvert.SerializeObject(busyResponse));
                            RemovePending(id, pending);
                            return;
                        }
                        autoLockToken = lockResult.Token;
                    }
                }

                var result = CommandRegistry.ExecuteCommand(command.type, parameters, pending.CompletionSource);

                if (result == null)
                {
                    // Async command – cleanup after completion on next editor frame to preserve order.
                    var capturedType = command.type;
                    var capturedParams = parameters;
                    var capturedLogType = logType;
                    var capturedAutoLockToken = autoLockToken;
                    var capturedQueueMs = queueMs;
                    pending.CompletionSource.Task.ContinueWith(t =>
                    {
                        SharedEditorOperationLock.ReleaseIfAutoLock(capturedAutoLockToken);
                        sw?.Stop();
                        var logStatus = "SUCCESS";
                        string logError = null;
                        if (t.IsFaulted)
                        {
                            logStatus = "ERROR";
                            logError = t.Exception?.InnerException?.Message;
                        }
                        else if (t.IsCompletedSuccessfully && t.Result != null)
                        {
                            try
                            {
                                var resultObj = JObject.Parse(t.Result);
                                if (string.Equals(resultObj.Value<string>("status"), "error", StringComparison.OrdinalIgnoreCase))
                                {
                                    logStatus = "ERROR";
                                    logError = resultObj.Value<string>("error");
                                }
                            }
                            catch { }
                        }
                        McpLogRecord.Log(capturedType, capturedParams, capturedLogType,
                            logStatus, sw?.ElapsedMilliseconds ?? 0, logError, capturedQueueMs);
                        EditorApplication.delayCall += () => RemovePending(id, pending);
                    }, TaskScheduler.Default);
                    return;
                }

                SharedEditorOperationLock.ReleaseIfAutoLock(autoLockToken);
                sw?.Stop();

                string syncLogStatus = "SUCCESS";
                string syncLogError = null;
                if (result is ErrorResponse errResp)
                {
                    syncLogStatus = "ERROR";
                    syncLogError = errResp.Error;
                }
                McpLogRecord.Log(command.type, parameters, logType, syncLogStatus, sw?.ElapsedMilliseconds ?? 0, syncLogError, queueMs);

                var response = new { status = "success", result };
                pending.TrySetResult(JsonConvert.SerializeObject(response));
                RemovePending(id, pending);
            }
            catch (Exception ex)
            {
                McpLog.Error($"Error processing command: {ex.Message}\n{ex.StackTrace}");
                pending.TrySetResult(SerializeError(ex.Message, "Unknown (error during processing)", ex.StackTrace));
                RemovePending(id, pending);
            }
        }

        private static void CancelPending(string id, CancellationToken token)
        {
            PendingCommand pending = null;
            lock (PendingLock)
            {
                if (Pending.Remove(id, out pending))
                {
                    if (Pending.Count == 0)
                    {
                        PendingOrder.Clear();
                    }

                    UnhookUpdateIfIdle();
                }
            }

            pending?.TrySetCanceled();
            pending?.Dispose();
        }

        private static void RemovePending(string id, PendingCommand pending)
        {
            lock (PendingLock)
            {
                Pending.Remove(id);
                if (Pending.Count == 0)
                {
                    PendingOrder.Clear();
                }

                UnhookUpdateIfIdle();
            }

            pending.Dispose();
        }

        private static string SerializeError(string message, string commandType = null, string stackTrace = null)
        {
            var errorResponse = new
            {
                status = "error",
                error = message,
                command = commandType ?? "Unknown",
                stackTrace
            };
            return JsonConvert.SerializeObject(errorResponse);
        }
    }
}
