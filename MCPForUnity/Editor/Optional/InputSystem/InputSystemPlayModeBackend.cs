using System;
using System.Collections.Generic;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services.PlayMode;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using InputSystemApi = UnityEngine.InputSystem.InputSystem;
using InputTouchPhase = UnityEngine.InputSystem.TouchPhase;

namespace MCPForUnity.Editor.Optional.InputSystem
{
    [InitializeOnLoad]
    internal sealed class InputSystemPlayModeBackend : IPlayModeInputBackend
    {
        private sealed class ScheduledRelease
        {
            internal int DueFrame;
            internal Action Release;
        }

        private static readonly InputSystemPlayModeBackend Instance = new();
        private readonly HashSet<Key> _pressedKeys = new();
        private readonly HashSet<MouseButton> _pressedMouseButtons = new();
        private readonly HashSet<GamepadButton> _pressedGamepadButtons = new();
        private readonly List<ScheduledRelease> _scheduledReleases = new();
        private Vector2 _mousePosition;
        private Vector2 _mouseDelta;
        private Vector2 _leftStick;
        private Vector2 _rightStick;
        private float _leftTrigger;
        private float _rightTrigger;
        private Vector2 _touchPosition;

        static InputSystemPlayModeBackend()
        {
            PlayModeInputBackendRegistry.RegisterInputSystem(Instance);
            EditorApplication.update += Instance.Tick;
        }

        public string Name => "unity_input_system";

        public object Capabilities => new
        {
            actions = new[] { "key", "mouse", "touch", "gamepad", "ui_click", "release_all" },
            tap_spans_frames = true,
            coordinate_origin = "top_left",
        };

        public PlayModeInputResult Execute(JObject parameters)
        {
            try
            {
                string action = parameters?["action"]?.ToString()?.ToLowerInvariant();
                return action switch
                {
                    "key" => ExecuteKey(parameters),
                    "mouse" => ExecuteMouse(parameters),
                    "ui_click" => ExecuteUiClick(parameters),
                    "touch" => ExecuteTouch(parameters),
                    "gamepad" => ExecuteGamepad(parameters),
                    "release_all" => ReleaseAllResult(),
                    _ => PlayModeInputResult.Fail("unsupported_input_action", $"Input System does not support '{action}'."),
                };
            }
            catch (Exception ex)
            {
                return PlayModeInputResult.Fail("input_injection_failed", ex.Message);
            }
        }

        public void ReleaseAll()
        {
            _scheduledReleases.Clear();
            _pressedKeys.Clear();
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
                InputSystemApi.QueueStateEvent(keyboard, new KeyboardState());

            Mouse mouse = Mouse.current;
            if (mouse != null)
                InputSystemApi.QueueStateEvent(mouse, new MouseState { position = _mousePosition });
            _pressedMouseButtons.Clear();

            Touchscreen touchscreen = Touchscreen.current;
            if (touchscreen != null)
                QueueTouch(touchscreen, InputTouchPhase.Ended, _touchPosition);

            Gamepad gamepad = Gamepad.current;
            if (gamepad != null)
                InputSystemApi.QueueStateEvent(gamepad, new GamepadState());
            _pressedGamepadButtons.Clear();
            _leftStick = Vector2.zero;
            _rightStick = Vector2.zero;
            _leftTrigger = 0f;
            _rightTrigger = 0f;
        }

        private PlayModeInputResult ExecuteKey(JObject parameters)
        {
            string keyName = NormalizeKeyName(parameters?["key"]?.ToString());
            if (!Enum.TryParse(keyName, true, out Key key) || key == Key.None)
                return PlayModeInputResult.Fail("invalid_key", $"Unknown Input System key '{parameters?["key"]}'.");

            string phase = parameters?["phase"]?.ToString()?.ToLowerInvariant() ?? "tap";
            int holdFrames = Mathf.Clamp(parameters?["holdFrames"]?.Value<int>() ?? 1, 1, 120);
            if (phase is "tap" or "press") _pressedKeys.Add(key);
            else if (phase == "release") _pressedKeys.Remove(key);
            else return PlayModeInputResult.Fail("invalid_phase", "phase must be tap, press, or release.");

            QueueKeyboardState();
            if (phase == "tap")
                Schedule(holdFrames, () => { _pressedKeys.Remove(key); QueueKeyboardState(); });

            return PlayModeInputResult.Ok("Keyboard input queued.", new
            {
                backend = Name,
                key = key.ToString(),
                phase,
                release_frame = phase == "tap" ? Time.frameCount + holdFrames : (int?)null,
            });
        }

