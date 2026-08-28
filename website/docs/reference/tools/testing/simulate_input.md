---
title: simulate_input
sidebar_label: simulate_input
description: "Inject deterministic input while the Unity Editor is in Play Mode."
---

# `simulate_input`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `testing` &nbsp;·&nbsp; **Module:** `services.tools.simulate_input`

## Description

Inject deterministic input while the Unity Editor is in Play Mode. Use target-based ui_click/ui_drag when possible. Keyboard, mouse, touch, and gamepad injection require the optional Unity Input System backend.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `action` | `Literal['capabilities', 'ui_click', 'ui_drag', 'key', 'mouse', 'touch', 'gamepad', 'release_all']` | yes | Input action to perform. |
| `target` | `str \| int \| None` | — | Target GameObject name, path, or instance ID. |
| `from_target` | `str \| int \| None` | — | Drag source GameObject. |
| `to_target` | `str \| int \| None` | — | Drag destination GameObject. |
| `position` | `list[float] \| None` | — | Normalized [x,y] position with top-left origin. |
| `end_position` | `list[float] \| None` | — | Normalized drag/touch end position. |
| `key` | `str \| None` | — | Input System key name. |
| `phase` | `Literal['tap', 'press', 'release'] \| None` | — | Input phase. |
| `button` | `str \| None` | — | Mouse or gamepad button name. |
| `control` | `str \| None` | — | Gamepad control or axis name. |
| `value` | `float \| None` | — | Axis/control value. |
| `delta` | `list[float] \| None` | — | Mouse delta [dx,dy] in pixels. |
| `hold_frames` | `int \| None` | — | Frames between press and release for tap. |
| `backend` | `Literal['auto', 'event_system', 'input_system'] \| None` | — | Input backend. |
| `editor_lock_token` | `str \| None` | — | Token returned by manage_editor_lock acquire for shared Editor mutation. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
Prefer target-based UI input when the target is known:

```text
simulate_input(action="ui_click", target="Canvas/StartButton", backend="event_system")
```

Input System taps span at least one PlayerLoop frame so gameplay code can observe them:

```text
simulate_input(action="key", key="space", phase="tap", hold_frames=1)
simulate_input(action="gamepad", control="buttonSouth", phase="tap")
simulate_input(action="release_all")
```

Coordinates are normalized `[x, y]` values with a top-left origin, matching screenshots and the `center` values from `mcpforunity://playmode/ui`.
<!-- examples:end -->
