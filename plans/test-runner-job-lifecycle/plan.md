# Unity MCP TestRunner 生命周期与共享锁修复计划

> 状态: done

状态说明：实现、非 Unity 回归与真实 Unity EditMode 定向验证均已完成；真实 Unity 结果为 59/59 通过。交付授权以本会话用户最新明确指令和 workflow ledger 的 `delivery:auto` 为准；本文件不把早期 Plan Review receipt 视为当前 SHA 的复核结论。

## 目标

修复 `run_tests` 在真正收到 `RunStarted` 前被错误判定初始化超时的问题，并把逻辑 job、物理 TestRunner callback owner、共享 Editor 自动锁和域重载恢复统一成一个可持久、可验证的生命周期。保持异步 API 的承诺：先返回 `job_id`，再在下一次 Unity 主线程调度中启动 TestRunner。

同时适配上游新增的 `clear_stuck` 参数：它必须能绕过普通测试启动的过滤器 guard、preflight 和命令级锁争用，但没有物理结束证据时不得宣称取消了真实 TestRunner，也不得释放物理 owner/fence 后允许新测试重叠。

## 已核实的兼容矩阵

| 上游变化 | 本地锁适配 | 本地可见性/自动批准适配 | 裁决 |
| --- | --- | --- | --- |
| `run_tests.clear_stuck` | 必须参数感知识别；恢复调用不走普通启动 guard/auto-lock；显式 token 如提供仍需透传供审计 | 仍是 `run_tests` 同一 tool，保持 destructive、非自动批准 | 纳入修复 |
| `run_tests` 常规启动 | `preflight` 的嵌套 `refresh_unity` 必须继承显式 token，且该显式 command lock 可跨 preflight domain reload 恢复；dispatcher/batch token 必须转交 job 生命周期；物理结束前不能释放或复用 job-attached lock；batch 中测试启动必须是最后一项 | tool 名与 testing group 不变 | 纳入修复 |
| `manage_gameobject.components_to_add` 支持对象载荷 | tool/action/risk tier 未改变；仍按现有非长耗时命令策略，不新增自动锁 | 仍是同一 destructive tool，无新增 tool 名 | 无源码适配，只跑现有回归 |
| ToolAnnotations 补全 | 与本地 `group:*`、`tool:*` tags 合并，不覆盖逐工具可见性 | 精确自动批准集合由 `test_tool_annotations.py` 固定 | 无源码适配，只跑现有回归 |
| `execute_code` CodeDom 内部改动 | 没有新增 tool 或公开参数；既有是否升级为高风险锁属于独立策略问题 | 仍是 destructive 且属于 `scripting_ext` | 本 revision 不扩 scope |

本地 fork 合并前基线 `9d8751f18315e26d8cd97a4ab54d844d43b85fad` 与合并后 `HEAD` 的 C# tool 名集合精确相同（36 → 36，added/removed 均为空）；此前的 35 → 35 漏算了本地 `manage_editor_lock`，现已更正。`C:\work\unity\.mcp-for-unity.json` 是逐工具 override map，不是严格 allowlist；未列工具回退到 EditorPrefs/元数据默认值。本任务不改变该既有语义，并用生产 `DiscoverAllTools` characterization 固定这三个事实。

## 根因与不变量

