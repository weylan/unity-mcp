#!/usr/bin/env node
"use strict";

const assert = require("assert");
const fs = require("fs");
const os = require("os");
const path = require("path");

const {
  DEFAULTS,
  serverPackageSourceForTag,
  readServerPackageSource,
  updateServerPackageSource,
} = require("../gameempire_merge_latest.js");

function withTempRepo(fn) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "gameempire-merge-test-"));
  try {
    fs.mkdirSync(path.join(dir, "MCPForUnity"), { recursive: true });
    fs.writeFileSync(
      path.join(dir, "MCPForUnity", "package.json"),
      `${JSON.stringify({
        name: "com.coplaydev.unity-mcp",
        version: "9.6.7-beta.5",
        mcpServerPackageSource: "git+https://github.com/weylan/unity-mcp.git@gameempire-mcp-v20260426.4#subdirectory=Server",
      }, null, 2)}\n`,
      "utf8"
    );
    fn(dir);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
}

withTempRepo((cwd) => {
  const opts = { cwd, ...DEFAULTS, dryRun: false };
  const tag = "gameempire-mcp-v20260426.6";

  assert.strictEqual(
    serverPackageSourceForTag(tag, opts),
    "git+https://github.com/weylan/unity-mcp.git@gameempire-mcp-v20260426.6#subdirectory=Server"
  );

  assert.strictEqual(updateServerPackageSource(opts, tag), true);
  assert.strictEqual(readServerPackageSource(opts), serverPackageSourceForTag(tag, opts));

  assert.strictEqual(updateServerPackageSource(opts, tag), false);
});

console.log("ok - gameempire merge updates mcpServerPackageSource to immutable tag");
