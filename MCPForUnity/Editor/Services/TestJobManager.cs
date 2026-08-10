using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditorInternal;
using UnityEditor.TestTools.TestRunner.Api;

namespace MCPForUnity.Editor.Services
{
    internal enum TestJobStatus
    {
        Running,
        Succeeded,
        Failed
    }

    internal enum TestJobPhase
    {
        Queued,
        Dispatching,
        AwaitingRunStarted,
        Running,
        Terminal
    }

    internal sealed class TestJobFailure
    {
        public string FullName { get; set; }
        public string Message { get; set; }
    }

    internal sealed class TestJob
    {
        public string JobId { get; set; }
        public long Generation { get; set; }
        public TestJobStatus Status { get; set; }
        public TestJobPhase Phase { get; set; }
        public string Mode { get; set; }
        public long StartedUnixMs { get; set; }
        public long? AwaitingRunStartedSinceUnixMs { get; set; }
        public long? FinishedUnixMs { get; set; }
        public long? PhysicalFinishedUnixMs { get; set; }
        public long LastUpdateUnixMs { get; set; }
        public int? TotalTests { get; set; }
        public int CompletedTests { get; set; }
        public string CurrentTestFullName { get; set; }
        public long? CurrentTestStartedUnixMs { get; set; }
        public string LastFinishedTestFullName { get; set; }
        public long? LastFinishedUnixMs { get; set; }
        public List<TestJobFailure> FailuresSoFar { get; set; }
        public string Error { get; set; }
        public TestRunResult Result { get; set; }
        public long InitTimeoutMs { get; set; }
        public TestFilterOptions FilterOptions { get; set; }
        public string AttachedLockToken { get; set; }
    }

    internal sealed class TestJobClearResult
    {
        public bool Cleared { get; set; }
        public string JobId { get; set; }
        public bool SafeToStartNewRun { get; set; }
        public bool PhysicalOwnerRetained { get; set; }
        public bool RestartRequiredIfOrphaned { get; set; }
    }

    /// <summary>
    /// Tracks MCP-owned asynchronous Unity TestRunner jobs. Logical job status and the physical
    /// callback owner are deliberately separate: a timeout can fail the logical request without
    /// pretending that Unity's TestRunner has stopped.
    /// </summary>
    [InitializeOnLoad]
    internal static class TestJobManager
    {
        private const int FailureCap = 25;
        private const long StuckThresholdMs = 60_000;
        private const long DefaultInitializationTimeoutMs = 15_000;
        private const long MaxInitializationTimeoutMs = 600_000;
        private const int MaxJobsToKeep = 10;
        private const long MinPersistIntervalMs = 1000;

        private const string SessionKeyJobsV1 = "MCPForUnity.TestJobsV1";
        private const string SessionKeyCurrentJobIdV1 = "MCPForUnity.CurrentTestJobIdV1";
        private const string SessionKeyJobsV2 = "MCPForUnity.TestJobsV2";

        private static readonly object LockObj = new();
        private static readonly Dictionary<string, TestJob> Jobs = new();
        private static readonly HashSet<string> ScheduledDispatches = new(StringComparer.Ordinal);

        private static string _currentJobId;
        private static TestJobIdentity? _physicalOwner;
        private static long _nextGeneration = 1;
        private static long _lastPersistUnixMs;
        private static bool _initialized;
        private static bool _initializing;

        internal static Func<long> UnixTimeMillisecondsForTests { get; set; }
        internal static Action<Action> DelayCallSchedulerForTests { get; set; }
        internal static Func<string, bool> PersistSnapshotForTests { get; set; }

        static TestJobManager()
        {
            EnsureInitialized();
        }

        public static string CurrentJobId
        {
            get
            {
                EnsureInitialized();
                lock (LockObj) return _currentJobId;
            }
        }

        public static bool HasRunningJob
        {
            get
            {
                EnsureInitialized();
                lock (LockObj) return !string.IsNullOrEmpty(_currentJobId) || _physicalOwner.HasValue;
            }
        }

        internal static TestJobIdentity? PhysicalOwnerForTests
        {
            get
            {
                EnsureInitialized();
                lock (LockObj) return _physicalOwner;
            }
        }

        internal static int JobCountForTests
        {
            get
            {
                EnsureInitialized();
                lock (LockObj) return Jobs.Count;
            }
        }

        internal static bool IsPhysicalOwner(TestJobIdentity owner)
        {
            EnsureInitialized();
            lock (LockObj)
            {
                return _physicalOwner.HasValue && _physicalOwner.Value == owner;
            }
        }

        internal static void EnsureInitialized()
        {
            TestJobIdentity? restoredOwner = null;
            TestJob restoredJob = null;
            List<TestJobIdentity> queued = null;

            lock (LockObj)
            {
                if (_initialized || _initializing) return;
                _initializing = true;
                try
                {
                    TryRestoreFromSessionStateLocked();
                    _initialized = true;
                    restoredOwner = _physicalOwner;
                    if (restoredOwner.HasValue)
                    {
                        Jobs.TryGetValue(restoredOwner.Value.JobId, out restoredJob);
                    }
                    queued = Jobs.Values
                        .Where(job => job.Phase == TestJobPhase.Queued)
                        .Select(IdentityOf)
                        .ToList();
                }
                finally
                {
                    _initializing = false;
                }
            }

            SharedEditorOperationLock.EnsureInitialized();
            if (restoredOwner.HasValue && restoredJob != null)
            {
                ReconcilePhysicalOwnerLock(restoredOwner.Value, restoredJob);
                TestRunStatus.RehydrateRunning(ParseMode(restoredJob.Mode), restoredJob.StartedUnixMs);
                if (MCPServiceLocator.Tests is IJobBoundTestRunnerService bound)
                {
                    bound.BindPhysicalOwner(restoredOwner.Value);
                }
                LogPhase(restoredJob, "rehydrated");
            }

            if (queued != null)
            {
                foreach (TestJobIdentity identity in queued)
                {
                    ScheduleDispatch(identity);
                }
            }
        }

