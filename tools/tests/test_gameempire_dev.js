#!/usr/bin/env node
"use strict";

const assert = require("assert");
const fs = require("fs");
const os = require("os");
const path = require("path");

const {
  DEFAULTS,
  checkPrivateSource,
  checkVersions,
  collectStatus,
  parseArgs,
  updatePrivateSource,
} = require("../gameempire_dev.js");

function writeJson(filePath, value) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  fs.writeFileSync(filePath, `${JSON.stringify(value, null, 2)}\n`, "utf8");
}

function withTempRepo(fn) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "gameempire-dev-test-"));
  try {
    writeJson(path.join(dir, "MCPForUnity", "package.json"), {
      name: "com.coplaydev.unity-mcp",
      version: "9.6.9-beta.7",
      mcpServerPackageSource:
        "git+https://github.com/weylan/unity-mcp.git@gameempire-mcp-v20260506.1#subdirectory=Server",
    });
    writeJson(path.join(dir, "manifest.json"), {
      name: "Unity MCP",
      version: "9.6.9-beta.7",
    });
    fs.mkdirSync(path.join(dir, "Server"), { recursive: true });
    fs.writeFileSync(
      path.join(dir, "Server", "pyproject.toml"),
      '[project]\nname = "mcpforunityserver"\nversion = "9.6.9-beta.7"\n',
      "utf8"
    );
    fn(dir);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
}

assert.deepStrictEqual(parseArgs(["check"]).command, "check");
assert.deepStrictEqual(parseArgs(["test-server", "-k", "camera"]).extraArgs, ["-k", "camera"]);
assert.deepStrictEqual(parseArgs(["test-server", "--", "-k", "camera"]).extraArgs, ["-k", "camera"]);
assert.deepStrictEqual(parseArgs(["test-server", "--pytest-k", "camera"]).extraArgs, ["-k", "camera"]);
assert.deepStrictEqual(parseArgs(["test-tools-full"]).command, "test-tools-full");
assert.deepStrictEqual(parseArgs(["unity-test", "--mode", "PlayMode"]).opts.mode, "PlayMode");
assert.strictEqual(parseArgs(["unity-test"]).opts.mode, "EditMode");
assert.strictEqual(parseArgs(["set-private-source", "gameempire-mcp-v20260506.2"]).opts.tag, "gameempire-mcp-v20260506.2");
assert.throws(() => parseArgs(["chek"]), /Unknown command: chek/);

withTempRepo((cwd) => {
  const status = collectStatus({ cwd, ...DEFAULTS });

  assert.deepStrictEqual(status.versions, {
    "MCPForUnity/package.json": "9.6.9-beta.7",
    "Server/pyproject.toml": "9.6.9-beta.7",
    "manifest.json": "9.6.9-beta.7",
  });
  assert.strictEqual(status.serverPackageSource.private, true);
  assert.strictEqual(status.serverPackageSource.tag, "gameempire-mcp-v20260506.1");
  assert.strictEqual(checkVersions(status).length, 0);
  assert.strictEqual(checkPrivateSource(status).length, 0);
});

withTempRepo((cwd) => {
  const manifestPath = path.join(cwd, "manifest.json");
  writeJson(manifestPath, { name: "Unity MCP", version: "9.6.8" });

  const status = collectStatus({ cwd, ...DEFAULTS });
  const errors = checkVersions(status);

  assert.deepStrictEqual(errors, [
    "Version mismatch: MCPForUnity/package.json=9.6.9-beta.7, Server/pyproject.toml=9.6.9-beta.7, manifest.json=9.6.8",
  ]);
});

withTempRepo((cwd) => {
  const packagePath = path.join(cwd, "MCPForUnity", "package.json");
  const packageJson = JSON.parse(fs.readFileSync(packagePath, "utf8"));
  packageJson.mcpServerPackageSource =
    "git+https://github.com/CoplayDev/unity-mcp.git@v9.6.8#subdirectory=Server";
  writeJson(packagePath, packageJson);

  const status = collectStatus({ cwd, ...DEFAULTS });
  const errors = checkPrivateSource(status);

  assert.deepStrictEqual(errors, [
    "MCPForUnity/package.json mcpServerPackageSource must point to git+https://github.com/weylan/unity-mcp.git@<tag>#subdirectory=Server",
  ]);
});

withTempRepo((cwd) => {
  const packagePath = path.join(cwd, "MCPForUnity", "package.json");
  const packageJson = JSON.parse(fs.readFileSync(packagePath, "utf8"));
  packageJson.mcpServerPackageSource =
    "git+https://github.com/weylan/unity-mcp.git@gameempire-mcp-v20260506.1#subdirectory=Server";
  writeJson(packagePath, packageJson);

  const status = collectStatus({ cwd, ...DEFAULTS, privateRepo: "https://example.com/private/unity-mcp.git" });
  const errors = checkPrivateSource(status);

  assert.deepStrictEqual(errors, [
    "MCPForUnity/package.json mcpServerPackageSource must point to git+https://example.com/private/unity-mcp.git@<tag>#subdirectory=Server",
  ]);
});

withTempRepo((cwd) => {
  assert.strictEqual(updatePrivateSource({ cwd, ...DEFAULTS }, "gameempire-mcp-v20260506.2"), true);
  assert.strictEqual(updatePrivateSource({ cwd, ...DEFAULTS }, "gameempire-mcp-v20260506.2"), false);

  const packageJson = JSON.parse(fs.readFileSync(path.join(cwd, "MCPForUnity", "package.json"), "utf8"));
  assert.strictEqual(
    packageJson.mcpServerPackageSource,
    "git+https://github.com/weylan/unity-mcp.git@gameempire-mcp-v20260506.2#subdirectory=Server"
  );
});

function SetPrivateSourceRejectsLatestAndLeavesOtherFilesUntouched() {
  withTempRepo((cwd) => {
    const packagePath = path.join(cwd, "MCPForUnity", "package.json");
    const pyprojectPath = path.join(cwd, "Server", "pyproject.toml");
    const packageBefore = fs.readFileSync(packagePath, "utf8");
    const pyprojectBefore = fs.readFileSync(pyprojectPath, "utf8");

    assert.throws(
      () => updatePrivateSource({ cwd, ...DEFAULTS }, "gameempire-mcp-latest"),
      /immutable/i
    );
    assert.strictEqual(fs.readFileSync(packagePath, "utf8"), packageBefore);
    assert.strictEqual(fs.readFileSync(pyprojectPath, "utf8"), pyprojectBefore);
  });
}

SetPrivateSourceRejectsLatestAndLeavesOtherFilesUntouched();

console.log("ok - gameempire private dev helpers");
