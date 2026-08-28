using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Services.PlayMode
{
    [InitializeOnLoad]
    internal static class PlayModeTestJobManager
    {
        private const int MaxSteps = 50;
        private const int MaxLogEntries = 100;
        private const int MaxRetainedJobs = 20;
        private const int MaxImmediateStepsPerTick = 8;
        private const int TickBudgetMilliseconds = 4;

        internal sealed class Job
        {
            internal string Id;
            internal string Status;
            internal JArray Steps;
            internal int StepIndex;
            internal double StartedAt;
            internal double FinishedAt;
            internal double TimeoutSeconds;
            internal double StepStartedAt;
            internal int StepStartedFrame;
            internal int StableFrames;
            internal int LastConditionFrame = -1;
            internal int NextEligibleFrame;
            internal string Error;
            internal PlayModeConditionResult LastCondition;
            internal readonly List<string> Log = new();

            internal bool IsTerminal => Status is "completed" or "failed" or "cancelled" or "interrupted" or "timeout";
        }

        private static readonly Dictionary<string, Job> Jobs = new(StringComparer.Ordinal);
        private static readonly Queue<string> JobOrder = new();
        private static Job _active;

        static PlayModeTestJobManager()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += () => InterruptActive("assembly_reload");
        }

        internal static bool HasRunningJob => _active is { Status: "running" };

        internal static string ValidateSequence(JArray steps)
        {
            if (steps == null || steps.Count == 0)
                return "steps must contain at least one step.";
            if (steps.Count > MaxSteps)
                return $"A sequence may contain at most {MaxSteps} steps.";

            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "input", "ui_click", "ui_drag", "key", "mouse", "touch", "gamepad",
                "release_all", "delay", "wait", "assert",
            };
            for (int index = 0; index < steps.Count; index++)
            {
                if (steps[index] is not JObject step)
                    return $"Step {index} must be an object.";
                string type = step["type"]?.ToString();
                if (string.IsNullOrWhiteSpace(type))
                    return $"Step {index} is missing type.";
                if (!allowed.Contains(type))
                    return $"Unknown step type '{type}' at index {index}.";
                if (type is "wait" or "assert" && step["condition"] is not JObject)
                    return $"Step {index} ({type}) requires a condition object.";
                if (type == "delay" && step["frames"] == null && step["seconds"] == null)
                    return $"Step {index} (delay) requires frames or seconds.";
            }
            return null;
        }

        internal static Job StartWait(JObject condition, double timeoutSeconds, int stableForFrames, out string error)
        {
            var steps = new JArray
            {
                new JObject
                {
                    ["type"] = "wait",
                    ["condition"] = condition?.DeepClone(),
                    ["timeout_seconds"] = timeoutSeconds,
                    ["stable_for_frames"] = stableForFrames,
                },
            };
            return Start(steps, timeoutSeconds, out error);
        }

        internal static Job Start(JArray steps, double timeoutSeconds, out string error)
        {
            error = ValidateSequence(steps);
            if (error != null) return null;
            if (!EditorApplication.isPlaying)
            {
                error = "Enter Play Mode before starting a Play Mode test job.";
                return null;
            }
            if (HasRunningJob)
            {
                error = $"Play Mode test job '{_active.Id}' is already running.";
                return null;
            }

            var job = new Job
            {
                Id = "playmode-" + Guid.NewGuid().ToString("N"),
                Status = "running",
                Steps = (JArray)steps.DeepClone(),
                StartedAt = EditorApplication.timeSinceStartup,
                TimeoutSeconds = Math.Max(0.01, Math.Min(300.0, timeoutSeconds)),
                StepStartedAt = -1,
                StepStartedFrame = -1,
            };
            AddLog(job, $"Started sequence with {job.Steps.Count} step(s).");
            Jobs[job.Id] = job;
            JobOrder.Enqueue(job.Id);
            _active = job;
            PruneJobs();
            return job;
        }

        internal static Job Get(string jobId)
            => !string.IsNullOrWhiteSpace(jobId) && Jobs.TryGetValue(jobId, out Job job) ? job : null;

        internal static bool Cancel(string jobId, out string error)
        {
            Job job = Get(jobId);
            if (job == null)
            {
                error = "Unknown job_id.";
                return false;
            }
            if (job.IsTerminal)
            {
                error = null;
                return true;
            }
            Finish(job, "cancelled", "Cancelled by caller.");
            error = null;
            return true;
        }

        internal static object ToSerializable(Job job)
        {
            return new
            {
                job_id = job.Id,
                status = job.Status,
                current_step = job.StepIndex,
                total_steps = job.Steps.Count,
                elapsed_seconds = Math.Max(0.0, (job.IsTerminal ? job.FinishedAt : EditorApplication.timeSinceStartup) - job.StartedAt),
                timeout_seconds = job.TimeoutSeconds,
                error = job.Error,
                condition = job.LastCondition == null ? null : new
                {
                    matched = job.LastCondition.Matched,
                    actual = job.LastCondition.Actual,
                    expected = job.LastCondition.Expected,
                    description = job.LastCondition.Description,
                    error = job.LastCondition.Error,
                },
                log = job.Log.ToArray(),
            };
        }

        private static void Tick()
        {
            Job job = _active;
            if (job == null || job.Status != "running") return;
            if (!EditorApplication.isPlaying)
            {
                Finish(job, "interrupted", "Play Mode exited.");
                return;
            }
            double now = EditorApplication.timeSinceStartup;
            if (now - job.StartedAt > job.TimeoutSeconds)
            {
                Finish(job, "timeout", $"Sequence exceeded {job.TimeoutSeconds:0.###} seconds.");
                return;
            }
            if (Time.frameCount < job.NextEligibleFrame) return;

            var stopwatch = Stopwatch.StartNew();
            int processed = 0;
            while (job.Status == "running"
                   && processed < MaxImmediateStepsPerTick
                   && stopwatch.ElapsedMilliseconds < TickBudgetMilliseconds)
            {
                if (job.StepIndex >= job.Steps.Count)
                {
                    Finish(job, "completed", null);
                    return;
                }
                if (job.Steps[job.StepIndex] is not JObject step)
                {
                    Finish(job, "failed", $"Step {job.StepIndex} is invalid.");
                    return;
                }
                if (job.StepStartedAt < 0)
                {
                    job.StepStartedAt = now;
                    job.StepStartedFrame = Time.frameCount;
                    job.StableFrames = 0;
                    AddLog(job, $"Step {job.StepIndex}: {step["type"]} started.");
                }

                bool completed = ProcessStep(job, step, now);
                if (!completed || job.Status != "running") return;
                AddLog(job, $"Step {job.StepIndex}: {step["type"]} completed.");
                job.StepIndex++;
                job.StepStartedAt = -1;
                job.StepStartedFrame = -1;
                job.StableFrames = 0;
                job.LastConditionFrame = -1;
                processed++;
                now = EditorApplication.timeSinceStartup;
            }
        }

        private static bool ProcessStep(Job job, JObject step, double now)
        {
            string type = step["type"]?.ToString()?.ToLowerInvariant();
            if (type == "delay")
            {
                if (step["frames"] != null)
                    return Time.frameCount - job.StepStartedFrame >= Math.Max(0, step["frames"].Value<int>());
                return now - job.StepStartedAt >= Math.Max(0.0, step["seconds"]?.Value<double>() ?? 0.0);
            }

            if (type is "wait" or "assert")
            {
                JObject condition = step["condition"] as JObject;
                PlayModeConditionResult result = PlayModeConditionEvaluator.Evaluate(condition);
                job.LastCondition = result;
                if (result.Error != null)
                {
                    Finish(job, "failed", $"Step {job.StepIndex} condition error: {result.Error}");
                    return false;
                }
                if (type == "assert")
                {
                    if (!result.Matched)
                        Finish(job, "failed", $"Assertion failed: {result.Description}");
                    return result.Matched;
                }

                int stableRequired = Mathf.Clamp(
                    step["stable_for_frames"]?.Value<int>()
                        ?? step["stableForFrames"]?.Value<int>()
                        ?? 1,
                    1,
                    300);
                if (job.LastConditionFrame != Time.frameCount)
                {
                    job.StableFrames = result.Matched ? job.StableFrames + 1 : 0;
                    job.LastConditionFrame = Time.frameCount;
                }
                if (job.StableFrames >= stableRequired) return true;

                double stepTimeout = step["timeout_seconds"]?.Value<double>()
                    ?? step["timeoutSeconds"]?.Value<double>()
                    ?? job.TimeoutSeconds;
                if (now - job.StepStartedAt > stepTimeout)
                    Finish(job, "failed", $"Wait timed out after {stepTimeout:0.###}s: {result.Description}");
                return false;
            }

            JObject input = type == "input" && step["input"] is JObject nested
                ? (JObject)nested.DeepClone()
                : (JObject)step.DeepClone();
            input["action"] = type == "input"
                ? input["action"] ?? input["type"]
                : type;
            input.Remove("type");
            PlayModeInputResult inputResult = PlayModeInputService.Execute(input, allowDuringJob: true);
            if (!inputResult.Success)
            {
                Finish(job, "failed", $"Input step failed ({inputResult.Code}): {inputResult.Message}");
                return false;
            }
            job.NextEligibleFrame = Time.frameCount + 1;
            return true;
        }

        private static void Finish(Job job, string status, string error)
        {
            job.Status = status;
            job.Error = error;
            job.FinishedAt = EditorApplication.timeSinceStartup;
            AddLog(job, error == null ? $"Job {status}." : $"Job {status}: {error}");
            PlayModeInputBackendRegistry.ReleaseAll();
            if (ReferenceEquals(_active, job)) _active = null;
        }

        private static void InterruptActive(string reason)
        {
            if (_active is { Status: "running" } job)
                Finish(job, "interrupted", reason);
            else
                PlayModeInputBackendRegistry.ReleaseAll();
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
                InterruptActive("play_mode_exited");
        }

        private static void AddLog(Job job, string message)
        {
            job.Log.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}");
            if (job.Log.Count > MaxLogEntries)
                job.Log.RemoveRange(0, job.Log.Count - MaxLogEntries);
        }

        private static void PruneJobs()
        {
            while (Jobs.Count > MaxRetainedJobs && JobOrder.Count > 0)
            {
                string id = JobOrder.Dequeue();
                if (_active != null && id == _active.Id)
                {
                    JobOrder.Enqueue(id);
                    break;
                }
                Jobs.Remove(id);
            }
        }
    }
}
