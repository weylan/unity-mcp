using MCPForUnity.Editor.Helpers;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MCPForUnity.Editor.Services.PlayMode
{
    internal static class PlayModeTargetResolver
    {
        internal static GameObject Resolve(JToken target, string searchMethod = null)
        {
            if (target == null) return null;
            if (target.Type == JTokenType.Object)
                return ObjectResolver.ResolveGameObject(target, searchMethod);
            if (!string.IsNullOrWhiteSpace(searchMethod))
                return GameObjectLookup.FindByTarget(target, searchMethod);

            string value = target.ToString();
            if (int.TryParse(value, out _))
            {
                GameObject byId = GameObjectLookup.FindByTarget(target, "by_id");
                if (byId != null) return byId;
            }
            if (value.Contains("/"))
            {
                GameObject byPath = GameObjectLookup.FindByTarget(target, "by_path");
                if (byPath != null) return byPath;
            }
            return GameObjectLookup.FindByTarget(target, "by_name");
        }
    }
}
