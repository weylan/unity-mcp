using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services.PlayMode;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace MCPForUnity.Editor.Resources.PlayMode
{
    [McpForUnityResource("get_playmode_ui")]
    public static class GetPlayModeUI
    {
        public static object HandleCommand(JObject @params)
        {
            if (!EditorApplication.isPlaying)
                return new ErrorResponse("play_mode_required", new
                {
                    message = "Enter Play Mode before reading the Play Mode UI resource.",
                });

            var p = new ToolParams(@params ?? new JObject());
            int pageSize = p.GetInt("pageSize") ?? 50;
            int cursor = p.GetInt("cursor") ?? 0;
            string framework = (p.Get("framework", "all") ?? "all").ToLowerInvariant();
            if (pageSize < 1 || pageSize > 100)
                return new ErrorResponse("invalid_page_size");
            if (cursor < 0)
                return new ErrorResponse("invalid_cursor");
            if (framework != "all" && framework != "ugui" && framework != "uitoolkit")
                return new ErrorResponse("invalid_framework");

            return new SuccessResponse(
                "Retrieved Play Mode UI.",
                PlayModeStateReader.BuildUiPage(@params));
        }
    }
}