        public static string StartJob(
            TestMode mode,
            TestFilterOptions filterOptions = null,
            long initTimeoutMs = 0,
            string attachedLockToken = null)
        {
            EnsureInitialized();
            if (initTimeoutMs < 0) initTimeoutMs = 0;
            if (initTimeoutMs > MaxInitializationTimeoutMs) initTimeoutMs = MaxInitializationTimeoutMs;

            long now = Now();
            TestJob job;
            TestJobIdentity identity;
            lock (LockObj)
            {
                if (!string.IsNullOrEmpty(_currentJobId) || _physicalOwner.HasValue)
                {
                    throw new InvalidOperationException("A Unity test run is already in progress.");
                }

                identity = new TestJobIdentity(Guid.NewGuid().ToString("N"), _nextGeneration++);
                job = new TestJob
                {
                    JobId = identity.JobId,
                    Generation = identity.Generation,
                    Status = TestJobStatus.Running,
                    Phase = TestJobPhase.Queued,
                    Mode = mode.ToString(),
                    StartedUnixMs = now,
                    LastUpdateUnixMs = now,
                    FailuresSoFar = new List<TestJobFailure>(),
                    InitTimeoutMs = initTimeoutMs,
                    FilterOptions = CloneFilter(filterOptions),
                    AttachedLockToken = attachedLockToken,
                };
                Jobs[job.JobId] = job;
                _currentJobId = job.JobId;
            }

            bool attached = string.IsNullOrEmpty(attachedLockToken)
                            || SharedEditorOperationLock.AttachToJob(attachedLockToken, identity);
            if (!attached)
            {
                lock (LockObj)
                {
                    Jobs.Remove(job.JobId);
                    if (_currentJobId == job.JobId) _currentJobId = null;
                }
                throw new InvalidOperationException("The effective editor lock could not be attached to the test job.");
            }

            if (!PersistToSessionState(force: true, critical: true))
            {
                lock (LockObj)
                {
                    Jobs.Remove(job.JobId);
                    if (_currentJobId == job.JobId) _currentJobId = null;
                }
                if (!string.IsNullOrEmpty(attachedLockToken))
                {
                    SharedEditorOperationLock.AbortAttachedJob(identity);
                }
                throw new InvalidOperationException("Failed to durably queue the test job.");
            }

            lock (LockObj) PruneJobsLocked();

            LogPhase(job, "queued");
            ScheduleDispatch(identity);
            return job.JobId;
        }

        private static void ScheduleDispatch(TestJobIdentity identity)
        {
            string key = identity.ToString();
            lock (LockObj)
            {
                if (!ScheduledDispatches.Add(key)) return;
            }

            ScheduleMainThread(() =>
            {
                lock (LockObj) ScheduledDispatches.Remove(key);
                TryDispatchQueuedJob(identity);
            });
        }

        internal static bool TryDispatchQueuedJobForTests(string jobId)
        {
            EnsureInitialized();
            TestJobIdentity identity;
            lock (LockObj)
            {
                if (!Jobs.TryGetValue(jobId, out TestJob job)) return false;
                identity = IdentityOf(job);
                ScheduledDispatches.Remove(identity.ToString());
            }
            return TryDispatchQueuedJob(identity);
        }

        private static bool TryDispatchQueuedJob(TestJobIdentity identity)
        {
            TestJob job;
            lock (LockObj)
            {
                if (!Jobs.TryGetValue(identity.JobId, out job)
                    || IdentityOf(job) != identity
                    || job.Phase != TestJobPhase.Queued
                    || _physicalOwner.HasValue)
                {
                    return false;
                }

                job.Phase = TestJobPhase.Dispatching;
                long priorUpdate = job.LastUpdateUnixMs;
                job.LastUpdateUnixMs = Now();
                _physicalOwner = identity;
                if (!PersistToSessionState(force: true, critical: true))
                {
                    job.Phase = TestJobPhase.Queued;
                    job.LastUpdateUnixMs = priorUpdate;
                    _physicalOwner = null;
                    return false;
                }
            }

            TestRunStatus.MarkStarted(ParseMode(job.Mode), job.StartedUnixMs);
            LogPhase(job, "owner_persisted");

            try
            {
                ITestRunnerService configuredRunner = MCPServiceLocator.Tests;
                IJobBoundTestRunnerService boundRunner = configuredRunner as IJobBoundTestRunnerService;
                Task<TestRunResult> task;
                if (boundRunner != null)
                {
                    boundRunner.BindPhysicalOwner(identity);
                    task = boundRunner.RunTestsForJobAsync(
                        identity,
                        ParseMode(job.Mode),
                        CloneFilter(job.FilterOptions));
                }
                else
                {
                    // Public service registrations remain supported. They cannot participate in
                    // owner-tagged Unity callbacks, but their returned task still owns the durable
                    // physical fence and provides matching terminal evidence.
                    task = configuredRunner.RunTestsAsync(
                        ParseMode(job.Mode),
                        CloneFilter(job.FilterOptions));
                }
                if (task == null)
                {
                    FinalizePhysicalOwner(identity, null, "Test runner returned no task.");
                    return false;
                }

                if (boundRunner == null)
                {
                    OnExecuteReturned(identity);
                    OnRunStarted(identity, null);
                }

                task.ContinueWith(completed =>
                {
                    ScheduleMainThread(() => FinalizeFromTask(identity, completed));
                }, TaskScheduler.Default);
                return true;
            }
            catch (Exception ex)
            {
                FinalizePhysicalOwner(identity, null, ex.GetBaseException().Message);
                return false;
            }
        }

