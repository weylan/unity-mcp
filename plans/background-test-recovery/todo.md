# Tasks: Unity MCP Background Test Recovery

> Updated: 2026-09-02

## In Progress

- [ ] Existing `make release` generation/commit of `MCPForUnity/package.json`, followed by consumer upgrade and shared-Editor background observation

## Pending

- [ ] Fresh consumer pin/guard/runtime projection and live background poll after release

## Blocked

## Completed

- [x] Observational getter and lifecycle watchdog — `TestJobManager.cs`, `run_tests.py`
- [x] Strict V3 lifecycle persistence migration and status/phase/owner cross-validation — `TestJobManager.cs`
- [x] Explicit exact-target nudge with remote-hosted fail-closed and structured failure outcome — `run_tests.py`, `focus_nudge.py`
- [x] PID/path/peer transport identity — WebSocket, stdio and server registry/model files
- [x] Tool documentation — `website/docs/reference/tools/`
- [x] Exact regression gates — Python `1438 passed, 2 skipped`; Node exact `4/4`; isolated Unity `2022.3.62f3c1` C# exact `8/8`
