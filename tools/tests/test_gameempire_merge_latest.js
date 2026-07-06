#!/usr/bin/env node
"use strict";

const assert = require("assert");
const { spawnSync } = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");

const {
  DEFAULTS,
  main,
  parseArgs,
  releaseCurrent,
  serverPackageSourceForTag,
  readServerPackageSource,
  updateServerPackageSource,
  fetchOriginTags,
  mergeLatest,
} = require("../gameempire_merge_latest.js");

function git(cwd, args) {
  const result = spawnSync("git", args, {
    cwd,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"],
  });
  if (result.status !== 0) {
    throw new Error(`git ${args.join(" ")} failed: ${(result.stderr || result.stdout).trim()}`);
  }
  return result.stdout.trim();
}

function silenceConsole(fn) {
  const originalLog = console.log;
  try {
    console.log = () => {};
    return fn();
  } finally {
    console.log = originalLog;
  }
}

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

function withTempGitRepo(fn) {
  withTempRepo((dir) => {
    git(dir, ["init"]);
    git(dir, ["config", "user.name", "GameEmpire Test"]);
    git(dir, ["config", "user.email", "gameempire-test@example.com"]);
    git(dir, ["checkout", "-b", DEFAULTS.targetBranch]);
    git(dir, ["add", "MCPForUnity/package.json"]);
    git(dir, ["commit", "-m", "initial package"]);
    fn(dir);
  });
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

assert.strictEqual(parseArgs([]).push, true);
assert.strictEqual(parseArgs(["--quiet"]).quiet, true);
assert.strictEqual(parseArgs(["--no-push"]).push, false);
assert.strictEqual(parseArgs(["--no-push", "--push"]).push, true);
assert.strictEqual(parseArgs(["--push", "--no-push"]).push, false);
assert.strictEqual(parseArgs([]).mode, "merge");
assert.strictEqual(parseArgs(["--release-only"]).mode, "release");
assert.strictEqual(parseArgs(["--no-merge"]).mode, "release");
assert.strictEqual(parseArgs(["--release-only", "--merge"]).mode, "merge");
assert.strictEqual(parseArgs(["--merge", "--release-only"]).mode, "release");

withTempGitRepo((cwd) => {
  const opts = { cwd, ...DEFAULTS, fetch: false, push: false, dryRun: false, quiet: true };

  const released = silenceConsole(() => releaseCurrent(opts));
  const head = git(cwd, ["rev-parse", "HEAD"]);
  const latest = git(cwd, ["rev-parse", DEFAULTS.latestTag]);
  const headTags = git(cwd, ["tag", "--points-at", "HEAD"]).split(/\r?\n/);

  assert.match(released.immutableTag, /^gameempire-mcp-v\d{8}\.1$/);
  assert.strictEqual(latest, head);
  assert.ok(headTags.includes(released.immutableTag));
  assert.ok(headTags.includes(DEFAULTS.latestTag));
  assert.strictEqual(readServerPackageSource(opts), serverPackageSourceForTag(released.immutableTag, opts));
});

withTempGitRepo((cwd) => {
  assert.strictEqual(silenceConsole(() => main(["--cwd", cwd, "--release-only", "--no-fetch", "--no-push", "--quiet"])), 0);

  const head = git(cwd, ["rev-parse", "HEAD"]);
  const latest = git(cwd, ["rev-parse", DEFAULTS.latestTag]);
  const headTags = git(cwd, ["tag", "--points-at", "HEAD"]).split(/\r?\n/);
  const immutableTag = headTags.find((tag) => /^gameempire-mcp-v\d{8}\.1$/.test(tag));

  assert.ok(immutableTag);
  assert.strictEqual(latest, head);
  assert.strictEqual(readServerPackageSource({ cwd, ...DEFAULTS }), serverPackageSourceForTag(immutableTag, DEFAULTS));
});

withTempGitRepo((cwd) => {
  const opts = { cwd, ...DEFAULTS, fetch: false, push: false, dryRun: false, quiet: true };
  git(cwd, ["remote", "add", opts.upstreamRemote, "."]);
  git(cwd, ["update-ref", `refs/remotes/${opts.upstreamRemote}/${opts.upstreamBranch}`, "HEAD"]);

  const beforeHead = git(cwd, ["rev-parse", "HEAD"]);
  const beforeSource = readServerPackageSource(opts);

  const merged = silenceConsole(() => mergeLatest(opts));

  assert.strictEqual(merged.skipped, true);
  assert.strictEqual(merged.reason, "up-to-date");
  assert.strictEqual(git(cwd, ["rev-parse", "HEAD"]), beforeHead);
  assert.strictEqual(readServerPackageSource(opts), beforeSource);
  assert.strictEqual(git(cwd, ["tag", "--list", `${DEFAULTS.tagPrefix}*`]), "");
  assert.strictEqual(git(cwd, ["tag", "--list", DEFAULTS.latestTag]), "");
});

withTempGitRepo((cwd) => {
  const origin = path.join(cwd, "origin.git");
  const staleHead = git(cwd, ["rev-parse", "HEAD"]);
  git(cwd, ["tag", DEFAULTS.latestTag, staleHead]);
  git(cwd, ["clone", "--bare", ".", origin]);
  git(cwd, ["remote", "add", DEFAULTS.originRemote, origin]);

  const packagePath = path.join(cwd, "MCPForUnity", "package.json");
  const packageJson = JSON.parse(fs.readFileSync(packagePath, "utf8"));
  packageJson.version = "9.6.7-beta.6";
  fs.writeFileSync(packagePath, `${JSON.stringify(packageJson, null, 2)}\n`, "utf8");
  git(cwd, ["add", "MCPForUnity/package.json"]);
  git(cwd, ["commit", "-m", "advance beta"]);

  const remoteHead = git(cwd, ["rev-parse", "HEAD"]);
  git(cwd, ["tag", "-f", DEFAULTS.latestTag, remoteHead]);
  git(cwd, ["push", DEFAULTS.originRemote, DEFAULTS.targetBranch]);
  git(cwd, ["push", DEFAULTS.originRemote, "-f", `refs/tags/${DEFAULTS.latestTag}`]);
  git(cwd, ["tag", "-f", DEFAULTS.latestTag, staleHead]);

  const plainFetch = spawnSync("git", ["fetch", DEFAULTS.originRemote, "--tags"], {
    cwd,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"],
  });
  assert.notStrictEqual(plainFetch.status, 0);

  silenceConsole(() => fetchOriginTags({ cwd, ...DEFAULTS, dryRun: false, quiet: true }));

  assert.strictEqual(git(cwd, ["rev-parse", DEFAULTS.latestTag]), remoteHead);
});

withTempGitRepo((cwd) => {
  const origin = path.join(cwd, "origin.git");
  git(cwd, ["clone", "--bare", ".", origin]);
  git(cwd, ["remote", "add", DEFAULTS.originRemote, origin]);

  silenceConsole(() => fetchOriginTags({ cwd, ...DEFAULTS, dryRun: false, quiet: true }));

  assert.strictEqual(git(cwd, ["tag", "--list", DEFAULTS.latestTag]), "");
});

withTempGitRepo((cwd) => {
  const opts = { cwd, ...DEFAULTS, fetch: false, push: false, dryRun: true, quiet: true };
  git(cwd, ["remote", "add", opts.upstreamRemote, "."]);
  git(cwd, ["update-ref", `refs/remotes/${opts.upstreamRemote}/${opts.upstreamBranch}`, "HEAD"]);

  const originalLog = console.log;
  const lines = [];
  try {
    console.log = (message) => lines.push(String(message));
    const merged = mergeLatest(opts);
    assert.strictEqual(merged.skipped, true);
    assert.strictEqual(merged.reason, "up-to-date");
  } finally {
    console.log = originalLog;
  }

  assert.ok(lines.some((line) => line.includes("merge skipped")));
  assert.ok(!lines.some((line) => line.includes("Unity MCP merge complete")));
  assert.ok(!lines.some((line) => line.includes("DRY-RUN update")));
});

console.log("ok - gameempire merge updates mcpServerPackageSource to immutable tag");