1. `StartedUnixMs` 是 job 创建时间，不是初始化等待起点。只有 `_testRunnerApi.Execute(settings)` 成功返回并且尚未收到 `RunStarted` 后，才写 `AwaitingRunStartedSinceUnixMs` 并开始 `init_timeout`。
2. `RunStarted(null)` 也证明运行已经开始；不能再用 `TotalTests != null` 充当启动标志。
3. 任一时刻最多一个持久的物理 owner（`job_id + generation`）。所有回调、task completion、timeout、clear 和 restore 都必须携带/校验该 identity，不能读取“当前最新 job”来猜归属。
4. owner claim 与 phase 关键转换必须先 force-persist 成功，之后才允许执行 Unity TestRunner 副作用；匹配 `RunFinished` 的 terminal durable write 也必须先成功，之后才允许改内存 owner/status、释放锁或调度后继。任一关键持久化失败均 fail closed。
5. 自动锁可从 dispatcher 或 batch 命令作用域原子附着到 job；附着后命令 cleanup 不释放，匹配物理终态才释放。显式锁在未附着时以到期时间持久化，可跨 dirty preflight 的 domain reload 恢复；附着 job 后普通 dispatcher/batch 的 `Reenter` 与提前 `release` 都 fail closed，只有 `clear_stuck` 等该 job 生命周期控制路径可核验使用。恢复优先级为“持久 physical owner + attached lock”高于“未附着且未过期的显式 command lock”；不持久化普通未附着 auto lock。
6. 逻辑 timeout/`clear_stuck` 可以把 job 标记 failed，但只要物理 owner 未收到匹配终态，phase/fence、`TestRunStatus` 和 job lock 都保留；响应必须明确 `safe_to_start_new_run=false`，必要时提示重启 Unity，而不是伪称已取消真实运行。
7. 域重载恢复持久 owner 时，必须在任何 readiness/guard consumer 读取前 eager 恢复 callback service、`TestRunStatus` busy 和 job lock；`Queued` 可安全重调度，`Dispatching/AwaitingRunStarted/Running` 禁止盲目重复 Execute。V1 current+running 迁移为带新 generation 的保守物理 owner，保持 no-anchor、no-reexecute。
8. `batch_execute` 中普通 `run_tests` 启动必须是最后一个可执行项；预扫描即拒绝其后的命令。成功创建 job 后外层 batch token 只归 job，batch 不得再执行或复用它；`clear_stuck` 不属于测试启动，仍按恢复动作处理。

## 首轮 Plan Review finding 处置

- F1 `accept`：attached explicit token 只供匹配 job 生命周期使用，普通命令 `Reenter`/`release` 均 fail closed，并补独占 RED。
- F2 `accept`：只持久化未过期显式 command lock，使 dirty preflight domain reload 后可继续沿用；unattached auto lock 不持久化。
- F3 `accept`：普通 `run_tests` 启动必须是 batch 最后一项；dispatcher 与 batch 拆成两个写入口和两个独占 RED。
- F4 `accept`：terminal durable write 是改内存 owner/status、释放锁、调度后继的前置 barrier，并补持久化失败注入 RED。
- F5 `accept`：把现有 `TestJobManager` 作为 eager `[InitializeOnLoad]` bootstrap；首个 readiness/guard consumer 必须先确保 V2/V1 owner、callback、status 与 lock 恢复。
- F6 `accept`：基线更正为本地 fork 的 36 → 36，补生产发现名称集、未列工具 fallback 与 `manage_gameobject` risk-tier characterization。
- F7 `accept`（P2）：attached physical owner 存在时拒绝 `force_release`，并同步 `clear_stuck`/`force_release` 相邻说明，诚实报告逻辑失败不等于物理取消。

旧 F1（Execute-return 通知）与旧 F4（`clear_stuck` 精确语义）保持关闭；旧 F2 的 terminal failure 方向由本 revision 补齐；旧 F3 的 eager reload 顺序与 V1 路径由本 revision 补齐。

## 最终复评 P2 收口（revision 5）

- F1 `accept`（P2）：把 `Website/docs/reference/tools/testing/run_tests.md` 与 `Server/src/cli/commands/editor.py` 纳入同批同步，明确逻辑 timeout/`clear_stuck` 不会解除物理 TestRunner fence；当 `safe_to_start_new_run=false` 时只能等待匹配终态或重启 Unity。此项不改变状态机、不变量、写入口或 RED 设计。

## 状态机

- `Queued`：job 已持久化并已返回给客户端，尚无物理 owner；域重载后可以重新登记一次主线程调度。
- `Dispatching`：owner 已原子 claim 且 force-persist，可能等待 service semaphore 或正在同步执行 `Execute`；初始化时钟为空，域重载后禁止重放。
- `AwaitingRunStarted`：`Execute` 已成功返回但尚未收到 `RunStarted`；此时才使用独立锚点计算 `init_timeout`。
- `Running`：收到匹配 owner 的 `RunStarted`，即使 total unknown 也进入该相位。
- `Terminal`：匹配的 `RunFinished`、明确 dispatch fault，或无物理 owner 的 queued cancel 已持久化。只有这里才允许释放/拆除 owner 和 job lock。

