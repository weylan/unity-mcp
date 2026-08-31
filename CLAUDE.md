# CLAUDE.md / AGENTS.md

本文件是 Codex/Claude/Cursor 等 AI coding agent 在本仓库工作的约束。优先级高于通用习惯，但低于用户本轮明确指令。`AGENTS.md` 应作为指向 `CLAUDE.md` 的软链接保留，避免两份规则文件内容漂移。

## 回复与协作

- 所有回复、计划、阶段性说明都使用中文。
- 用户熟悉 Go/C++/Lua，不熟悉 C#/Unity 和 TS/JS/Java。涉及 C#、Unity Editor、Python MCP、Node 脚本时，要说明关键假设和验证方式，不要假设用户知道 Unity 工程细节。
- 一次性输出很长的计划或文件内容时分段输出，避免网络超时。
- 使用 Codex 执行长 Plan/Task 时，把完整 Task 写入临时文件，让 Codex 读取文件执行，避免长文本被截断或误读。
- 不恢复、覆盖或回滚用户已有改动，除非用户明确要求。

## 当前 fork 目标

这是 `weylan/unity-mcp` 的私有开发 fork，从上游主干 fork 后用于单独增加和维护私有功能。当前唯一开发分支是 `beta`。日常开发优先满足私有 Unity MCP 使用需求，同时保持能够从上游 `CoplayDev/unity-mcp` 的 `beta` 分支合并。

本 fork 采用单分支开发规则：

- 本地和 `origin` 远端只保留并使用 `beta` 一个开发分支。
- 所有开发、测试、修复、私有发布、Codex/Claude/Cursor agent 工作都必须直接在 `beta` 上完成。
- 不创建 feature、bugfix、hotfix、codex、临时测试等任何其他本地或远端私有开发分支。
- 不切换到 `main`、`gameempire/protect`、`upstream/*` 或其他分支做开发；这些上游引用只用于同步/合并来源，不作为工作分支。
- 开始任何代码修改前必须确认当前分支是 `beta`；如果不在该分支，先切回 `beta`，不要在错误分支继续工作。
- 如果发现多余本地分支或 `origin` 上的私有临时分支，应删除它们，避免后续开发落到错误分支。
- `origin/beta` 是本 fork 的唯一远端开发分支；不要在 `origin` 保留 `gameempire/protect` 或其他私有开发分支。

私有发布链路使用：

- 目标分支：`beta`
- 上游分支：`upstream/beta`
- 私有远端：`origin`
- 不可变标签：`gameempire-mcp-vYYYYMMDD.N`
- latest 标签：`gameempire-mcp-latest`
- Unity 包内 Server 源：`MCPForUnity/package.json` 的 `mcpServerPackageSource`，必须指向 `git+https://github.com/weylan/unity-mcp.git@<tag>#subdirectory=Server`

## 项目结构

`MCP for Unity` 是 AI assistant 通过 MCP 控制 Unity Editor 的桥接系统：

```text
AI Assistant
  -> MCP 协议 (stdio/HTTP)
  -> Python Server (Server/src/)
  -> WebSocket + HTTP
  -> Unity Editor Plugin (MCPForUnity/)
  -> Unity Editor API
```

两个主要代码库：

- `Server/`：Python MCP server，使用 FastMCP、FastAPI、Click。
- `MCPForUnity/`：Unity C# Editor package。

Python 侧有三层，不是互相自动生成：

| 层 | 位置 | 框架 | 用途 |
| --- | --- | --- | --- |
| MCP Tools | `Server/src/services/tools/` | FastMCP `@mcp_for_unity_tool` | 暴露给 AI assistant |
| CLI Commands | `Server/src/cli/commands/` | Click `@click.command` | 给开发者命令行使用 |
| Resources | `Server/src/services/resources/` | FastMCP `@mcp_for_unity_resource` | 只读状态 |

MCP tools 经 WebSocket 调 Unity：`send_with_unity_instance`。CLI 经 HTTP 调 Unity：`run_command`。两者最终走 C# 侧同名 `HandleCommand`。