        private PlayModeInputResult ExecuteMouse(JObject parameters)
        {
            Mouse mouse = Mouse.current ?? InputSystemApi.AddDevice<Mouse>();
            Vector2? position = ReadScreenPosition(parameters?["position"] as JArray);
            if (position.HasValue)
                _mousePosition = position.Value;
            else if (_mousePosition == Vector2.zero)
                _mousePosition = mouse.position.ReadValue();
            if (parameters?["delta"] is JArray delta && delta.Count >= 2)
                _mouseDelta = ReadVector2(delta);
            else
                _mouseDelta = Vector2.zero;

            string buttonName = parameters?["button"]?.ToString()?.ToLowerInvariant();
            if (!string.IsNullOrEmpty(buttonName))
            {
                if (!TryResolveMouseButton(buttonName, out MouseButton button))
                    return PlayModeInputResult.Fail("invalid_mouse_button", $"Unknown mouse button '{buttonName}'.");
                string phase = parameters?["phase"]?.ToString()?.ToLowerInvariant() ?? "tap";
                bool pressed = phase is "tap" or "press";
                if (!pressed && phase != "release")
                    return PlayModeInputResult.Fail("invalid_phase", "phase must be tap, press, or release.");
                if (pressed) _pressedMouseButtons.Add(button);
                else _pressedMouseButtons.Remove(button);
                QueueMouseState(mouse);
                if (phase == "tap")
                {
                    int holdFrames = Mathf.Clamp(parameters?["holdFrames"]?.Value<int>() ?? 1, 1, 120);
                    Schedule(holdFrames, () =>
                    {
                        _pressedMouseButtons.Remove(button);
                        QueueMouseState(mouse);
                    });
                }
            }
            else
            {
                QueueMouseState(mouse);
            }

            return PlayModeInputResult.Ok("Mouse input queued.", new { backend = Name });
        }

        private PlayModeInputResult ExecuteUiClick(JObject parameters)
        {
            if (parameters?["position"] is not JArray position || position.Count < 2)
                return PlayModeInputResult.Fail(
                    "position_required",
                    "backend='input_system' ui_click requires normalized position [x,y]. Use the playmode UI resource to get element centers.");
            var copy = (JObject)parameters.DeepClone();
            copy["action"] = "mouse";
            copy["button"] = "left";
            copy["phase"] = "tap";
            return ExecuteMouse(copy);
        }

        private PlayModeInputResult ExecuteTouch(JObject parameters)
        {
            if (parameters?["position"] is not JArray position || position.Count < 2)
                return PlayModeInputResult.Fail("position_required", "touch requires normalized position [x,y].");
            Touchscreen touchscreen = Touchscreen.current ?? InputSystemApi.AddDevice<Touchscreen>();
            _touchPosition = ReadScreenPosition(position).Value;
            string phase = parameters?["phase"]?.ToString()?.ToLowerInvariant() ?? "tap";
            if (phase is not ("tap" or "press" or "release"))
                return PlayModeInputResult.Fail("invalid_phase", "phase must be tap, press, or release.");
            QueueTouch(touchscreen, phase == "release" ? InputTouchPhase.Ended : InputTouchPhase.Began, _touchPosition);
            if (phase == "tap")
            {
                int holdFrames = Mathf.Clamp(parameters?["holdFrames"]?.Value<int>() ?? 1, 1, 120);
                Schedule(holdFrames, () => QueueTouch(touchscreen, InputTouchPhase.Ended, _touchPosition));
            }
            return PlayModeInputResult.Ok("Touch input queued.", new { backend = Name, phase });
        }

        private PlayModeInputResult ExecuteGamepad(JObject parameters)
        {
            Gamepad gamepad = Gamepad.current ?? InputSystemApi.AddDevice<Gamepad>();
            string controlName = (parameters?["control"] ?? parameters?["button"])?.ToString();
            if (string.IsNullOrWhiteSpace(controlName))
                return PlayModeInputResult.Fail("control_required", "gamepad requires control or button.");

            if (TryResolveGamepadButton(controlName, out GamepadButton button))
            {
                string phase = parameters?["phase"]?.ToString()?.ToLowerInvariant() ?? "tap";
                bool pressed = phase is "tap" or "press";
                if (!pressed && phase != "release")
                    return PlayModeInputResult.Fail("invalid_phase", "phase must be tap, press, or release.");
                if (pressed) _pressedGamepadButtons.Add(button);
                else _pressedGamepadButtons.Remove(button);
                QueueGamepadState(gamepad);
                if (phase == "tap")
                {
                    int holdFrames = Mathf.Clamp(parameters?["holdFrames"]?.Value<int>() ?? 1, 1, 120);
                    Schedule(holdFrames, () =>
                    {
                        _pressedGamepadButtons.Remove(button);
                        QueueGamepadState(gamepad);
                    });
                }
            }
            else if (string.Equals(controlName, "leftTrigger", StringComparison.OrdinalIgnoreCase))
            {
                _leftTrigger = parameters?["value"]?.Value<float>() ?? 0f;
                QueueGamepadState(gamepad);
            }
            else if (string.Equals(controlName, "rightTrigger", StringComparison.OrdinalIgnoreCase))
            {
                _rightTrigger = parameters?["value"]?.Value<float>() ?? 0f;
                QueueGamepadState(gamepad);
            }
            else if (string.Equals(controlName, "leftStick", StringComparison.OrdinalIgnoreCase)
                     && parameters?["position"] is JArray left)
            {
                _leftStick = ReadVector2(left);
                QueueGamepadState(gamepad);
            }
            else if (string.Equals(controlName, "rightStick", StringComparison.OrdinalIgnoreCase)
                     && parameters?["position"] is JArray right)
            {
                _rightStick = ReadVector2(right);
                QueueGamepadState(gamepad);
            }
            else
            {
                return PlayModeInputResult.Fail("invalid_gamepad_control", $"Unknown gamepad control '{controlName}'.");
            }

            return PlayModeInputResult.Ok("Gamepad input queued.", new { backend = Name, control = controlName });
        }

