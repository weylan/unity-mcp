---
title: manage_playmode_test
sidebar_label: manage_playmode_test
description: "Run cancellable Play Mode waits and deterministic action sequences. wait/run_sequence return a job_id and are polled through status."
---

# `manage_playmode_test`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `testing` &nbsp;·&nbsp; **Module:** `services.tools.manage_playmode_test`

## Description

Run cancellable Play Mode waits and deterministic action sequences. wait/run_sequence return a job_id and are polled through status. Sequence steps support input, UI actions, delays, waits, and assertions.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `action` | `Literal['wait', 'run_sequence', 'status', 'cancel']` | yes | wait, run_sequence, status, or cancel. |
| `condition` | `dict[str, Any] \| None` | — | Condition for wait. |
| `steps` | `list[dict[str, Any]] \| None` | — | Sequence step list. |
| `timeout_seconds` | `float \| None` | — | Job timeout in unscaled seconds. |
| `stable_for_frames` | `int \| None` | — | Consecutive matching frames required. |
| `job_id` | `str \| None` | — | Job identifier for status/cancel. |
| `editor_lock_token` | `str \| None` | — | Token returned by manage_editor_lock acquire for shared Editor mutation. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
Start a sequence that clicks a button, waits for a stable property value, and asserts it:

```text
manage_playmode_test(
    action="run_sequence",
    timeout_seconds=10,
    steps=[
        {"type": "ui_click", "target": "Canvas/StartButton", "backend": "event_system"},
        {
            "type": "wait",
            "timeout_seconds": 5,
            "stable_for_frames": 2,
            "condition": {
                "type": "component_property",
                "target": "Player",
                "component": "Health",
                "property": "currentHP",
                "operator": "gt",
                "value": 0
            }
        },
        {
            "type": "assert",
            "condition": {"type": "ui_text", "operator": "contains", "value": "Ready"}
        }
    ]
)
```

The tool returns a `job_id`. Poll it with `action="status"`; cancel with `action="cancel"`.
<!-- examples:end -->
