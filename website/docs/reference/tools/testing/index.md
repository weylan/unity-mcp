---
title: "testing tools"
sidebar_label: "testing"
description: "MCP for Unity tools in the testing group."
---

# `testing` tools

Test runner & async test jobs

- **[`get_test_job`](./get_test_job.md)** — Polls an async Unity test job by job_id.
- **[`manage_playmode_test`](./manage_playmode_test.md)** — Run cancellable Play Mode waits and deterministic action sequences. wait/run_sequence return a job_id and are polled through status.
- **[`run_tests`](./run_tests.md)** — Starts a Unity test run asynchronously and returns a job_id immediately.
- **[`simulate_input`](./simulate_input.md)** — Inject deterministic input while the Unity Editor is in Play Mode.
