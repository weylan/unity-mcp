# Known EditMode Test Failures (gameempire fork)

This fork carries a small, stable set of EditMode test failures that are **not regressions**.
They are consequences of this fork's deliberate divergence from upstream (private server
package source) or of machine-local Editor state. Do **not** treat them as blocking, and do
**not** count them against a "0 fail" acceptance gate for fork changes — gate on *net-new*
failures instead.

Baseline captured on `gameempire-mcp-v20260706.2` (HEAD `86023a95`), Unity 2022.3.62f3c1,
Linux headless: **1115 total / 1042 passed / 5 failed / 18 inconclusive / 50 skipped**. The 5
failures below are the entire failing set; the 18 inconclusive are `ManageGraphicsTests`
`Assume.That` environment skips (URP/Volume absent) and are benign.

## Structural (fork-inherent) — 4 tests

`MCPForUnityTests.Editor.Helpers.CodexConfigHelperTests`:
- `BuildCodexServerBlock_OnNonWindows_ExcludesEnv`
- `BuildCodexServerBlock_OnWindows_IncludesSystemRootEnv`
- `UpsertCodexServerBlock_OnNonWindows_ExcludesEnv`
- `UpsertCodexServerBlock_OnWindows_IncludesSystemRootEnv`

**Failure message:** `Args should contain PyPI package reference — Expected: True But was: False`

**Cause:** these assert the generated `uvx` args contain the upstream PyPI reference
`mcpforunityserver`. This fork rewrites `MCPForUnity/package.json` `mcpServerPackageSource`
to a private `git+https://github.com/weylan/unity-mcp.git@gameempire-mcp-v*#subdirectory=Server`
source (see `tools/gameempire_dev.js` `checkPrivateSource`), so the PyPI reference is
intentionally absent. **Structural — will stay red for as long as the fork ships a private
server source.** Not worth patching the upstream tests (they'd re-conflict on every merge);
if desired later, fork them to accept the git+ source pattern.

## Environment / machine-local — 1 test

`MCPForUnityTests.Editor.Services.Server.ServerCommandBuilderTests.TryBuildCommand_LocalServerWithVenv_UsesPythonEntrypoint`

**Failure message:** expected the command to contain `--http-url http://localhost:8080`,
but got `... Server/src/main.py --transport http --http-url http://127.0.0.1:8080 --project-scoped-tools ...`

**Cause:** the builder reads real machine-local Editor state (`127.0.0.1:8080` +
`--project-scoped-tools`, an upstream #596 feature) instead of the test's injected
`localhost:8080`. This is EditorPrefs / EditorConfigurationCache pollution on the test host,
not a code defect. It may pass on a clean-config machine. Worth a small isolation fix
(reset the relevant prefs in test setup) if it becomes noisy, but it does not block.

## How to use this file

When running `make test-unity` (or headless EditMode), subtract these 5 from the fail count.
A fork change is clean iff it introduces **no failures beyond this list**. Update this file if
the baseline count changes or a listed test starts passing (e.g. after a clean-config run or a
test-isolation fix).
