using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services.PlayMode;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Tools.PlayMode
{
    [McpForUnityTool(
        "manage_playmode_test",
        AutoRegister = false,
        Group = "testing",
        RequiresPolling = true,
        PollAction = "status",
        MaxPollSeconds = 300)]
    public static class ManagePlayModeTest
    {
        public static object HandleCommand(JObject @params)
        {
            var p = new ToolParams(@params ?? new JObject());
            string action = p.Get("action")?.ToLowerInvariant();
            return action switch
            {
                "wait" => StartWait(p, @params),
                "run_sequence" => StartSequence(p, @params),
                "status" => GetStatus(p),
                "cancel" => Cancel(p),
                _ => new ErrorResponse("invalid_action", new
                {
                    message = "action must be wait, run_sequence, status, or cancel.",
                }),
            };
        }

        private static object StartWait(ToolParams p, JObject parameters)
        {
            if (!EditorApplication.isPlaying)
                return PlayModeRequired();
            if (parameters?["condition"] is not JObject condition)
                return new ErrorResponse("condition_required");
            double timeout = ClampTimeout(parameters["timeoutSeconds"]?.Value<double>() ?? 30.0);
            int stable = Mathf.Clamp(parameters["stableForFrames"]?.Value<int>() ?? 1, 1, 300);
            PlayModeTestJobManager.Job job = PlayModeTestJobManager.StartWait(
                condition,
                timeout,
                stable,
                out string error);
            return job == null
                ? new ErrorResponse("playmode_job_not_started", new { message = error })
                : Pending(job, "Play Mode wait started.");
        }

        private static object StartSequence(ToolParams p, JObject parameters)
        {
            if (!EditorApplication.isPlaying)
                return PlayModeRequired();
            if (parameters?["steps"] is not JArray steps)
                return new ErrorResponse("steps_required");
            double timeout = ClampTimeout(parameters["timeoutSeconds"]?.Value<double>() ?? 30.0);
            PlayModeTestJobManager.Job job = PlayModeTestJobManager.Start(steps, timeout, out string error);
            return job == null
                ? new ErrorResponse("playmode_job_not_started", new { message = error })
                : Pending(job, "Play Mode sequence started.");
        }

        private static object GetStatus(ToolParams p)
        {
            string jobId = p.Get("jobId") ?? p.Get("job_id");
            PlayModeTestJobManager.Job job = PlayModeTestJobManager.Get(jobId);
            if (job == null) return new ErrorResponse("unknown_job_id");
            object data = PlayModeTestJobManager.ToSerializable(job);
            return job.Status == "running"
                ? new PendingResponse("Play Mode test is running.", 0.2, data)
                : new SuccessResponse($"Play Mode test {job.Status}.", data);
        }

        private static object Cancel(ToolParams p)
        {
            string jobId = p.Get("jobId") ?? p.Get("job_id");
            if (!PlayModeTestJobManager.Cancel(jobId, out string error))
                return new ErrorResponse("cancel_failed", new { message = error });
            PlayModeTestJobManager.Job job = PlayModeTestJobManager.Get(jobId);
            return new SuccessResponse("Play Mode test cancelled.", PlayModeTestJobManager.ToSerializable(job));
        }

        private static PendingResponse Pending(PlayModeTestJobManager.Job job, string message)
            => new(message, 0.2, PlayModeTestJobManager.ToSerializable(job));

        private static ErrorResponse PlayModeRequired()
            => new("play_mode_required", new
            {
                message = "Enter Play Mode before starting a Play Mode test job.",
            });

        private static double ClampTimeout(double value)
            => System.Math.Max(0.01, System.Math.Min(300.0, value));
    }
}
