# 私有 fork 上游合并、发布与消费侧升级经验

本文记录 `weylan/unity-mcp` 私有 fork 在 2026-09-24 完成的一次上游同步、私有发布和 `../unity` 消费侧升级。目的是让下一次维护 `beta` 时可以按清单复用，减少误删私有功能、版本不一致和发布后缓存未刷新等问题。

## 本次结果

- 工作分支：`beta`。
- 合并前 fork 提交：`e58268c7`。
- 本次同步的 `upstream/beta`：`63202654`。
- 上游合并提交：`a4820b40`。
- Unity/Python 版本：`10.2.1-beta.6`。
- 私有发布提交：`9d6158e1`。
- 不可变发布标签：`gameempire-mcp-v20260924.1`。
- `gameempire-mcp-latest`、`origin/beta` 和不可变标签最终都指向 `9d6158e1e424680e33c1e46574aa9ecb8adbb797`。
- 合并前发现的 `TestProjects/AssetStoreUploads` 750 个删除是本地未提交状态，已恢复；上游仍需要这些文件，不能把它们当作清理项删除。
- `../unity` 已由 `make upgrade-mcp` 升级到上述不可变标签，消费侧本地提交为 `855c4bafb`。该提交尚未推送。

本次经验文档只记录维护知识，不触发 `make release`，也不创建或移动新的版本标签。

## 合并前检查

1. 确认当前分支是 `beta`，同时确认工作区状态；不要在 `main`、`gameempire/protect` 或临时分支上开发。
2. 先区分用户改动和同步遗留。只有确认 `TestProjects/AssetStoreUploads` 的删除属于本次同步准备工作后，才执行：

   ```bash
   git restore -- TestProjects/AssetStoreUploads
   ```

3. 拉取上游和标签，再做无推送合并：

   ```bash
   git fetch upstream beta --tags
   make merge NO_PUSH=1
   ```

4. 合并过程中随时用下面的命令确认没有遗漏冲突：

   ```bash
   git status --short
   git ls-files -u
   ```

## 冲突处理规则

| 文件或区域 | 处理原则 |
| --- | --- |
| `CLAUDE.md` | 保留本 fork 的中文分支、同步和私有发布规则，同时吸收上游新增技术说明；`AGENTS.md` 必须继续是指向它的软链接。 |
| `Makefile`、`tools/gameempire_*` | 保留私有开发、检查、发布和合并入口，不接受上游批量删除。Makefile 只做薄转发，复杂逻辑放在跨平台 Node/Python 脚本中。 |
| PlayMode、Editor Lock、私有测试和 harness | 保留私有功能和测试约束，逐段吸收上游的兼容改动，不要用上游目录整体覆盖。 |
| `ExecuteCode.cs` | 保留私有 `args/replay`、缓存指纹、锁和统计逻辑；吸收上游 64 项编译缓存上限，并合并两侧测试。缓存上限只限制索引/保留项，不能破坏域重载时的程序集清理。 |
| `ToolDiscovery` 及测试 | 保留私有基线，吸收上游 `TypeCache` 路径和稳定排序测试。排序使用 `FullName`，再用 `Assembly.FullName` 作为稳定的次级键。 |
| `.github/workflows/python-tests.yml` | 使用上游 `uv sync --locked` 逻辑，保证锁文件和 CI 环境一致。 |
| Unity `.meta` 文件 | 对每个 rename 或删除核对 GUID；不能因为私有删除而把旧 GUID 错配给新文件。合并后运行空白检查。 |
| 版本和 Server 源 | `MCPForUnity/package.json`、`Server/pyproject.toml`、根 `manifest.json`、`Server/uv.lock` 统一到同一个版本。发布阶段再把 `mcpServerPackageSource` 改成新的私有不可变标签，不能回退到上游源。 |

解决冲突后先检查 `git diff --check` 和 `git ls-files -u`，再运行项目检查；不要在仍有 unmerged entry 时开始发布。

## 本次合并中值得保留的修复

- `SceneSaveUtility` 返回明确的成功/失败结果；`BuildRunner` 遇到有名字的脏场景保存失败时抛错，未保存的 Untitled 场景只给出可诊断的警告。
- `CommandRegistry` 使用确定性排序，避免反射顺序变化导致工具列表或测试结果漂移。
- `StdioBridgeHost` 修正重发索引，`Stop` 时清空队列和索引并完成等待中的 `TaskCompletionSource`；处理排队任务前检查其是否已经过期，避免停止后继续发送旧请求。

