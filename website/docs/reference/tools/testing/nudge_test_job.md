---
title: nudge_test_job
sidebar_label: nudge_test_job
description: "Explicitly nudges one exact local Unity process for an active test job."
---

# `nudge_test_job`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `testing` &nbsp;·&nbsp; **Module:** `services.tools.run_tests`

## Description

Explicitly and temporarily focuses the exact local Unity process selected for an
active test job, then restores the original application. The command fails closed
unless the current user, session, PID, canonical project root, and loopback peer
all agree. `get_test_job` never invokes this command automatically.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `job_id` | `str` | yes | Active job id returned by `run_tests` |
| `focus_duration` | `float` | — | Seconds to keep Unity focused before restoring the original app (0.1–30; default: 1) |

## Returns

A `dict` reporting the exact session, PID, canonical project root, and whether the
focus attempt was performed or safely skipped.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->
