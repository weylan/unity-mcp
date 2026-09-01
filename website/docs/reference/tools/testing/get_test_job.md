---
title: get_test_job
sidebar_label: get_test_job
description: "Observationally polls an async Unity test job without changing focus or lifecycle state."
---

# `get_test_job`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `testing` &nbsp;·&nbsp; **Module:** `services.tools.run_tests`

## Description

Observationally polls an async Unity test job by `job_id`. Reads never focus Unity,
advance a timeout, or write lifecycle state. If an operator explicitly wants to
temporarily foreground one exact local Editor, use [`nudge_test_job`](./nudge_test_job.md).

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `job_id` | `str` | yes | Job id returned by run_tests |
| `include_failed_tests` | `bool` | — | Include details for failed/skipped tests only (default: false) |
| `include_details` | `bool` | — | Include details for all tests (default: false) |
| `wait_timeout` | `int \| None` | — | If set, wait up to this many seconds for tests to complete before returning. Reduces polling frequency and avoids client-side loop detection. Recommended: 30-60 seconds. Returns immediately if tests complete sooner. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->
