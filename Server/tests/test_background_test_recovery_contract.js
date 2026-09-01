#!/usr/bin/env node
"use strict";

const { spawnSync } = require("node:child_process");
const path = require("node:path");

// Contract discovery and execution deliberately consume this one table.
const EXACT_TESTS = new Map([
  [
    "UnityMcp.Server.GetTestJob.IsObservationalForStalledUnfocusedJob",
    "tests/integration/test_run_tests_async.py::test_get_test_job_is_observational_for_stalled_unfocused_job",
  ],
  [
    "UnityMcp.Server.NudgeTestJob.UsesExactLocalIdentity",
    "tests/test_focus_nudge.py::test_nudge_test_job_uses_exact_local_identity",
  ],
  [
    "UnityMcp.Server.WebSocketRegistration.PersistsPidPathAndPeer",
    "tests/integration/test_plugin_hub_websocket_auth.py::test_websocket_registration_persists_pid_path_and_peer",
  ],
  [
    "UnityMcp.Server.StdioDiscovery.PreservesPidAndProjectIdentity",
    "tests/test_models_characterization.py::test_stdio_discovery_preserves_pid_and_project_identity",
  ],
]);

if (process.argv.includes("--list-tests-json")) {
  process.stdout.write(`${JSON.stringify({ tests: [...EXACT_TESTS.keys()] })}\n`);
  process.exit(0);
}

const selected = process.argv.slice(2);
const unknown = selected.filter((name) => !EXACT_TESTS.has(name));
if (unknown.length > 0) {
  process.stderr.write(`Unknown exact test id(s): ${unknown.join(", ")}\n`);
  process.exit(2);
}

const entries = selected.length > 0
  ? selected.map((name) => [name, EXACT_TESTS.get(name)])
  : [...EXACT_TESTS.entries()];
const serverRoot = path.resolve(__dirname, "..");

for (const [exactId, pytestNodeId] of entries) {
  process.stdout.write(`[exact-red] ${exactId} -> ${pytestNodeId}\n`);
  const result = spawnSync(
    process.platform === "win32" ? "uv.exe" : "uv",
    ["run", "--frozen", "pytest", "-q", pytestNodeId],
    { cwd: serverRoot, encoding: "utf8", stdio: "inherit", windowsHide: true },
  );
  if (result.error) {
    process.stderr.write(`${result.error.message}\n`);
    process.exit(1);
  }
  if (result.status !== 0) process.exit(result.status ?? 1);
}