        internal static void OnExecuteReturned(TestJobIdentity owner)
        {
            TestJob job = null;
            lock (LockObj)
            {
                if (!TryGetPhysicalOwnerJobLocked(owner, out job)
                    || job.Phase != TestJobPhase.Dispatching)
                {
                    return;
                }

                TestJobPhase priorPhase = job.Phase;
                long? priorAnchor = job.AwaitingRunStartedSinceUnixMs;
                long priorUpdate = job.LastUpdateUnixMs;
                long now = Now();
                job.Phase = TestJobPhase.AwaitingRunStarted;
                job.AwaitingRunStartedSinceUnixMs = now;
                job.LastUpdateUnixMs = now;
                if (!PersistToSessionState(force: true, critical: true))
                {
                    job.Phase = priorPhase;
                    job.AwaitingRunStartedSinceUnixMs = priorAnchor;
                    job.LastUpdateUnixMs = priorUpdate;
                    return;
                }
            }
            LogPhase(job, "execute_returned");
        }

        internal static bool OnRunStarted(TestJobIdentity owner, int? totalTests)
        {
            TestJob job = null;
            lock (LockObj)
            {
                if (!TryGetPhysicalOwnerJobLocked(owner, out job)
                    || (job.Phase != TestJobPhase.Dispatching
                        && job.Phase != TestJobPhase.AwaitingRunStarted))
                {
                    return false;
                }

                TestJob prior = CloneJob(job);
                long now = Now();
                job.Phase = TestJobPhase.Running;
                job.AwaitingRunStartedSinceUnixMs = null;
                job.LastUpdateUnixMs = now;
                job.TotalTests = totalTests;
                job.CompletedTests = 0;
                job.CurrentTestFullName = null;
                job.CurrentTestStartedUnixMs = null;
                job.LastFinishedTestFullName = null;
                job.LastFinishedUnixMs = null;
                job.FailuresSoFar ??= new List<TestJobFailure>();
                job.FailuresSoFar.Clear();
                if (!PersistToSessionState(force: true, critical: true))
                {
                    Jobs[job.JobId] = prior;
                    job = prior;
                    return false;
                }
            }
            LogPhase(job, totalTests.HasValue ? "run_started" : "run_started_total_unknown");
            return true;
        }

        internal static void OnTestStarted(TestJobIdentity owner, string testFullName)
        {
            if (string.IsNullOrWhiteSpace(testFullName)) return;
            lock (LockObj)
            {
                if (!TryGetPhysicalOwnerJobLocked(owner, out TestJob job)
                    || job.Phase != TestJobPhase.Running) return;
                long now = Now();
                job.LastUpdateUnixMs = now;
                job.CurrentTestFullName = testFullName;
                job.CurrentTestStartedUnixMs = now;
            }
            PersistToSessionState();
        }

        internal static bool OnLeafTestFinished(
            TestJobIdentity owner,
            string testFullName,
            bool isFailure,
            string message)
        {
            lock (LockObj)
            {
                if (!TryGetPhysicalOwnerJobLocked(owner, out TestJob job)
                    || job.Phase != TestJobPhase.Running) return false;
                long now = Now();
                job.LastUpdateUnixMs = now;
                job.CompletedTests = Math.Max(0, job.CompletedTests + 1);
                job.LastFinishedTestFullName = testFullName;
                job.LastFinishedUnixMs = now;
                if (isFailure)
                {
                    job.FailuresSoFar ??= new List<TestJobFailure>();
                    if (job.FailuresSoFar.Count < FailureCap)
                    {
                        job.FailuresSoFar.Add(new TestJobFailure
                        {
                            FullName = testFullName,
                            Message = string.IsNullOrWhiteSpace(message) ? "Test failed" : message,
                        });
                    }
                }
            }
            PersistToSessionState();
            return true;
        }

        internal static bool FinalizePhysicalOwnerFromRunFinished(
            TestJobIdentity owner,
            TestRunResult resultPayload)
        {
            return FinalizePhysicalOwner(owner, resultPayload, null);
        }

        private static bool FinalizePhysicalOwner(
            TestJobIdentity owner,
            TestRunResult resultPayload,
            string terminalError)
        {
            TestJob job;
            TestJob prior;
            string priorCurrent;
            TestJobIdentity? priorOwner;
            lock (LockObj)
            {
                if (!TryGetPhysicalOwnerJobLocked(owner, out job)
                    || job.Phase == TestJobPhase.Terminal)
                {
                    return false;
                }

                prior = CloneJob(job);
                priorCurrent = _currentJobId;
                priorOwner = _physicalOwner;
                long now = Now();
                bool preserveLogicalFailure = job.Status == TestJobStatus.Failed
                                              && !string.IsNullOrEmpty(job.Error);

                job.Phase = TestJobPhase.Terminal;
                job.AwaitingRunStartedSinceUnixMs = null;
                job.LastUpdateUnixMs = now;
                job.FinishedUnixMs ??= now;
                job.PhysicalFinishedUnixMs = now;
                job.CurrentTestFullName = null;
                if (!preserveLogicalFailure)
                {
                    if (!string.IsNullOrEmpty(terminalError))
                    {
                        job.Status = TestJobStatus.Failed;
                        job.Error = terminalError;
                        job.Result = null;
                    }
                    else
                    {
                        job.Status = resultPayload != null && resultPayload.Failed > 0
                            ? TestJobStatus.Failed
                            : TestJobStatus.Succeeded;
                        job.Error = null;
                        job.Result = resultPayload;
                    }
                }
                _physicalOwner = null;
                if (_currentJobId == owner.JobId) _currentJobId = null;

                if (!PersistToSessionState(force: true, critical: true))
                {
                    Jobs[owner.JobId] = prior;
                    _currentJobId = priorCurrent;
                    _physicalOwner = priorOwner;
                    return false;
                }
                PruneJobsLocked();
            }

            if (MCPServiceLocator.Tests is IJobBoundTestRunnerService runner)
            {
                runner.ClearPhysicalOwner(owner);
            }
            var lockState = SharedEditorOperationLock.GetState();
            bool matchingAttachment = lockState.IsAttachedToJob
                                      && string.Equals(lockState.AttachedJobId, owner.JobId, StringComparison.Ordinal)
                                      && lockState.AttachedJobGeneration == owner.Generation;
            if (matchingAttachment && !SharedEditorOperationLock.CompleteAttachedJob(owner))
            {
                McpLog.Warn($"[TestJobManager] Physical terminal persisted for {owner}, but attached lock completion could not be confirmed.");
            }
            TestRunStatus.MarkFinished();
            LogPhase(job, "physical_terminal");
            return true;
        }