传输模式：

- Stdio：单 agent 模式。每个客户端独立启动 Python 进程，经旧 TCP bridge 连接 Unity；新连接会顶掉旧连接。
- HTTP：多 agent 友好模式。一个共享 Python server，通过 `/hub/plugin` WebSocket hub 连接 Unity；用 `client_id` 做会话隔离。

## 开发原则

- Python MCP tool、Python CLI command、C# Editor tool 按领域保持对称，但不要强行抽象成一套生成逻辑。
- 工具保持小而专注。不要为了方便给已有 tool 增加大量参数。
- 读状态优先用 Resource；会修改 Unity 状态的能力才做 Tool。
- 参数化 FastMCP Resource URI 必须使用 RFC6570 query template（例如 `mcpforunity://playmode/state{?include_ui,ui_limit,player}`），不要拼成普通 query 占位串。
- 新增或修改 Resource 时，必须用真实 `register_all_resources` 注册到 FastMCP，并通过 `list_resource_templates()` 精确断言最终 URI template；只测装饰器或直接调用函数不能证明资源已被框架接受。Play Mode 资源的现成入口是 `Server/tests/test_playmode_testing.py::test_playmode_resources_register_with_fastmcp`。
- 可能返回大量数据的接口必须分页，使用 `page_size` 和 `cursor`，有更多数据时返回 `next_cursor`。
- 新功能和非紧急 bugfix 使用 TDD：先加能失败的测试，再实现，再跑目标测试。
- 完成 Feature/Bugfix/Refactor 后执行 `/simplify` 评审；Hotfix 或流程优化可跳过 TDD，但仍要做对应验证。
- 新增 Makefile 目标或脚本必须兼容 macOS/Linux/Windows。优先使用 Node 标准库实现复杂逻辑，Makefile 只做薄转发。
- 删除功能时直接删除，不保留 `_unused`、废弃注释或内部兼容壳。

## 常用命令

所有私有开发入口优先走 Makefile：

```bash
make status
make check
make test-tools
make test-tools-full
make test-server
make test-server PYTEST_K=camera
make test-unity
make test-unity MODE=PlayMode
make preflight
```

Unity 多版本兼容检查：

```bash
tools/check-unity-versions.sh
tools/check-unity-versions.sh --full
```

本地 headless Unity harness（需要本机有 Hub 激活的 Unity Editor）：

```bash
python tools/local_harness.py
python tools/local_harness.py --legs smoke,editmode,playmode
python tools/local_harness.py --reuse
```

`tools/local_harness.py` 退出码约定：`0` 通过，`1` 阻塞性测试回归，`2` bridge 不可达或环境搭建失败，`3` Unity 项目无法编译，`4` 无 Unity license 或 Hub seat，`5` 找不到 Editor 二进制或版本。

私有发布/合并：

```bash
make merge
make merge NO_PUSH=1
make merge DRY_RUN=1
make release
make private-source TAG=gameempire-mcp-vYYYYMMDD.N
```

命令含义：

- `make status`：显示 Unity 包、Python server、MCP manifest 的版本，以及当前私有 Server 源。
- `make check`：检查版本一致性和 `mcpServerPackageSource` 是否指向私有 fork 标签。
- `make test-tools`：运行快速 Node 私有发布/开发工具测试。
- `make test-tools-full`：运行 Node 工具测试和 Python 工具 characterization 测试。
- `make test-server`：运行 `Server/tests/` pytest。
- `make test-unity`：通过 Server CLI 运行 Unity Test Framework 测试，需要 Unity 已运行且 MCP bridge 连接。
- `make preflight`：私有发布前检查，运行 `check`、工具测试、Server pytest。
- `make merge`：拉上游 beta，合并到本 fork 的 `beta`，更新私有 tag/latest tag。
- `make release`：不合并上游，只对当前 `beta` 做私有发布标签。

底层脚本：

