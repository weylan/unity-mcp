---
title: "testing tools"
sidebar_label: "testing"
description: "MCP for Unity tools in the testing group."
---

# `testing` tools

Test runner & async test jobs

- **[`get_test_job`](./get_test_job.md)** — Observationally polls an async Unity test job without changing focus or lifecycle state.
- **[`manage_playmode_test`](./manage_playmode_test.md)** — Run cancellable Play Mode waits and deterministic action sequences. wait/run_sequence return a job_id and are polled through status.
- **[`nudge_test_job`](./nudge_test_job.md)** — Explicitly nudges one exact local Unity process for an active test job.
- **[`run_tests`](./run_tests.md)** — Starts a Unity test run asynchronously and returns a job_id immediately.
- **[`simulate_input`](./simulate_input.md)** — Inject deterministic input while the Unity Editor is in Play Mode.
