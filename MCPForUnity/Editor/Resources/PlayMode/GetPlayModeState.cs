using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services.PlayMode;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace MCPForUnity.Editor.Resources.PlayMode
{
    [McpForUnityResource("get_playmode_state")]
    public static class GetPlayModeState
    {
        public static object HandleCommand(JObject @params)
        {
            if (!EditorApplication.isPlaying)
                return new ErrorResponse("play_mode_required", new
                {
                    message = "Enter Play Mode before reading the Play Mode state resource.",
                });

            return new SuccessResponse(
                "Retrieved Play Mode state.",
                PlayModeStateReader.BuildSnapshot(@params));
        }
    }
}
