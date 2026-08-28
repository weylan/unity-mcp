---
id: playmode-testing
slug: /guides/playmode-testing
title: Play Mode Testing
sidebar_label: Play Mode Testing
description: Read runtime state, simulate input, and run deterministic action sequences in Unity Editor Play Mode.
---

# Play Mode Testing

The Play Mode testing surface drives a game running inside the Unity Editor. It does not connect to standalone players or devices.

## Enable the tools

The mutating tools belong to the `testing` group, which is disabled by default:

```text
manage_tools(action="activate", group="testing")
manage_editor(action="play")
```

Wait for `mcpforunity://editor/state` to report `ready_for_tools=true` after the Play Mode domain reload.

## Read runtime state

- `mcpforunity://playmode/state` returns a compact scene, frame, camera, player, Animator, and UI summary.
- `mcpforunity://playmode/ui` returns paginated uGUI, TextMeshPro, and UI Toolkit text/interactable elements.

Both resources return `play_mode_required` outside Play Mode. UI rectangles and centers use normalized coordinates with a top-left origin.

## Simulate input

`simulate_input` has two backends:

- `event_system` dispatches deterministic target-based uGUI click and drag events.
- `input_system` queues keyboard, mouse, touch, and gamepad state through `com.unity.inputsystem`.

The Input System integration is optional. Read `simulate_input(action="capabilities")` before using device input. Projects using only legacy `Input.GetKey` can still use target-based EventSystem actions, but legacy keyboard/gamepad state cannot be injected reliably through Unity's public API.

Always call `simulate_input(action="release_all")` after manually held input. Test jobs release all controls automatically on completion, cancellation, Play Mode exit, and assembly reload.

## Wait conditions

`manage_playmode_test(action="wait")` supports:

- `component_property`
- `object_exists`
- `ui_text`
- `scene`
- `play_mode`
- `frame_count`
- `animator_state`
- `animator_parameter`

Operators are `equals`, `not_equals`, `gt`, `gte`, `lt`, `lte`, `contains`, and `not_contains`. Use `tolerance` for floating-point equality and `stable_for_frames` to reject one-frame transients. Timeouts use unscaled Editor time, so they still complete when `Time.timeScale` is zero.

## Run sequences

Sequences allow at most 50 steps. Supported step types are input actions, `delay`, `wait`, and `assert`. They intentionally do not support arbitrary C#, nested MCP calls, loops, or branches.

Only one sequence owns the Editor input stream at a time. Direct `simulate_input` calls return `playmode_job_busy` while a sequence is running. On failure, status includes the step index, observed/expected condition values, elapsed time, and a bounded execution log.

See [`manage_playmode_test`](/reference/tools/testing/manage_playmode_test) and [`simulate_input`](/reference/tools/testing/simulate_input) for complete parameter schemas.
