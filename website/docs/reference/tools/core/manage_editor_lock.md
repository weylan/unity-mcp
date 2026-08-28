---
title: manage_editor_lock
sidebar_label: manage_editor_lock
description: "Manage the shared Unity editor operation lock."
---

# `manage_editor_lock`

> **Auto-generated** from the Python tool registry. Do not hand-edit outside `<!-- examples:start --><!-- examples:end -->` blocks — the generator (`tools/generate_docs_reference.py`) will overwrite them.

**Group:** `core` &nbsp;·&nbsp; **Module:** `services.tools.manage_editor_lock`

## Description

Manage the shared Unity editor operation lock. Use acquire to hold the lock across multiple high-risk editor operations, then pass the returned token as editor_lock_token to those tools and release it when done.

## Parameters

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `action` | `Literal['acquire', 'release', 'extend', 'get_state', 'force_release']` | yes | Lock action: acquire, release, extend, get_state, force_release. |
| `holder_hint` | `str \| None` | — | Short holder label for acquire, e.g. client/session name. |
| `reason` | `str \| None` | — | Reason for acquiring the lock. |
| `token` | `str \| None` | — | Lock token returned by acquire, required for release and extend. |
| `ttl_seconds` | `int \| None` | — | Optional acquire TTL in seconds. Unity uses its default when omitted or non-positive. |
| `additional_seconds` | `int \| None` | — | Optional extension duration in seconds. |

## Returns

A `dict` containing the Unity response. The exact shape depends on the action.

## Examples

<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->
