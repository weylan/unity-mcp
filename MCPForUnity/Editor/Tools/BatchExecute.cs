using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MCPForUnity.Editor.Constants;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace MCPForUnity.Editor.Tools
{
    /// <summary>
    /// Executes multiple MCP commands within a single Unity-side handler. Commands are executed sequentially
    /// on the main thread to preserve determinism and Unity API safety.
    /// </summary>
    [McpForUnityTool("batch_execute", AutoRegister = false)]
    public static class BatchExecute
    {
        /// <summary>Default limit when no EditorPrefs override is set.</summary>
        internal const int DefaultMaxCommandsPerBatch = 25;

        /// <summary>Hard ceiling to prevent extreme editor freezes regardless of user setting.</summary>
        internal const int AbsoluteMaxCommandsPerBatch = 100;

        /// <summary>
        /// Returns the user-configured max commands per batch, clamped between 1 and <see cref="AbsoluteMaxCommandsPerBatch"/>.
        /// </summary>
        internal static int GetMaxCommandsPerBatch()
        {
            int configured = EditorPrefs.GetInt(EditorPrefKeys.BatchExecuteMaxCommands, DefaultMaxCommandsPerBatch);
            return Math.Clamp(configured, 1, AbsoluteMaxCommandsPerBatch);
        }

        public static async Task<object> HandleCommand(JObject @params)
        {
            if (@params == null)
            {
                return new ErrorResponse("'commands' payload is required.");
            }

            var commandsToken = @params["commands"] as JArray;
            if (commandsToken == null || commandsToken.Count == 0)
            {
                return new ErrorResponse("Provide at least one command entry in 'commands'.");
            }

            int maxCommands = GetMaxCommandsPerBatch();
            if (commandsToken.Count > maxCommands)
            {
                return new ErrorResponse(
                    $"A maximum of {maxCommands} commands are allowed per batch (configurable in MCP Tools window, hard max {AbsoluteMaxCommandsPerBatch}).");
            }

            // A normal run_tests call transfers the batch operation lock to a physical owner and
            // returns before Unity starts executing tests. No later child may run under that fence.
            for (int i = 0; i < commandsToken.Count; i++)
            {
                if (commandsToken[i] is JObject candidate && IsNormalRunTestsStart(candidate))
                {
                    if (i != commandsToken.Count - 1)
                    {
                        return new ErrorResponse(
                            "A normal run_tests start must be the last executable command in batch_execute.");
                    }
                }
            }

            bool failFast = @params.Value<bool?>("failFast") ?? false;
            bool parallelRequested = @params.Value<bool?>("parallel") ?? false;
            int? maxParallel = @params.Value<int?>("maxParallelism");

            if (parallelRequested)
            {
                McpLog.Info("[GuardedNotice] batch_execute parallel mode requested; running sequentially on the Unity main thread for safety.");
            }

            var commandResults = new List<object>(commandsToken.Count);
            int invocationSuccessCount = 0;
            int invocationFailureCount = 0;
            bool anyCommandFailed = false;

            string batchAutoToken = null;
            bool batchNeedsLock = false;
            bool hasExplicitLockToken = false;
            string batchClientToken = new ToolParams(@params).Get("editor_lock_token");
            foreach (var cmdToken in commandsToken)
            {
                if (cmdToken is JObject cmdObj)
                {
                    string tool = cmdObj["tool"]?.ToString();
                    var cmdParams = NormalizeParameterKeys(cmdObj["params"] as JObject ?? new JObject());
                    string act = cmdParams?.Value<string>("action");
                    if (!IsRunTestsRecovery(tool, cmdParams)
                        && SharedEditorOperationLock.IsHighRiskTool(tool, act))
                    {
                        batchNeedsLock = true;
                        break;
                    }
                }
            }

            if (batchNeedsLock)
            {
                int batchTtl = Math.Min(commandsToken.Count * 15, 300);
                if (!string.IsNullOrEmpty(batchClientToken))
                {
                    hasExplicitLockToken = SharedEditorOperationLock.Reenter(
                        batchClientToken,
                        "batch_execute",
                        batchTtl);
                    if (!hasExplicitLockToken)
                    {
                        if (SharedEditorOperationLock.TryGetMatchingAttachedToken(batchClientToken, out var attachedHolder))
                        {
                            return SharedEditorOperationLock.BuildBusyResponse(attachedHolder, "batch_execute");
                        }
                        if (SharedEditorOperationLock.ValidateToken(batchClientToken))
                        {
                            return SharedEditorOperationLock.BuildPersistenceFailedResponse("batch_execute");
                        }
                        return SharedEditorOperationLock.BuildTokenInvalidResponse(batchClientToken, "batch_execute");
                    }
                }
                else
                {
                    var lockResult = SharedEditorOperationLock.TryAcquire(
                        "auto", "batch_execute", isExplicit: false, ttlSeconds: batchTtl);
                    if (!lockResult.Acquired)
                    {
                        return lockResult.PersistenceFailed
                            ? SharedEditorOperationLock.BuildPersistenceFailedResponse("batch_execute")
                            : SharedEditorOperationLock.BuildBusyResponse(lockResult.BusyHolder, "batch_execute");
                    }
                    batchAutoToken = lockResult.Token;
                }
            }

            try
            {
            foreach (var token in commandsToken)
            {
                if (token is not JObject commandObj)
                {
                    invocationFailureCount++;
                    anyCommandFailed = true;
                    commandResults.Add(new
                    {
                        tool = (string)null,
                        callSucceeded = false,
                        error = "Command entries must be JSON objects."
                    });
                    if (failFast)
                    {
                        break;
                    }
                    continue;
                }

                string toolName = commandObj["tool"]?.ToString();
                var rawParams = commandObj["params"] as JObject ?? new JObject();
                var commandParams = NormalizeParameterKeys(rawParams);

                if (string.IsNullOrWhiteSpace(toolName))
                {
                    invocationFailureCount++;
                    anyCommandFailed = true;
                    commandResults.Add(new
                    {
                        tool = toolName,
                        callSucceeded = false,
                        error = "Each command must include a non-empty 'tool' field."
                    });
                    if (failFast)
                    {
                        break;
                    }
                    continue;
                }

                // Block disabled tools (mirrors TransportCommandDispatcher check)
                var toolMeta = MCPServiceLocator.ToolDiscovery.GetToolMetadata(toolName);
                if (toolMeta != null && !MCPServiceLocator.ToolDiscovery.IsToolEnabled(toolName))
                {
                    invocationFailureCount++;
                    anyCommandFailed = true;
                    commandResults.Add(new
                    {
                        tool = toolName,
                        callSucceeded = false,
                        result = new ErrorResponse($"Tool '{toolName}' is disabled in the Unity Editor.")
                    });
                    if (failFast) break;
                    continue;
                }

                var guardDecision = SharedEditorCommandGuard.Evaluate(toolName, commandParams, hasExplicitLockToken);
                if (!guardDecision.Allowed)
                {
                    invocationFailureCount++;
                    anyCommandFailed = true;
                    SharedEditorCommandGuard.LogDecision(guardDecision, commandParams, "tool");
                    commandResults.Add(new
                    {
                        tool = toolName,
                        callSucceeded = false,
                        result = guardDecision.ToErrorResponse()
                    });
                    if (failFast) break;
                    continue;
                }

                if (guardDecision.WarnOnly)
                {
                    SharedEditorCommandGuard.LogDecision(guardDecision, commandParams, "tool");
                }

                try
                {
                    if (string.Equals(toolName, "run_tests", StringComparison.OrdinalIgnoreCase)
                        && !IsRunTestsRecovery(toolName, commandParams))
                    {
                        string effectiveLockToken = hasExplicitLockToken ? batchClientToken : batchAutoToken;
                        if (!string.IsNullOrEmpty(effectiveLockToken))
                        {
                            commandParams[RunTests.InternalEditorLockTokenParameter] = effectiveLockToken;
                        }
                    }

                    var result = await CommandRegistry.InvokeCommandAsync(toolName, commandParams).ConfigureAwait(true);
                    bool callSucceeded = DetermineCallSucceeded(result);
                    if (callSucceeded)
                    {
                        invocationSuccessCount++;
                    }
                    else
                    {
                        invocationFailureCount++;
                        anyCommandFailed = true;
                    }

                    commandResults.Add(new
                    {
                        tool = toolName,
                        callSucceeded,
                        result
                    });

                    if (!callSucceeded && failFast)
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    invocationFailureCount++;
                    anyCommandFailed = true;
                    commandResults.Add(new
                    {
                        tool = toolName,
                        callSucceeded = false,
                        error = ex.Message
                    });

                    if (failFast)
                    {
                        break;
                    }
                }
            }
            }
            finally
            {
                SharedEditorOperationLock.ReleaseIfAutoLock(batchAutoToken);
            }

            bool overallSuccess = !anyCommandFailed;
            var data = new
            {
                results = commandResults,
                callSuccessCount = invocationSuccessCount,
                callFailureCount = invocationFailureCount,
                parallelRequested,
                parallelApplied = false,
                maxParallelism = maxParallel
            };

            return overallSuccess
                ? new SuccessResponse("Batch execution completed.", data)
                : new ErrorResponse("One or more commands failed.", data);
        }

        private static bool DetermineCallSucceeded(object result)
        {
            if (result == null)
            {
                return true;
            }

            if (result is IMcpResponse response)
            {
                return response.Success;
            }

            if (result is JObject obj)
            {
                var successToken = obj["success"];
                if (successToken != null && successToken.Type == JTokenType.Boolean)
                {
                    return successToken.Value<bool>();
                }
            }

            if (result is JToken token)
            {
                var successToken = token["success"];
                if (successToken != null && successToken.Type == JTokenType.Boolean)
                {
                    return successToken.Value<bool>();
                }
            }

            return true;
        }

        private static bool IsNormalRunTestsStart(JObject command)
        {
            string tool = command?["tool"]?.ToString();
            var parameters = command?["params"] as JObject ?? new JObject();
            return string.Equals(tool, "run_tests", StringComparison.OrdinalIgnoreCase)
                   && !IsRunTestsRecovery(tool, parameters);
        }

        private static bool IsRunTestsRecovery(string tool, JObject parameters)
        {
            if (!string.Equals(tool, "run_tests", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return RunTests.IsClearStuckRequest(parameters);
        }

        private static JObject NormalizeParameterKeys(JObject source)
        {
            if (source == null)
            {
                return new JObject();
            }

            var normalized = new JObject();
            foreach (var property in source.Properties())
            {
                string normalizedName = ToCamelCase(property.Name);
                normalized[normalizedName] = property.Value;
            }
            return normalized;
        }

        private static string ToCamelCase(string key) => StringCaseUtility.ToCamelCase(key);
    }
}
