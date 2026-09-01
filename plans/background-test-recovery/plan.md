# Unity MCP Background Test Recovery Plan

> 创建: 2026-09-01 | 状态: done | 作者: codex
> Machine contract: `./plan.contract.json`

## Goal

Make async TestRunner polling observational and background-safe. An Editor lifecycle watchdog advances queued and initialization timeouts without requiring application focus; explicit `nudge_test_job` remains available only for a unique local PID/path/session target.

## Read First

1. `CLAUDE.md`
2. `plans/test-runner-job-lifecycle/plan.md`
3. `MCPForUnity/Editor/Services/TestJobManager.cs`
4. `Server/src/services/tools/run_tests.py`
5. `Server/src/utils/focus_nudge.py`

## Constraints

- `get_test_job` is a pure projection in Python and C#; it never nudges focus or persists timeout state.
- Editor `update` watchdog owns timeout transitions and queued cleanup; critical transitions are persist-first with rollback.
- Queued timeout has its own fixed 60-second budget: `QueuedDeadlineUnixMs = CreatedUnixMs + 60000`; `init_timeout` starts only after `Execute` returns.
- Restore V3 accepts exact V3, derives V2 queued deadlines from the original creation time, migrates V1 conservatively, and preserves unknown future-schema bytes/fence in a blocked state without replay or overwrite.
- `nudge_test_job` is explicit, non-destructive but mutating, non-idempotent, and aliases visibility to `get_test_job`.
- Focus requires exact user/session/PID/absolute project root/local peer; missing, remote, mismatched or ambiguous targets fail closed.
- Immediately before the OS call, nudge rechecks the PID command line and canonical project root; platform mocks cover macOS, Windows and Linux while real AXRaise remains a separately authorized environment check.
- macOS, Windows and Linux target the supplied PID, never the first Unity process/window.
- No Unity restart/kill and no real shared-Editor run without separate authorization.
- `MCPForUnity/package.json` is a delivery artifact: do not hand-edit it during implementation. After every test gate passes and revision 4 is re-authorized, only the existing `make release` flow may generate and commit its immutable-tag source update.

## Existing Plan Relationship

No non-done plan overlaps the declared files. This succeeds the completed `test-runner-job-lifecycle` plan without rewriting it.

## Steps

1. Add every exact RED named by `plan.contract.json`; the Node wrapper owns one local mapping table that both emits `--list-tests-json` and executes those same concrete pytest node IDs.
2. Make `TestJobManager.GetJob` observational; add watchdog, V3 migration, queued deadline and PlayerLoop wakeup.
3. Move nudge behavior to explicit `nudge_test_job`; propagate PID/project-root/peer through WebSocket and stdio.
4. Make `focus_nudge.py` validate PID plus canonical project root and target only that process on all supported OSes.
5. Update annotations and tool docs; leave plan status for the root workflow to close after isolated Unity verification.
6. After all implementation tests pass and revision 4 is re-authorized, let the existing `make release` flow generate/commit `MCPForUnity/package.json`; do not change `Makefile`, the release publisher or its tests.

## Verification

- `node /Users/weylan/Projects/unity/Tools/plan_contract.js validate /Users/weylan/Projects/unity-mcp/plans/background-test-recovery/plan.contract.json --profile red --repo-root /Users/weylan/Projects/unity-mcp --tests-root /Users/weylan/Projects/unity-mcp --json`
- `node Server/tests/test_background_test_recovery_contract.js`（正常模式必须从与 listing 相同的本地映射表逐个执行 exact pytest node ID；任一漏收集/改名即失败）
- `cd Server && uv run --frozen pytest tests/integration/test_run_tests_async.py tests/test_focus_nudge.py tests/test_tool_annotations.py tests/test_transport_characterization.py tests/integration/test_plugin_hub_websocket_auth.py tests/test_models_characterization.py -q`
- Do not run the eight C# exact tests through MCP `run_tests`: six call `ResetForTests`/`ClearInMemoryForTests` and would reset the outer MCP job. Copy `TestProjects/UnityMCPTests` to a temporary directory and run an independent Unity `2022.3.62f3c1` headless process with `-batchmode -nographics -testPlatform EditMode -assemblyNames MCPForUnityTests.EditMode` and this semicolon filter: `MCPForUnityTests.Editor.Services.TestJobManagerLifecycleTests.StartJob_PersistsQueuedDeadlineFromCreatedTimeIndependentOfInitTimeoutAndReloadsV3;MCPForUnityTests.Editor.Services.TestJobManagerInitTimeoutTests.GetTestJobHandler_AfterDeadline_IsReadOnlyUntilWatchdogTicks;MCPForUnityTests.Editor.Services.TestJobManagerInitTimeoutTests.LifecycleWatchdog_AwaitingTimeout_PersistFirstRollbackAndRetainsPhysicalOwner;MCPForUnityTests.Editor.Services.TestJobManagerLifecycleTests.QueuedWatchdog_ExpiredOwnerlessJob_PersistFirstRollbackAndLateDispatchNoOp;MCPForUnityTests.Editor.Services.TestJobManagerLifecycleTests.LifecycleWatchdog_SubscriptionAndPlayerLoopWakeup_RemainSingleAndConditional;MCPForUnityTests.Editor.Services.TestJobManagerLifecycleTests.RestoreLifecycle_V3V2V1AndFutureSchema_AreStrictAndNeverReplayUnsafeWork;MCPForUnityTests.Editor.Services.WebSocketTransportClientTests.BuildRegisterPayload_IncludesExactProjectRootAndCurrentProcessId;MCPForUnityTests.Editor.Services.StdioBridgeHostHeartbeatTests.BuildHeartbeatPayload_IncludesAssetsPathAndCurrentProcessId`. Unity 2022.3 may keep the batch process resident after writing XML when `-quit` is omitted; verify the XML first, then terminate only the PID whose exact `-projectPath` names the temporary copy.
- The isolated headless result must report all eight exact methods collected and passed before any `make release`; the shared foreground Unity Editor is never used for this gate.
- Re-run the same absolute validator with `--profile final` after implementation; missing exact IDs, duplicate ownership or zero matches must fail.

## Verified implementation state

On 2026-09-02 the RED contract matched all `12/12` exact owners, the Node wrapper executed all `4/4` mapped pytest nodes, the related Python files passed `196/196`, and the full Python suite passed `1438` with `2` skips and no failures. The isolated Unity `2022.3.62f3c1` run compiled the changed C# and reported exactly `8/8` passed. The shared Unity Editor was not used for those gates.

The post-implementation three-lane review was applied only where it affected the normal background-test path: V3 restore now rejects contradictory status/phase/current/owner snapshots while retaining a matching physical owner for a logical terminal job; remote-hosted nudge fails before Unity or OS lookup; and nudge execution/restore failures are returned as failures. General upgrade rollback, journal carry-forward expansion, watchdog throttling and PID-reuse hardening remain outside this fix.

The existing release flow generated commit `92139f8509bd4cd6ec24953d250c2e17c4a3ec94` and published immutable tag `gameempire-mcp-v20260902.1`. Remote `beta`, that immutable tag and `gameempire-mcp-latest` were read back at the same OID; the generated Server source names the same immutable tag.

## Related

- [todo.md](./todo.md)
- [context.md](./context.md)