这些修复同时需要对应测试；只解决编译冲突而不保留测试，下一次同步很容易再次回归。

## 验证结果与失败诊断

合并和发布阶段使用以下分层验证：

```bash
make check
make test-tools-full
make preflight
```

本次结果为：工具 characterization 测试 127 passed；`preflight` 为 1580 passed、2 skipped。Unity 原生 batch 编译退出码为 0，local harness 的 smoke leg 为 7/7。

EditMode harness 受到共享 Editor guard/physical fence 阻塞；第一次 `make test-unity MODE=EditMode` 还遇到 8080 server 不可达。两者分别属于共享编辑器互斥和运行环境未启动，不能误判为代码回归。最终回复中必须明确记录这类环境阻塞，不能笼统声称 Unity 全部测试通过。

发布前后应核对实际引用，而不是只看工作区版本：

```bash
git show -s --format='%H %s' gameempire-mcp-vYYYYMMDD.N
git rev-parse gameempire-mcp-latest^{commit}
git ls-remote --tags https://github.com/weylan/unity-mcp.git \
  'gameempire-mcp-latest' 'gameempire-mcp-vYYYYMMDD.N' \
  'gameempire-mcp-vYYYYMMDD.N^{}'
```

本次曾遇到本机 `core.sshcommand` 强制使用不适用的 Gitee 密钥，导致 GitHub 推送认证失败。排查时使用：

```bash
git config --show-origin --get core.sshcommand
```

改用具备 GitHub 权限的 SSH agent 或 HTTPS 后，再用 `git ls-remote` 验证远端 ref；不要在经验文档中记录私钥路径或凭据。

## 消费侧升级经验（`../unity`）

消费侧的唯一入口是：

```bash
cd ../unity
make upgrade-mcp
```

本次升级器根据远端 `gameempire-mcp-latest` 解析出 `gameempire-mcp-v20260924.1` 和完整提交号，并同步更新了 Unity manifest、packages lock、guard 常量、运行时断言、smoke 文档和调试文档，共 6 个文件。不要手改升级器生成的这些值。

仅更新 `manifest.json` 不代表 Unity 已经使用新代码。旧的 `Library/PackageCache` 仍可能缓存旧提交。确认没有常驻 Unity Editor 后，可以用项目约定的 batchmode 重新解析包；完成后检查缓存目录、包内 `package.json` 和严格 guard：

```bash
node Tools/unity_mcp_guard_check.js --require-package-cache
node Tools/upgrade_mcp.js --dry-run --json
```

本次缓存从 `com.coplaydev.unity-mcp@e58268c708` 更新到 `com.coplaydev.unity-mcp@9d6158e1e4`，严格 guard 和升级器 dry-run 均通过。消费侧针对性测试也通过：guard 19、升级器 15、runtime 3，`start_mcp` 为 75 passed、2 skipped。

消费侧完整工具测试为 135 个测试文件通过、1 个已有的 `test_eval_records.js` hook 断言失败。该失败与 MCP 升级文件无关，且可单独复现；遇到同类结果时要在报告中区分“本次改动验证通过”和“全仓库基线仍有失败”。

## 下一次同步和发布清单

在 `unity-mcp`：

```bash
git status --short --branch
git ls-files -u
git restore -- TestProjects/AssetStoreUploads  # 仅在确认是同步遗留删除后执行
git fetch upstream beta --tags
make merge NO_PUSH=1
make check
make preflight
make release
```

`make release` 前再次确认四处版本一致、`mcpServerPackageSource` 指向当前私有标签，并确认没有未解决冲突。发布后检查 `origin/beta`、不可变标签和 `gameempire-mcp-latest` 的提交号是否一致。

在 `../unity`：

```bash
make upgrade-mcp
node Tools/unity_mcp_guard_check.js --require-package-cache
node Tools/upgrade_mcp.js --dry-run --json
```

如需刷新 Unity 包缓存，先确认没有用户正在使用的共享 Editor；刷新后查看 Unity batchmode 日志，不要只依据命令退出码。

本次没有删除 `origin` 上历史 `beta-version-*` 分支。这些分支与文件同步无关，清理前需要单独确认其用途。

## 经验文档自身的提交规则

仅更新本文档时，显式暂存文档并提交即可：

```bash
git add -- docs/PRIVATE_FORK_MERGE_RELEASE_LESSONS.md
git diff --cached --check
git commit
```

文档提交不运行 `make release`，不创建或移动版本标签，也不推送远端；发布动作必须由后续明确的发布任务单独执行。