`TestJobStatus` 表示面向调用方的逻辑结果，与物理 phase 正交：timeout/clear 后 status 可为 `Failed`，而 phase 仍保持非终态，直到真实物理终态到达。

## 实现范围

### Unity C#

- `TestJobManager`：V2 持久结构、phase/anchor/generation/physical owner、关键持久化返回值、可控时钟与调度 seam、identity-aware callback、terminal durable-write barrier、reload busy/lock/callback 恢复、安全 clear/timeout；作为现有类增加 eager `[InitializeOnLoad]` bootstrap，并提供幂等 `EnsureInitialized` 供首个外部 consumer 调用。
- `TestRunnerService` + 新内部 job-bound interface：semaphore 获取后绑定 owner；`Execute` 返回后显式通知；所有回调携带捕获的 identity；公开 `ITestRunnerService` 兼容入口保留。
- `TestRunStatus`：提供基于持久 owner 的 reload rehydrate，只有匹配 owner 终态才清除。
- `TestRunStatus` / `EditorStateCache` / `PlayModeOptionsGuard`：首个外部状态读取先调用幂等 bootstrap，确保 persisted owner、callback service 与锁已恢复，再生成 readiness 或决定是否恢复 PlayMode options。
- `SharedEditorOperationLock`：参数感知 lock policy；只持久化未过期显式 command lock 与 attached job lock；command lock attach/detach job；attached token 拒绝普通重入/提前释放且不按命令完成或 TTL 过期；异常路径释放未附着的 command lock。
- `TransportCommandDispatcher`：把有效 auto/explicit token 传给直接 `run_tests`；cleanup 只释放仍属 command 的锁；同步抛异常也必须清理；attached token 的普通命令重入 fail closed。
- `BatchExecute`：预扫描要求普通 `run_tests` 启动为最后一项，把 outer token 转交 job 后立即结束 batch；不得执行测试启动后的子命令，也不得继续复用 attached token。
- `SharedEditorCommandGuard` / `RunTests`：`clear_stuck` 独立恢复语义；普通启动仍要求显式测试过滤器。
- `ManageEditorLock`：attached physical owner 存在时拒绝 `force_release`，响应明确说明 owner/fence 仍保留；不把人工逃生口伪装成安全取消。
- `ToolDiscoveryService` 生产发现路径保持不变；仅添加 36-name、未列工具 fallback 与 `manage_gameobject` risk-tier characterization。

### Python Server

- `preflight` 接受可选 `editor_lock_token`，dirty refresh 时透传。
- `run_tests` 的常规 preflight 与 `clear_stuck` payload 均保留显式 token；无 token 时保持现有调用兼容。

### 明确不做

- 不把项目逐工具 override map 改成 deny-by-default 严格白名单。
- 不改变上游精确 ToolAnnotations 自动批准集合。
- 不给 `manage_gameobject`、`manage_components` 等所有普通写工具做一次全局锁策略重分类。
- 不把 `execute_code` 的既有锁策略缺口塞进本次 TestRunner 修复。
- 不尝试通过 `clear_stuck` 或 timeout 伪造 Unity TestRunner 已被取消。

## RED 测试

