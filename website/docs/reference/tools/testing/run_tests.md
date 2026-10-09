---
title: run_tests
sidebar_label: run_tests
description: "Starts a Unity test run asynchronously and returns a job_id immediately."
---

# `run_tests`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `testing` &nbsp;·&nbsp; **Module:** `services.tools.run_tests`

## Description

Starts a Unity test run asynchronously and returns a job_id immediately. Poll with get_test_job for progress.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `mode` | `Literal['EditMode', 'PlayMode']` | — | Unity test mode to run |
| `test_names` | `list[str] \| str \| None` | — | Full names of specific tests to run |
| `group_names` | `list[str] \| str \| None` | — | Same as test_names, except it allows for Regex |
| `category_names` | `list[str] \| str \| None` | — | NUnit category names to filter by |
| `assembly_names` | `list[str] \| str \| None` | — | Assembly names to filter tests by |
| `include_failed_tests` | `bool` | — | Include details for failed/skipped tests only (default: false) |
| `include_details` | `bool` | — | Include details for all tests (default: false) |
| `editor_lock_token` | `str \| None` | — | Token returned by manage_editor_lock acquire for multi-operation editor locks |
| `init_timeout` | `int \| None` | — | Initialization timeout in milliseconds, measured only after Unity's TestRunner Execute call returns while RunStarted is still pending (default: 15000). Recommended: 120000 for PlayMode. |
| `clear_stuck` | `bool` | — | Logically fail/clear a stuck job instead of starting a run. This does not cancel Unity's physical TestRunner owner. If safe_to_start_new_run is false, wait for its terminal callback or restart Unity. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
### Run every EditMode test

```json
{
  "mode": "EditMode",
  "include_failed_tests": true
}
```

The call returns immediately with a `job_id`. Poll that exact job; results are not included in the start response:

```text
get_test_job(job_id="<returned job_id>", wait_timeout=30)
```

### Run selected tests

`test_names` accepts full test names, while each `group_names` value is a regular expression matched against full names:

```json
{
  "mode": "EditMode",
  "test_names": ["MyGame.Tests.InventoryTests.AddItem_IncreasesCount"],
  "group_names": ["^MyGame\\.Tests\\.Inventory"],
  "include_failed_tests": true
}
```

Filters can also be combined with `category_names` and `assembly_names`.

### Run PlayMode tests

```json
{
  "mode": "PlayMode",
  "assembly_names": ["MyGame.PlayModeTests"],
  "init_timeout": 120000
}
```

The initialization timeout starts only after Unity's `Execute` call returns and while `RunStarted` is still pending. PlayMode startup often needs the recommended 120000 ms.

### Recover a logically stuck job

```text
run_tests(clear_stuck=true)
```

This only marks the logical job failed; it does not cancel Unity's physical TestRunner run. Inspect `safe_to_start_new_run` and `physical_owner_retained` in the response. If `safe_to_start_new_run=false`, keep polling the old `job_id` until its physical terminal callback arrives, or restart Unity before starting another run. An `editor_lock_token`, when supplied, remains attached to that physical owner until cleanup completes.
<!-- examples:end -->
