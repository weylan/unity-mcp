using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Runtime.Helpers;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MCPForUnity.Editor.Services.PlayMode
{
    internal static class PlayModeStateReader
    {
        private static long _sequence;

        internal static object BuildSnapshot(JObject parameters)
        {
            var p = new ToolParams(parameters ?? new JObject());
            bool includeUi = p.GetBool("includeUI", true);
            int uiLimit = Math.Max(0, Math.Min(100, p.GetInt("uiLimit") ?? 20));
            string playerReference = p.Get("player");
            Scene scene = SceneManager.GetActiveScene();
            Camera camera = Camera.main ?? UnityFindObjectsCompat.FindAll<Camera>().FirstOrDefault();
            GameObject player = ResolvePlayer(playerReference);
            List<PlayModeUiItem> uiItems = includeUi
                ? PlayModeUiScanner.Scan()
                : new List<PlayModeUiItem>();

            return new
            {
                schema_version = "unity-mcp/playmode-state@1",
                observed_at_unix_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                sequence = Interlocked.Increment(ref _sequence),
                play_mode = new
                {
                    is_playing = UnityEditor.EditorApplication.isPlaying,
                    is_paused = UnityEditor.EditorApplication.isPaused,
                },
                scene = new
                {
                    name = scene.name,
                    path = scene.path,
                    build_index = scene.buildIndex,
                    is_loaded = scene.isLoaded,
                    root_count = scene.isLoaded ? scene.rootCount : 0,
                },
                frame = new
                {
                    count = Time.frameCount,
                    time = Time.time,
                    unscaled_time = Time.unscaledTime,
                    delta_time = Time.deltaTime,
                    unscaled_delta_time = Time.unscaledDeltaTime,
                    time_scale = Time.timeScale,
                },
                screen = new
                {
                    width = Screen.width,
                    height = Screen.height,
                    dpi = Screen.dpi,
                },
                camera = BuildCamera(camera),
                player = BuildPlayer(player),
                ui = new
                {
                    included = includeUi,
                    total_count = uiItems.Count,
                    returned_count = Math.Min(uiLimit, uiItems.Count),
                    truncated = uiItems.Count > uiLimit,
                    items = uiItems.Take(uiLimit).Select(item => item.ToSerializable()).ToArray(),
                },
            };
        }

        internal static object BuildUiPage(JObject parameters)
        {
            var p = new ToolParams(parameters ?? new JObject());
            int pageSize = Math.Max(1, Math.Min(100, p.GetInt("pageSize") ?? 50));
            int cursor = Math.Max(0, p.GetInt("cursor") ?? 0);
            string framework = (p.Get("framework", "all") ?? "all").ToLowerInvariant();
            List<PlayModeUiItem> items = PlayModeUiScanner.Scan(framework);
            int next = cursor + pageSize;
            return new
            {
                schema_version = "unity-mcp/playmode-ui@1",
                observed_at_unix_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                framework,
                cursor,
                page_size = pageSize,
                total_count = items.Count,
                next_cursor = next < items.Count ? (int?)next : null,
                has_more = next < items.Count,
                items = items.Skip(cursor).Take(pageSize).Select(item => item.ToSerializable()).ToArray(),
            };
        }

        private static GameObject ResolvePlayer(string playerReference)
        {
            if (!string.IsNullOrWhiteSpace(playerReference))
                return PlayModeTargetResolver.Resolve(new JValue(playerReference));
            try
            {
                return GameObject.FindGameObjectWithTag("Player");
            }
            catch
            {
                return null;
            }
        }

        private static object BuildCamera(Camera camera)
        {
            if (camera == null) return null;
            Transform transform = camera.transform;
            return new
            {
                instance_id = camera.gameObject.GetInstanceIDCompat(),
                name = camera.gameObject.name,
                path = PlayModeUiScanner.GetTransformPath(transform),
                position = Vector3Data(transform.position),
                forward = Vector3Data(transform.forward),
                field_of_view = camera.fieldOfView,
                orthographic = camera.orthographic,
                orthographic_size = camera.orthographicSize,
                near_clip_plane = camera.nearClipPlane,
                far_clip_plane = camera.farClipPlane,
            };
        }

        private static object BuildPlayer(GameObject player)
        {
            if (player == null) return null;
            Transform transform = player.transform;
            Rigidbody rigidbody = player.GetComponent<Rigidbody>();
            CharacterController controller = player.GetComponent<CharacterController>();
            Animator animator = player.GetComponent<Animator>();
            return new
            {
                instance_id = player.GetInstanceIDCompat(),
                name = player.name,
                path = PlayModeUiScanner.GetTransformPath(transform),
                tag = player.tag,
                active = player.activeInHierarchy,
                position = Vector3Data(transform.position),
                rotation = Vector3Data(transform.eulerAngles),
                velocity = rigidbody != null
                    ? Vector3Data(rigidbody.velocity)
                    : controller != null ? Vector3Data(controller.velocity) : null,
                animator = BuildAnimator(animator),
            };
        }

        private static object BuildAnimator(Animator animator)
        {
            if (animator == null) return null;
            var layers = new List<object>();
            for (int index = 0; index < animator.layerCount; index++)
            {
                AnimatorStateInfo state = animator.IsInTransition(index)
                    ? animator.GetNextAnimatorStateInfo(index)
                    : animator.GetCurrentAnimatorStateInfo(index);
                layers.Add(new
                {
                    index,
                    name = animator.GetLayerName(index),
                    state_hash = state.fullPathHash,
                    normalized_time = state.normalizedTime,
                    is_in_transition = animator.IsInTransition(index),
                });
            }

            var parameters = new List<object>();
            foreach (AnimatorControllerParameter parameter in animator.parameters)
            {
                object value = parameter.type switch
                {
                    AnimatorControllerParameterType.Float => animator.GetFloat(parameter.nameHash),
                    AnimatorControllerParameterType.Int => animator.GetInteger(parameter.nameHash),
                    AnimatorControllerParameterType.Bool => animator.GetBool(parameter.nameHash),
                    AnimatorControllerParameterType.Trigger => animator.GetBool(parameter.nameHash),
                    _ => null,
                };
                parameters.Add(new { name = parameter.name, type = parameter.type.ToString(), value });
            }

            return new
            {
                enabled = animator.enabled,
                speed = animator.speed,
                layers,
                parameters,
            };
        }

        internal static object Vector3Data(Vector3 value)
            => new { x = value.x, y = value.y, z = value.z };
    }
}