1. `MCPForUnityTests.Editor.Tools.RunTestsTests.HandleCommand_ReturnsQueuedJobBeforeRunnerInvocationAndAttachesAutoLock`
2. `MCPForUnityTests.Editor.Services.TestJobManagerLifecycleTests.Dispatch_ForcePersistsOwnerBeforeRunnerAndPersistenceFailureDoesNotExecute`
3. `MCPForUnityTests.Editor.Services.TestRunnerServiceJobBindingTests.OperationLockContended_DoesNotEnterAwaitingUntilExecuteActuallyReturns`
4. `MCPForUnityTests.Editor.Services.TestJobManagerLifecycleTests.InitializationTimeout_UsesAwaitingAnchorAndUnknownTotalTransitionsToRunning`
5. `MCPForUnityTests.Editor.Services.TestJobManagerLifecycleTests.LateCallbacks_MutateOnlyMatchingPhysicalOwnerAndFinalizeExactlyOnce`
6. `MCPForUnityTests.Editor.Services.TestJobManagerLifecycleTests.RunFinished_WhenCriticalPersistFails_PreservesOwnerStatusAndLockAndDoesNotDispatchNext`
7. `MCPForUnityTests.Editor.Services.TestJobManagerLifecycleTests.TimeoutAndClear_PreservePhysicalOwnerLockUntilMatchingRunFinished`
8. `MCPForUnityTests.Editor.Services.TestJobManagerLifecycleTests.FirstReadinessConsumerAfterReload_RehydratesOwnerLockStatusAndCallbacksBeforeObservation`
9. `MCPForUnityTests.Editor.Services.TestJobManagerLifecycleTests.LegacyV1CurrentRunning_RestoresOwnerWithoutAnchorOrExecuteReplay`
10. `MCPForUnityTests.Editor.Services.SharedEditorOperationLockTests.AttachedAutoAndExplicitLocks_ReleaseOnlyAtMatchingJobTerminal`
11. `MCPForUnityTests.Editor.Services.SharedEditorOperationLockTests.AttachedExplicitToken_CannotReenterAnotherCommandUntilPhysicalTerminal`
12. `MCPForUnityTests.Editor.Services.SharedEditorOperationLockTests.ExplicitLock_SurvivesPreflightDomainReload_AndAttachesToJob`
13. `MCPForUnityTests.Editor.Services.TransportCommandDispatcherTests.DirectRunTests_TransfersEffectiveLockAndRegistryExceptionDoesNotLeak`
14. `MCPForUnityTests.Editor.Tools.BatchExecuteTests.RunTestsStart_MustBeLastAndTransfersOuterLockWithoutExecutingLaterCommands`
15. `MCPForUnityTests.Editor.Services.SharedEditorCommandGuardTests.ClearStuck_BypassesStartFilterGuardButNormalRunStillRequiresFilter`
16. `MCPForUnityTests.Editor.Tools.ManageEditorLockTests.ForceRelease_WithAttachedPhysicalOwner_IsRejectedAndReportsFence`
17. `MCPForUnity.Editor.Tests.EditMode.Services.ToolDiscoveryServiceTests.DiscoverAllTools_MatchesForkBaselineAt9d8751_Exactly36Names`
18. `MCPForUnity.Editor.Tests.EditMode.Services.ToolDiscoveryServiceTests.UnlistedBuiltInTool_UsesEditorPrefsThenMetadataFallback`
19. `MCPForUnityTests.Editor.Services.SharedEditorOperationLockTests.ManageGameObject_RemainsOutsideHighRiskClassifier`
20. Python：更新 `test_run_tests_async.py` 与 `test_editor_lock_token_forwarding.py`，覆盖 clear/preflight token 透传、显式 token 跨 refresh 后沿用和无 token 兼容。
21. 白名单回归：运行并按需增强 `Server/tests/test_tool_annotations.py`、`Server/tests/test_per_tool_visibility.py`，固定 `run_tests`/`manage_gameobject`/`execute_code` 仍 gated/destructive，以及 `group:*` 与 `tool:*` tag 合并不丢失。

测试全部使用 fake runner、可控 Task gate、可控时钟/调度和 SessionState seam；不嵌套启动真实 TestRunner，不用 `Thread.Sleep` 证明时序。

## 验证顺序

1. 定向 Server pytest（run_tests、lock token、annotations、visibility）。
2. `make test-server`、`make test-tools`、`make check`。
3. Unity EditMode 定向回归与全量 EditMode。受共享 Editor R6 约束：执行任何真实 Unity 测试前另行征得用户确认；连续两次 MCP 超时立即停止。
4. `/simplify` 三路代码复核。
5. 同步 `run_tests.clear_stuck` 与 `manage_editor_lock.force_release` 的相邻参数/响应文档，包括 Website `run_tests` reference 与 CLI `--clear-stuck` help，明确逻辑失败不等于物理取消。
6. 不 push、不打 tag、不 release，除非用户后续明确授权。
