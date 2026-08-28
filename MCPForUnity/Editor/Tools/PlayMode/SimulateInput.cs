using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services.PlayMode;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace MCPForUnity.Editor.Tools.PlayMode
{
    [McpForUnityTool("simulate_input", AutoRegister = false, Group = "testing")]
    public static class SimulateInput
    {
        public static object HandleCommand(JObject @params)
        {
            string action = @params?["action"]?.ToString()?.Trim().ToLowerInvariant();
            if (action == "capabilities")
                return new SuccessResponse("Play Mode input capabilities retrieved.", PlayModeInputService.GetCapabilities());
            if (!EditorApplication.isPlaying)
                return new ErrorResponse("play_mode_required", new
                {
                    message = "Enter Play Mode before simulating input.",
                });
            return PlayModeInputService.Execute(@params ?? new JObject(), allowDuringJob: false).ToResponse();
        }
    }
}