        private static void FinalizeFromTask(TestJobIdentity owner, Task<TestRunResult> task)
        {
            if (task == null) return;
            if (task.IsFaulted)
            {
                FinalizePhysicalOwner(owner, null,
                    task.Exception?.GetBaseException().Message ?? "Unknown test job failure");
            }
            else if (task.IsCanceled)
            {
                FinalizePhysicalOwner(owner, null, "Test job canceled");
            }
            else
            {
                FinalizePhysicalOwner(owner, task.Result, null);
            }
        }

        public static TestJobClearResult ClearStuckJob()
        {
            EnsureInitialized();
            TestJobIdentity identity = default;
            TestJob job = null;
            bool ownerlessTerminal = false;
            lock (LockObj)
            {
                if (string.IsNullOrEmpty(_currentJobId)
                    || !Jobs.TryGetValue(_currentJobId, out job))
                {
                    return new TestJobClearResult
                    {
                        Cleared = false,
                        SafeToStartNewRun = !_physicalOwner.HasValue,
                    };
                }

                identity = IdentityOf(job);
                TestJob prior = CloneJob(job);
                long now = Now();
                job.Status = TestJobStatus.Failed;
                job.Error = "Job cleared logically (stuck or orphaned)";
                job.FinishedUnixMs ??= now;
                job.LastUpdateUnixMs = now;

                ownerlessTerminal = job.Phase == TestJobPhase.Queued
                                    && (!_physicalOwner.HasValue || _physicalOwner.Value != identity);
                if (ownerlessTerminal)
                {
                    job.Phase = TestJobPhase.Terminal;
                    _currentJobId = null;
                }

                if (!PersistToSessionState(force: true, critical: true))
                {
                    Jobs[job.JobId] = prior;
                    if (ownerlessTerminal) _currentJobId = prior.JobId;
                    return new TestJobClearResult
                    {
                        Cleared = false,
                        JobId = job.JobId,
                        SafeToStartNewRun = false,
                        PhysicalOwnerRetained = _physicalOwner.HasValue,
                        RestartRequiredIfOrphaned = _physicalOwner.HasValue,
                    };
                }
                if (ownerlessTerminal) PruneJobsLocked();
            }

            if (ownerlessTerminal)
            {
                var lockState = SharedEditorOperationLock.GetState();
                if (lockState.IsAttachedToJob
                    && string.Equals(lockState.AttachedJobId, identity.JobId, StringComparison.Ordinal)
                    && lockState.AttachedJobGeneration == identity.Generation)
                {
                    SharedEditorOperationLock.AbortAttachedJob(identity);
                }
            }

            bool physicalRetained = !ownerlessTerminal && _physicalOwner.HasValue;
            McpLog.Warn($"[TestJobManager] Logical clear job={job.JobId} phase={job.Phase} physical_owner_retained={physicalRetained}");
            return new TestJobClearResult
            {
                Cleared = true,
                JobId = job.JobId,
                SafeToStartNewRun = ownerlessTerminal && !_physicalOwner.HasValue,
                PhysicalOwnerRetained = physicalRetained,
                RestartRequiredIfOrphaned = physicalRetained,
            };
        }

        internal static TestJob GetJob(string jobId)
        {
            EnsureInitialized();
            if (string.IsNullOrWhiteSpace(jobId)) return null;

            bool shouldPersist = false;
            TestJob result;
            lock (LockObj)
            {
                if (!Jobs.TryGetValue(jobId, out result)) return null;

                // The budget starts only after Execute returned for the matching physical owner.
                if (result.Status == TestJobStatus.Running
                    && result.Phase == TestJobPhase.AwaitingRunStarted
                    && result.AwaitingRunStartedSinceUnixMs.HasValue)
                {
                    long now = Now();
                    long timeout = result.InitTimeoutMs > 0
                        ? result.InitTimeoutMs
                        : DefaultInitializationTimeoutMs;
                    if (!EditorStateCache.GetActualIsCompiling()
                        && !EditorApplication.isUpdating
                        && now - result.AwaitingRunStartedSinceUnixMs.Value > timeout)
                    {
                        result.Status = TestJobStatus.Failed;
                        result.Error = "Test job failed to initialize (RunStarted was not observed within timeout; physical fence retained)";
                        result.FinishedUnixMs = now;
                        result.LastUpdateUnixMs = now;
                        shouldPersist = true;
                        McpLog.Warn($"[TestJobManager] Job {jobId} logical init timeout after {timeout}ms; physical owner retained={_physicalOwner.HasValue}");
                    }
                }
            }

            if (shouldPersist) PersistToSessionState(force: true);
            return result;
        }

        internal static object ToSerializable(TestJob job, bool includeDetails, bool includeFailedTests)
        {
            if (job == null) return null;

            object resultPayload = null;
            if (job.Status == TestJobStatus.Succeeded && job.Result != null)
            {
                resultPayload = job.Result.ToSerializable(job.Mode, includeDetails, includeFailedTests);
            }

