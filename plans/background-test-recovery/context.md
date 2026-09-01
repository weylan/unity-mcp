# Context: Unity MCP Background Test Recovery

## Evidence

`get_test_job` is annotated read-only but Python launches a focus nudge in both wait branches. The C# getter can convert `AwaitingRunStarted` to failed and persist `SessionState`. Queued jobs only use `delayCall`, reload replays them without a deadline decision, and no lifecycle watchdog subscribes to `EditorApplication.update`.

WebSocket and stdio identity omit process ID; focus implementations choose the first matching Unity process/window. Granting macOS Accessibility therefore cannot make automatic polling safe.

## Decision

Use lifecycle progress by default and make focus an explicit recovery command. V3 adds queued deadline and strict schema handling. WebSocket peer plus PID/path/session form the exact target; stdio is local by construction but still carries PID/path. Remote WebSocket sessions never focus a process on the server host.

The queued budget is independent from test initialization: it is always 60 seconds from the original `CreatedUnixMs`, while `init_timeout` starts only after `Execute` returns. V2 restoration derives the queued deadline from its original creation time; V1 follows a conservative migration. A future schema is not treated as V2/V1: its raw bytes and fence are retained in `RestoreBlocked`, with no overwrite, dispatch or replacement run.

## Plan Review disposition

All first-round findings F1-F11 are accepted. The contract now gives independent exact RED ownership to the C# getter, watchdog scheduler, WebSocket registry and stdio discovery; the nudge test crosses production platform selection and performs a second PID/path validation immediately before focus. Exact RED enumeration is executable from the main repository validator using this repository as both `repo-root` and `tests-root`.

Second-round blockers are also accepted. `StartJob -> PersistToSessionState` now has an independent exact RED that creates a real job, proves `QueuedDeadlineUnixMs = CreatedUnixMs + 60000` under different `init_timeout` values, and reloads the persisted V3 snapshot. The Node wrapper uses one local table for both listing contract IDs and executing the mapped exact pytest node IDs, so renames or collection gaps cannot leave a listing-only false green.

## Verification isolation

The eight C# exact methods cannot be executed as one MCP `run_tests` job: six intentionally call `ResetForTests` or `ClearInMemoryForTests`, which would erase or replace the outer MCP job that is meant to report their result. Their authoritative runtime gate is therefore an independent Unity `2022.3.62f3c1` headless process against a temporary copy of `TestProjects/UnityMCPTests`, restricted to `assemblyNames=MCPForUnityTests.EditMode` and the eight full method IDs joined by a semicolon filter. This does not start, restart, focus or kill the shared Editor.

The 2026-09-02 isolated run reported `Passed total=8 passed=8 failed=0 skipped=0`, with every declared full method ID present once. The result XML was checked before the still-resident temporary Unity PID was terminated; its command line named only `/tmp/unity-mcp-headless-r4.KC2erF/TestProjects/UnityMCPTests`. Python also passed `1438` tests with `2` skips and no failures, and the Node exact wrapper passed all four mapped entries.

## Delivery boundary

Review found that the existing private release flow generates a source-tag update in `MCPForUnity/package.json` and commits it. Revision 4 includes that output path, while `Makefile`, `Tools/gameempire_merge_latest.js` and `Tools/tests/test_gameempire_merge_latest.js` remain read-only context. No production release code changes are needed: after all tests pass and revision 4 receives fresh authorization, only the existing `make release` command may generate/commit the package change.

That boundary completed at immutable tag `gameempire-mcp-v20260902.1`, release commit `92139f8509bd4cd6ec24953d250c2e17c4a3ec94`. The remote branch and both release refs were independently read back at the same OID. The temporary DNS workaround was command-scoped (`http.curloptResolve` plus SSH `HostKeyAlias=github.com`), retained normal TLS/host-key validation, and wrote no system or repository configuration.

## Rejected

- Keep auto-focus and improve TCC prompts: read-only polling would retain an OS side effect.
- Match by project or process name: duplicate projects and multiple Editors are normal.
- Let getter mutate timeout state: polling frequency would remain a lifecycle writer.