- `tools/gameempire_dev.js`：私有开发状态、检查、测试入口。
- `tools/gameempire_merge_latest.js`：私有合并和发布标签流程。

## Python MCP 工具模式

工具放在 `Server/src/services/tools/`，用 `@mcp_for_unity_tool` 自动注册：

```python
from services.registry import mcp_for_unity_tool

@mcp_for_unity_tool(
    description="Does something in Unity.",
    group="core",
)
async def manage_something(ctx: Context, action: Literal["create", "delete"]) -> dict[str, Any]:
    unity_instance = await get_unity_instance_from_context(ctx)
    params = {"action": action}
    return await send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "manage_something",
        params,
    )
```

`group="core"` 默认启用。非 core 分组如 `vfx`、`animation`、`ui`、`scripting_ext`、`testing`、`probuilder`、`profiling`、`docs` 默认可被 `manage_tools` 控制可见性。

CLI command 不使用 MCP decorator，使用 Click，并用 `@handle_unity_errors` 处理 Unity 调用错误。

## C# Editor 工具模式

C# 工具通过 `[McpForUnityTool]` 和反射注册：

```csharp
[McpForUnityTool("manage_something", AutoRegister = false, Group = "core")]
public static class ManageSomething
{
    public static object HandleCommand(JObject @params)
    {
        var p = new ToolParams(@params);
        var name = p.RequireString("name");
        return new SuccessResponse("Done.", new { name });
    }
}
```

参数读取统一用 `ToolParams`：

```csharp
var p = new ToolParams(parameters);
var pageSize = p.GetInt("page_size", "pageSize") ?? 50;
var name = p.RequireString("name");
```

长耗时 C# handler 可以返回 `Task<object>`。需要轮询 Unity Editor 状态时，用 `EditorApplication.update` + `TaskCompletionSource`，参考 `RefreshUnity.cs`。

Unity API 兼容性要集中在 `MCPForUnity/Runtime/Helpers/Unity*Compat.cs`。不要在调用点到处散落 `#if UNITY_x_y_OR_NEWER`。兼容策略源头是 `MCPForUnity/Runtime/Helpers/UnityCompatShims.cs` 的 XML 文档。

## 测试要求

- Python 改动：至少运行相关 pytest；大范围改动运行 `make test-server`。
- 工具脚本/Makefile 改动：默认运行 `make test-tools`，并至少跑 `make status` 或 `make check` 验证入口；如果改到 Python 工具链、发布 characterization 或 `uv` 流程，再运行 `make test-tools-full`。
- Unity C# 改动：优先补 `TestProjects/UnityMCPTests/Assets/Tests/` 下测试；能连 Unity 时运行 `make test-unity` 或指定 `MODE=PlayMode`。
- 发布前：运行 `make preflight`。如果因为当前仓库版本不一致或 Unity 未连接导致失败，要在最终回复里明确失败原因。
- 如果是 Go 文件改动，完成后修复所有 `_test` 测试文件中的错误并运行对应 Go 测试。

## 新增工具清单

添加新 Unity 能力时通常需要同步：

1. `Server/src/services/tools/manage_<domain>.py`
2. `Server/src/cli/commands/<domain>.py`
3. `MCPForUnity/Editor/Tools/Manage<Domain>.cs`
4. `Server/tests/test_manage_<domain>.py` 或 `Server/tests/integration/...`
5. `TestProjects/UnityMCPTests/Assets/Tests/...`

如果只是 C# custom tool 或 Resource，不要机械添加不需要的 Python 层；先根据调用路径确认真正需要暴露的表面。

## 禁止事项

- 不在 `main`、`gameempire/protect` 或任何非 `beta` 分支开发或提交私有需求。
- 不把 `mcpServerPackageSource` 改回上游源，除非用户明确要求。
- 不跳过必要测试后声称完成。
- 不新增只使用一次的 helper。
- 不对未修改代码补大段注释或 docstring。
- 不引入平台专用 shell 语法来实现长期 Makefile 目标。