            bool physicalRetained;
            bool safeToStart;
            lock (LockObj)
            {
                physicalRetained = _physicalOwner.HasValue
                                   && _physicalOwner.Value == IdentityOf(job);
                safeToStart = !_physicalOwner.HasValue && string.IsNullOrEmpty(_currentJobId);
            }

            return new
            {
                job_id = job.JobId,
                generation = job.Generation,
                status = job.Status.ToString().ToLowerInvariant(),
                phase = PhaseName(job.Phase),
                mode = job.Mode,
                started_unix_ms = job.StartedUnixMs,
                awaiting_run_started_since_unix_ms = job.AwaitingRunStartedSinceUnixMs,
                finished_unix_ms = job.FinishedUnixMs,
                physical_finished_unix_ms = job.PhysicalFinishedUnixMs,
                last_update_unix_ms = job.LastUpdateUnixMs,
                safe_to_start_new_run = safeToStart,
                physical_owner_retained = physicalRetained,
                restart_required_if_orphaned = physicalRetained && job.Status == TestJobStatus.Failed,
                progress = new
                {
                    completed = job.CompletedTests,
                    total = job.TotalTests,
                    current_test_full_name = job.CurrentTestFullName,
                    current_test_started_unix_ms = job.CurrentTestStartedUnixMs,
                    last_finished_test_full_name = job.LastFinishedTestFullName,
                    last_finished_unix_ms = job.LastFinishedUnixMs,
                    stuck_suspected = IsStuck(job),
                    editor_is_focused = InternalEditorUtility.isApplicationActive,
                    blocked_reason = GetBlockedReason(job),
                    failures_so_far = BuildFailuresPayload(job.FailuresSoFar),
                    failures_capped = job.FailuresSoFar != null && job.FailuresSoFar.Count >= FailureCap,
                },
                error = job.Error,
                result = resultPayload,
            };
        }

        private static string GetBlockedReason(TestJob job)
        {
            if (job == null || job.Status != TestJobStatus.Running || !IsStuck(job)) return null;
            if (!InternalEditorUtility.isApplicationActive) return "editor_unfocused";
            if (EditorStateCache.GetActualIsCompiling()) return "compiling";
            if (EditorApplication.isUpdating) return "asset_import";
            return "unknown";
        }

        private static bool IsStuck(TestJob job)
        {
            if (job == null || job.Status != TestJobStatus.Running) return false;
            if (string.IsNullOrWhiteSpace(job.CurrentTestFullName)
                || !job.CurrentTestStartedUnixMs.HasValue) return false;
            return Now() - job.CurrentTestStartedUnixMs.Value > StuckThresholdMs;
        }

        private static object[] BuildFailuresPayload(List<TestJobFailure> failures)
        {
            if (failures == null || failures.Count == 0) return Array.Empty<object>();
            return failures.Select(item => (object)new
            {
                full_name = item?.FullName,
                message = item?.Message,
            }).ToArray();
        }

        private sealed class PersistedStateV2
        {
            public int schema_version { get; set; } = 2;
            public string current_job_id { get; set; }
            public PersistedOwner physical_owner { get; set; }
            public long next_generation { get; set; }
            public List<PersistedJobV2> jobs { get; set; }
        }

        private sealed class PersistedOwner
        {
            public string job_id { get; set; }
            public long generation { get; set; }
        }

        private sealed class PersistedJobV2
        {
            public string job_id { get; set; }
            public long generation { get; set; }
            public string status { get; set; }
            public string phase { get; set; }
            public string mode { get; set; }
            public long started_unix_ms { get; set; }
            public long? awaiting_run_started_since_unix_ms { get; set; }
            public long? finished_unix_ms { get; set; }
            public long? physical_finished_unix_ms { get; set; }
            public long last_update_unix_ms { get; set; }
            public int? total_tests { get; set; }
            public int completed_tests { get; set; }
            public string current_test_full_name { get; set; }
            public long? current_test_started_unix_ms { get; set; }
            public string last_finished_test_full_name { get; set; }
            public long? last_finished_unix_ms { get; set; }
            public List<TestJobFailure> failures_so_far { get; set; }
            public string error { get; set; }
            public long init_timeout_ms { get; set; }
            public string[] test_names { get; set; }
            public string[] group_names { get; set; }
            public string[] category_names { get; set; }
            public string[] assembly_names { get; set; }
            public string attached_lock_token { get; set; }
        }

        private sealed class PersistedStateV1
        {
            public string current_job_id { get; set; }
            public List<PersistedJobV1> jobs { get; set; }
        }

        private sealed class PersistedJobV1
        {
            public string job_id { get; set; }
            public string status { get; set; }
            public string mode { get; set; }
            public long started_unix_ms { get; set; }
            public long? finished_unix_ms { get; set; }
            public long last_update_unix_ms { get; set; }
            public int? total_tests { get; set; }
            public int completed_tests { get; set; }
            public string current_test_full_name { get; set; }
            public long? current_test_started_unix_ms { get; set; }
            public string last_finished_test_full_name { get; set; }
            public long? last_finished_unix_ms { get; set; }
            public List<TestJobFailure> failures_so_far { get; set; }
            public string error { get; set; }
            public long init_timeout_ms { get; set; }
        }

