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
Start a filtered run, then poll the returned job:

```text
run_tests(mode="EditMode", test_names=["My.Namespace.Tests.Case"])
get_test_job(job_id="<returned job_id>", wait_timeout=30)
```

Recovery is a logical state operation only:

```text
run_tests(clear_stuck=true)
```

If its response contains `safe_to_start_new_run=false`, do not start another test run until the physical owner terminates or Unity has been restarted.
<!-- examples:end -->
