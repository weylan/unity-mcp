using System;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;

namespace MCPForUnity.Editor.Tools
{
    [McpForUnityTool("manage_editor_lock", AutoRegister = false)]
    public static class ManageEditorLock
    {
        public static object HandleCommand(JObject @params)
        {
            if (@params == null)
            {
                return new ErrorResponse("Parameters cannot be null.");
            }

            var p = new ToolParams(@params);
            var actionResult = p.GetRequired("action");
            if (!actionResult.IsSuccess)
            {
                return new ErrorResponse(actionResult.ErrorMessage);
            }

            string action = actionResult.Value.ToLowerInvariant();

            switch (action)
            {
                case "acquire":
                    return HandleAcquire(p);
                case "release":
                    return HandleRelease(p);
                case "extend":
                    return HandleExtend(p);
                case "get_state":
                    return HandleGetState();
                case "force_release":
                    return HandleForceRelease();
                default:
                    return new ErrorResponse($"Unknown action '{action}'. Valid: acquire, release, extend, get_state, force_release.");
            }
        }

        private static object HandleAcquire(ToolParams p)
        {
            string holderHint = p.Get("holder_hint") ?? p.Get("holderHint");
            if (string.IsNullOrWhiteSpace(holderHint))
            {
                return new ErrorResponse("'holder_hint' is required for acquire.");
            }

            string reason = p.Get("reason") ?? "";
            int ttl = p.GetInt("ttl_seconds", 0);
            if (ttl <= 0) ttl = p.GetInt("ttlSeconds", 0);

            var result = SharedEditorOperationLock.TryAcquire(holderHint, reason, isExplicit: true, ttlSeconds: ttl);

            if (result.Acquired)
            {
                return new SuccessResponse("Lock acquired.", new
                {
                    acquired = true,
                    token = result.Token,
                    expires_at = result.ExpiresAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    expires_in_ms = Math.Max(0, (long)(result.ExpiresAtUtc - DateTime.UtcNow).TotalMilliseconds),
                });
            }

            return SharedEditorOperationLock.BuildBusyResponse(result.BusyHolder, "manage_editor_lock:acquire");
        }

        private static object HandleRelease(ToolParams p)
        {
            string token = p.Get("token");
            if (string.IsNullOrWhiteSpace(token))
            {
                return new ErrorResponse("'token' is required for release.");
            }

            bool released = SharedEditorOperationLock.Release(token);
            if (released)
            {
                return new SuccessResponse("Lock released.", new { released = true });
            }

            return new ErrorResponse("Token does not match current lock or no lock is held.", new { released = false });
        }

        private static object HandleExtend(ToolParams p)
        {
            string token = p.Get("token");
            if (string.IsNullOrWhiteSpace(token))
            {
                return new ErrorResponse("'token' is required for extend.");
            }

            int additionalSeconds = p.GetInt("additional_seconds", 60);
            if (additionalSeconds <= 0) additionalSeconds = p.GetInt("additionalSeconds", 60);

            bool extended = SharedEditorOperationLock.Extend(token, additionalSeconds);
            if (extended)
            {
                var state = SharedEditorOperationLock.GetState();
                return new SuccessResponse("Lock extended.", new
                {
                    extended = true,
                    new_expires_at = state.ExpiresAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    expires_in_ms = state.ExpiresInMs,
                });
            }

            return new ErrorResponse("Token does not match current lock or no lock is held.", new { extended = false });
        }

        private static object HandleGetState()
        {
            var state = SharedEditorOperationLock.GetState();
            return new SuccessResponse("Lock state retrieved.", new
            {
                locked = state.Locked,
                holder_hint = state.HolderHint,
                reason = state.Reason,
                acquired_at = state.Locked ? state.AcquiredAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") : null,
                expires_at = state.Locked ? state.ExpiresAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") : null,
                expires_in_ms = state.ExpiresInMs,
                is_explicit = state.IsExplicit,
            });
        }

        private static object HandleForceRelease()
        {
            var evicted = SharedEditorOperationLock.ForceRelease();
            if (!evicted.Locked)
            {
                return new SuccessResponse("No lock was held.", new { was_locked = false });
            }

            return new SuccessResponse("Lock force released.", new
            {
                was_locked = true,
                evicted_holder_hint = evicted.HolderHint,
                evicted_reason = evicted.Reason,
            });
        }
    }
}