        private static void TryRestoreFromSessionStateLocked()
        {
            Jobs.Clear();
            ScheduledDispatches.Clear();
            _currentJobId = null;
            _physicalOwner = null;
            _nextGeneration = 1;

            try
            {
                string jsonV2 = SessionState.GetString(SessionKeyJobsV2, string.Empty);
                if (!string.IsNullOrWhiteSpace(jsonV2))
                {
                    RestoreV2Locked(JsonConvert.DeserializeObject<PersistedStateV2>(jsonV2));
                    return;
                }

                string jsonV1 = SessionState.GetString(SessionKeyJobsV1, string.Empty);
                if (string.IsNullOrWhiteSpace(jsonV1))
                {
                    string legacyCurrent = SessionState.GetString(SessionKeyCurrentJobIdV1, string.Empty);
                    _currentJobId = string.IsNullOrWhiteSpace(legacyCurrent) ? null : legacyCurrent;
                    return;
                }

                RestoreV1Locked(JsonConvert.DeserializeObject<PersistedStateV1>(jsonV1));
                PersistToSessionState(force: true);
            }
            catch (Exception ex)
            {
                Jobs.Clear();
                _currentJobId = null;
                _physicalOwner = null;
                McpLog.Warn($"[TestJobManager] Failed to restore SessionState: {ex.Message}");
            }
        }

        private static void RestoreV2Locked(PersistedStateV2 state)
        {
            if (state?.jobs == null) return;
            foreach (PersistedJobV2 persisted in state.jobs)
            {
                if (persisted == null || string.IsNullOrWhiteSpace(persisted.job_id)) continue;
                var job = new TestJob
                {
                    JobId = persisted.job_id,
                    Generation = persisted.generation > 0 ? persisted.generation : _nextGeneration++,
                    Status = ParseStatus(persisted.status),
                    Phase = ParsePhase(persisted.phase),
                    Mode = persisted.mode,
                    StartedUnixMs = persisted.started_unix_ms,
                    AwaitingRunStartedSinceUnixMs = persisted.awaiting_run_started_since_unix_ms,
                    FinishedUnixMs = persisted.finished_unix_ms,
                    PhysicalFinishedUnixMs = persisted.physical_finished_unix_ms,
                    LastUpdateUnixMs = persisted.last_update_unix_ms,
                    TotalTests = persisted.total_tests,
                    CompletedTests = persisted.completed_tests,
                    CurrentTestFullName = persisted.current_test_full_name,
                    CurrentTestStartedUnixMs = persisted.current_test_started_unix_ms,
                    LastFinishedTestFullName = persisted.last_finished_test_full_name,
                    LastFinishedUnixMs = persisted.last_finished_unix_ms,
                    FailuresSoFar = persisted.failures_so_far ?? new List<TestJobFailure>(),
                    Error = persisted.error,
                    InitTimeoutMs = persisted.init_timeout_ms,
                    FilterOptions = new TestFilterOptions
                    {
                        TestNames = persisted.test_names,
                        GroupNames = persisted.group_names,
                        CategoryNames = persisted.category_names,
                        AssemblyNames = persisted.assembly_names,
                    },
                    AttachedLockToken = persisted.attached_lock_token,
                };
                Jobs[job.JobId] = job;
                _nextGeneration = Math.Max(_nextGeneration, job.Generation + 1);
            }

            _nextGeneration = Math.Max(_nextGeneration, state.next_generation > 0 ? state.next_generation : 1);
            _currentJobId = string.IsNullOrWhiteSpace(state.current_job_id) ? null : state.current_job_id;
            if (!string.IsNullOrEmpty(_currentJobId) && !Jobs.ContainsKey(_currentJobId)) _currentJobId = null;

            if (state.physical_owner != null
                && Jobs.TryGetValue(state.physical_owner.job_id, out TestJob physicalJob)
                && physicalJob.Generation == state.physical_owner.generation
                && physicalJob.Phase != TestJobPhase.Terminal)
            {
                _physicalOwner = new TestJobIdentity(physicalJob.JobId, physicalJob.Generation);
                _currentJobId ??= physicalJob.JobId;
            }
        }

        private static void RestoreV1Locked(PersistedStateV1 state)
        {
            if (state?.jobs == null) return;
            foreach (PersistedJobV1 persisted in state.jobs)
            {
                if (persisted == null || string.IsNullOrWhiteSpace(persisted.job_id)) continue;
                long generation = _nextGeneration++;
                var status = ParseStatus(persisted.status);
                var job = new TestJob
                {
                    JobId = persisted.job_id,
                    Generation = generation,
                    Status = status,
                    Phase = status == TestJobStatus.Running ? TestJobPhase.Running : TestJobPhase.Terminal,
                    Mode = persisted.mode,
                    StartedUnixMs = persisted.started_unix_ms,
                    AwaitingRunStartedSinceUnixMs = null,
                    FinishedUnixMs = persisted.finished_unix_ms,
                    PhysicalFinishedUnixMs = status == TestJobStatus.Running
                        ? null
                        : persisted.finished_unix_ms,
                    LastUpdateUnixMs = persisted.last_update_unix_ms,
                    TotalTests = persisted.total_tests,
                    CompletedTests = persisted.completed_tests,
                    CurrentTestFullName = persisted.current_test_full_name,
                    CurrentTestStartedUnixMs = persisted.current_test_started_unix_ms,
                    LastFinishedTestFullName = persisted.last_finished_test_full_name,
                    LastFinishedUnixMs = persisted.last_finished_unix_ms,
                    FailuresSoFar = persisted.failures_so_far ?? new List<TestJobFailure>(),
                    Error = persisted.error,
                    InitTimeoutMs = persisted.init_timeout_ms,
                };
                Jobs[job.JobId] = job;
            }

            _currentJobId = string.IsNullOrWhiteSpace(state.current_job_id) ? null : state.current_job_id;
            if (!string.IsNullOrEmpty(_currentJobId)
                && Jobs.TryGetValue(_currentJobId, out TestJob current)
                && current.Status == TestJobStatus.Running)
            {
                _physicalOwner = IdentityOf(current);
                current.Phase = TestJobPhase.Running;
                current.AwaitingRunStartedSinceUnixMs = null;
            }
            else
            {
                _currentJobId = null;
            }
        }

