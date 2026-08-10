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
| `editor_lock_token` | `str \| None` | — | Token returned by `manage_editor_lock acquire`; it is preserved through dirty preflight refresh and transferred to the physical test job. |
| `init_timeout` | `int \| None` | — | Initialization timeout in milliseconds, measured only after Unity TestRunner `Execute` returns while `RunStarted` is still pending (default: 15000). Recommended: 120000 for PlayMode. |
| `clear_stuck` | `bool` | — | Logically fail/clear a stuck job instead of starting a run. This does not cancel Unity's physical TestRunner owner. |

## Returns

A normal start returns `job_id` with `status: "queued"`; poll `get_test_job` for the current `phase`, progress, and result. A logical timeout or `clear_stuck` response also reports `safe_to_start_new_run`, `physical_owner_retained`, and `restart_required_if_orphaned`. `finished_unix_ms` records the immutable logical completion time, while `physical_finished_unix_ms` is populated only after matching physical terminal evidence arrives.

When `safe_to_start_new_run` is `false`, the physical TestRunner fence is still active even if the logical status is `failed`. Wait for the matching terminal callback. If the owner is truly orphaned and no terminal callback can arrive, restart Unity; neither `clear_stuck` nor `manage_editor_lock force_release` pretends to cancel or unlock that physical run.

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