        private PlayModeInputResult ReleaseAllResult()
        {
            ReleaseAll();
            return PlayModeInputResult.Ok("Released all Input System controls.", new { backend = Name });
        }

        private void QueueKeyboardState()
        {
            Keyboard keyboard = Keyboard.current ?? InputSystemApi.AddDevice<Keyboard>();
            InputSystemApi.QueueStateEvent(keyboard, new KeyboardState(_pressedKeys.ToArray()));
        }

        private void QueueMouseState(Mouse mouse)
        {
            var state = new MouseState
            {
                position = _mousePosition,
                delta = _mouseDelta,
            };
            foreach (MouseButton button in _pressedMouseButtons)
                state = state.WithButton(button);
            InputSystemApi.QueueStateEvent(mouse, state);
        }

        private static void QueueTouch(Touchscreen touchscreen, InputTouchPhase phase, Vector2 position)
        {
            InputSystemApi.QueueStateEvent(touchscreen, new TouchState
            {
                touchId = 1,
                phase = phase,
                position = position,
                pressure = phase == InputTouchPhase.Ended ? 0f : 1f,
            });
        }

        private void QueueGamepadState(Gamepad gamepad)
        {
            var state = new GamepadState(_pressedGamepadButtons.ToArray())
            {
                leftStick = _leftStick,
                rightStick = _rightStick,
                leftTrigger = _leftTrigger,
                rightTrigger = _rightTrigger,
            };
            InputSystemApi.QueueStateEvent(gamepad, state);
        }

        private void Schedule(int holdFrames, Action release)
        {
            _scheduledReleases.Add(new ScheduledRelease
            {
                DueFrame = Time.frameCount + Math.Max(1, holdFrames),
                Release = release,
            });
        }

        private void Tick()
        {
            if (!EditorApplication.isPlaying || _scheduledReleases.Count == 0) return;
            for (int index = _scheduledReleases.Count - 1; index >= 0; index--)
            {
                ScheduledRelease scheduled = _scheduledReleases[index];
                if (Time.frameCount < scheduled.DueFrame) continue;
                _scheduledReleases.RemoveAt(index);
                try { scheduled.Release(); }
                catch (Exception ex) { McpLog.Warn($"Scheduled input release failed: {ex.Message}"); }
            }
        }

        private static Vector2? ReadScreenPosition(JArray normalized)
            => normalized is { Count: >= 2 }
                ? PlayModeInputService.NormalizedToScreen(normalized)
                : null;

        private static Vector2 ReadVector2(JArray array)
            => new(array?[0]?.Value<float>() ?? 0f, array?[1]?.Value<float>() ?? 0f);

        private static bool TryResolveMouseButton(string button, out MouseButton value)
        {
            switch (button)
            {
                case "left": case "0": value = MouseButton.Left; return true;
                case "right": case "1": value = MouseButton.Right; return true;
                case "middle": case "2": value = MouseButton.Middle; return true;
                case "forward": value = MouseButton.Forward; return true;
                case "back": value = MouseButton.Back; return true;
                default: value = default; return false;
            }
        }

        private static bool TryResolveGamepadButton(string control, out GamepadButton value)
        {
            switch (control?.Trim().ToLowerInvariant())
            {
                case "buttonsouth": case "south": case "a": value = GamepadButton.South; return true;
                case "buttonnorth": case "north": case "y": value = GamepadButton.North; return true;
                case "buttonwest": case "west": case "x": value = GamepadButton.West; return true;
                case "buttoneast": case "east": case "b": value = GamepadButton.East; return true;
                case "leftshoulder": case "lb": value = GamepadButton.LeftShoulder; return true;
                case "rightshoulder": case "rb": value = GamepadButton.RightShoulder; return true;
                case "start": value = GamepadButton.Start; return true;
                case "select": case "back": value = GamepadButton.Select; return true;
                case "leftstickpress": value = GamepadButton.LeftStick; return true;
                case "rightstickpress": value = GamepadButton.RightStick; return true;
                case "dpadup": value = GamepadButton.DpadUp; return true;
                case "dpaddown": value = GamepadButton.DpadDown; return true;
                case "dpadleft": value = GamepadButton.DpadLeft; return true;
                case "dpadright": value = GamepadButton.DpadRight; return true;
                default: value = default; return false;
            }
        }

        private static string NormalizeKeyName(string value)
        {
            return value?.Trim().ToLowerInvariant() switch
            {
                " " => "Space",
                "esc" => "Escape",
                "ctrl" => "LeftCtrl",
                "shift" => "LeftShift",
                "alt" => "LeftAlt",
                "enter" => "Enter",
                var normalized => normalized,
            };
        }
    }
}