        private static bool PersistToSessionState(bool force = false, bool critical = false)
        {
            long now = Now();
            if (!force && now - _lastPersistUnixMs < MinPersistIntervalMs) return true;

            try
            {
                PersistedStateV2 snapshot;
                lock (LockObj)
                {
                    snapshot = new PersistedStateV2
                    {
                        current_job_id = _currentJobId,
                        physical_owner = _physicalOwner.HasValue
                            ? new PersistedOwner
                            {
                                job_id = _physicalOwner.Value.JobId,
                                generation = _physicalOwner.Value.Generation,
                            }
                            : null,
                        next_generation = _nextGeneration,
                        jobs = Jobs.Values
                            .OrderByDescending(job => job.LastUpdateUnixMs)
                            .Take(MaxJobsToKeep)
                            .Select(ToPersisted)
                            .ToList(),
                    };
                }

                string json = JsonConvert.SerializeObject(snapshot);
                if (PersistSnapshotForTests != null && !PersistSnapshotForTests(json))
                {
                    throw new InvalidOperationException("Injected SessionState persistence failure.");
                }
                SessionState.SetString(SessionKeyJobsV2, json);
                SessionState.SetString(SessionKeyCurrentJobIdV1, snapshot.current_job_id ?? string.Empty);
                _lastPersistUnixMs = now;
                return true;
            }
            catch (Exception ex)
            {
                McpLog.Warn($"[TestJobManager] Failed to persist SessionState: {ex.Message}");
                if (critical)
                {
                    McpLog.Error("[TestJobManager] Critical lifecycle persistence failed; retaining the prior physical fence.");
                }
                return false;
            }
        }

        private static void ReconcilePhysicalOwnerLock(TestJobIdentity owner, TestJob job)
        {
            if (!SharedEditorOperationLock.IsEnabled) return;

            var state = SharedEditorOperationLock.GetState();
            if (state.IsAttachedToJob)
            {
                if (!string.Equals(state.AttachedJobId, owner.JobId, StringComparison.Ordinal)
                    || state.AttachedJobGeneration != owner.Generation)
                {
                    McpLog.Error($"[TestJobManager] Restored owner {owner} conflicts with attached lock owner {state.AttachedJobId}@{state.AttachedJobGeneration}; retaining both fences.");
                }
                return;
            }

            string token = null;
            bool attached;
            if (state.Locked)
            {
                attached = SharedEditorOperationLock.AttachCurrentToJobForRecovery(owner, out token);
            }
            else
            {
                var acquired = SharedEditorOperationLock.TryAcquire(
                    "test-job-restore",
                    $"rehydrate:{owner.JobId}",
                    isExplicit: false);
                token = acquired.Token;
                attached = acquired.Acquired
                           && SharedEditorOperationLock.AttachToJob(token, owner);
            }

            if (!attached)
            {
                McpLog.Error($"[TestJobManager] Could not re-establish the operation-lock fence for restored owner {owner}.");
                return;
            }

            lock (LockObj)
            {
                job.AttachedLockToken = token;
            }
            if (!PersistToSessionState(force: true, critical: true))
            {
                McpLog.Error($"[TestJobManager] Restored lock for {owner}, but could not persist its token; fence remains attached.");
            }
        }

        private static PersistedJobV2 ToPersisted(TestJob job)
        {
            return new PersistedJobV2
            {
                job_id = job.JobId,
                generation = job.Generation,
                status = job.Status.ToString().ToLowerInvariant(),
                phase = PhaseName(job.Phase),
                mode = job.Mode,
                started_unix_ms = job.StartedUnixMs,
                awaiting_run_started_since_unix_ms = job.AwaitingRunStartedSinceUnixMs,
                finished_unix_ms = job.FinishedUnixMs,
                physical_finished_unix_ms = job.PhysicalFinishedUnixMs,
                last_update_unix_ms = job.LastUpdateUnixMs,
                total_tests = job.TotalTests,
                completed_tests = job.CompletedTests,
                current_test_full_name = job.CurrentTestFullName,
                current_test_started_unix_ms = job.CurrentTestStartedUnixMs,
                last_finished_test_full_name = job.LastFinishedTestFullName,
                last_finished_unix_ms = job.LastFinishedUnixMs,
                failures_so_far = (job.FailuresSoFar ?? new List<TestJobFailure>()).Take(FailureCap).ToList(),
                error = job.Error,
                init_timeout_ms = job.InitTimeoutMs,
                test_names = job.FilterOptions?.TestNames,
                group_names = job.FilterOptions?.GroupNames,
                category_names = job.FilterOptions?.CategoryNames,
                assembly_names = job.FilterOptions?.AssemblyNames,
                attached_lock_token = job.AttachedLockToken,
            };
        }

        private static bool TryGetPhysicalOwnerJobLocked(TestJobIdentity owner, out TestJob job)
        {
            job = null;
            return _physicalOwner.HasValue
                   && _physicalOwner.Value == owner
                   && Jobs.TryGetValue(owner.JobId, out job)
                   && job.Generation == owner.Generation;
        }

        private static TestJobIdentity IdentityOf(TestJob job)
            => new(job.JobId, job.Generation);

        private static TestJob CloneJob(TestJob source)
        {
            return new TestJob
            {
                JobId = source.JobId,
                Generation = source.Generation,
                Status = source.Status,
                Phase = source.Phase,
                Mode = source.Mode,
                StartedUnixMs = source.StartedUnixMs,
                AwaitingRunStartedSinceUnixMs = source.AwaitingRunStartedSinceUnixMs,
                FinishedUnixMs = source.FinishedUnixMs,
                PhysicalFinishedUnixMs = source.PhysicalFinishedUnixMs,
                LastUpdateUnixMs = source.LastUpdateUnixMs,
                TotalTests = source.TotalTests,
                CompletedTests = source.CompletedTests,
                CurrentTestFullName = source.CurrentTestFullName,
                CurrentTestStartedUnixMs = source.CurrentTestStartedUnixMs,
                LastFinishedTestFullName = source.LastFinishedTestFullName,
                LastFinishedUnixMs = source.LastFinishedUnixMs,
                FailuresSoFar = (source.FailuresSoFar ?? new List<TestJobFailure>())
                    .Select(failure => new TestJobFailure
                    {
                        FullName = failure.FullName,
                        Message = failure.Message,
                    }).ToList(),
                Error = source.Error,
                Result = source.Result,
                InitTimeoutMs = source.InitTimeoutMs,
                FilterOptions = CloneFilter(source.FilterOptions),
                AttachedLockToken = source.AttachedLockToken,
            };
        }

        private static TestFilterOptions CloneFilter(TestFilterOptions source)
        {
            if (source == null) return null;
            return new TestFilterOptions
            {
                TestNames = source.TestNames?.ToArray(),
                GroupNames = source.GroupNames?.ToArray(),
                CategoryNames = source.CategoryNames?.ToArray(),
                AssemblyNames = source.AssemblyNames?.ToArray(),
            };
        }

        private static TestJobStatus ParseStatus(string status)
        {
            return status?.Trim().ToLowerInvariant() switch
            {
                "succeeded" => TestJobStatus.Succeeded,
                "failed" => TestJobStatus.Failed,
                _ => TestJobStatus.Running,
            };
        }

        private static TestJobPhase ParsePhase(string phase)
        {
            return phase?.Trim().ToLowerInvariant() switch
            {
                "queued" => TestJobPhase.Queued,
                "dispatching" => TestJobPhase.Dispatching,
                "awaiting_run_started" => TestJobPhase.AwaitingRunStarted,
                "running" => TestJobPhase.Running,
                "terminal" => TestJobPhase.Terminal,
                _ => TestJobPhase.Running,
            };
        }

        private static string PhaseName(TestJobPhase phase)
        {
            return StringCaseUtility.ToSnakeCase(phase.ToString());
        }

        private static void PruneJobsLocked()
        {
            int removeCount = Jobs.Count - MaxJobsToKeep;
            if (removeCount <= 0) return;

            string physicalJobId = _physicalOwner?.JobId;
            string[] removable = Jobs.Values
                .Where(job => job.Phase == TestJobPhase.Terminal
                              && !string.Equals(job.JobId, _currentJobId, StringComparison.Ordinal)
                              && !string.Equals(job.JobId, physicalJobId, StringComparison.Ordinal))
                .OrderBy(job => job.LastUpdateUnixMs)
                .ThenBy(job => job.JobId, StringComparer.Ordinal)
                .Take(removeCount)
                .Select(job => job.JobId)
                .ToArray();
            foreach (string jobId in removable)
            {
                Jobs.Remove(jobId);
            }
        }

        private static TestMode ParseMode(string mode)
        {
            return string.Equals(mode, TestMode.PlayMode.ToString(), StringComparison.OrdinalIgnoreCase)
                ? TestMode.PlayMode
                : TestMode.EditMode;
        }

        private static long Now()
            => UnixTimeMillisecondsForTests?.Invoke()
               ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private static void ScheduleMainThread(Action action)
        {
            if (DelayCallSchedulerForTests != null)
            {
                DelayCallSchedulerForTests(action);
            }
            else
            {
                EditorApplication.delayCall += () =>
                {
                    try { action(); }
                    catch (Exception ex)
                    {
                        McpLog.Error($"[TestJobManager] Delayed lifecycle action failed: {ex.Message}\n{ex.StackTrace}");
                    }
                };
            }
        }

        private static void LogPhase(TestJob job, string transition)
        {
            if (job == null) return;
            long elapsed = Math.Max(0, Now() - job.StartedUnixMs);
            McpLog.Info($"[TestJobLifecycle] job={job.JobId} generation={job.Generation} phase={PhaseName(job.Phase)} transition={transition} elapsed_ms={elapsed}");
        }

        internal static void ClearInMemoryForTests()
        {
            lock (LockObj)
            {
                Jobs.Clear();
                ScheduledDispatches.Clear();
                _currentJobId = null;
                _physicalOwner = null;
                _nextGeneration = 1;
                _lastPersistUnixMs = 0;
                _initialized = false;
                _initializing = false;
            }
        }

        internal static void ResetForTests(bool clearSessionState)
        {
            ClearInMemoryForTests();
            UnixTimeMillisecondsForTests = null;
            DelayCallSchedulerForTests = null;
            PersistSnapshotForTests = null;
            if (clearSessionState)
            {
                SessionState.SetString(SessionKeyJobsV1, string.Empty);
                SessionState.SetString(SessionKeyJobsV2, string.Empty);
                SessionState.SetString(SessionKeyCurrentJobIdV1, string.Empty);
            }
            lock (LockObj) _initialized = true;
        }

        internal static void SeedLegacyV1StateForTests(
            string jobId,
            TestMode mode,
            long startedUnixMs)
        {
            var state = new PersistedStateV1
            {
                current_job_id = jobId,
                jobs = new List<PersistedJobV1>
                {
                    new()
                    {
                        job_id = jobId,
                        status = "running",
                        mode = mode.ToString(),
                        started_unix_ms = startedUnixMs,
                        last_update_unix_ms = startedUnixMs,
                        failures_so_far = new List<TestJobFailure>(),
                        init_timeout_ms = 15_000,
                    },
                },
            };
            SessionState.SetString(SessionKeyJobsV2, string.Empty);
            SessionState.SetString(SessionKeyJobsV1, JsonConvert.SerializeObject(state));
            SessionState.SetString(SessionKeyCurrentJobIdV1, jobId);
        }
    }
}
