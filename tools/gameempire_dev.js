#!/usr/bin/env node
"use strict";

const { spawnSync } = require("child_process");
const fs = require("fs");
const path = require("path");

const {
  DEFAULTS: RELEASE_DEFAULTS,
  serverPackageSourceForTag,
  updateServerPackageSource,
} = require("./gameempire_merge_latest.js");

function privateRepoFromReleaseDefaults() {
  return RELEASE_DEFAULTS.serverPackageSourcePrefix.replace(/^git\+/, "").replace(/@$/, "");
}

const DEFAULTS = {
  privateRepo: privateRepoFromReleaseDefaults(),
  serverPackageSourceSuffix: RELEASE_DEFAULTS.serverPackageSourceSuffix,
};

const COMMANDS = new Set([
  "status",
  "check",
  "test-server",
  "test-tools",
  "test-tools-full",
  "test-unity",
  "unity-test",
  "preflight",
  "set-private-source",
]);

function usage() {
  return [
    "Usage: node tools/gameempire_dev.js <command> [options] [-- extra args]",
    "",
    "Commands:",
    "  status                         Print private fork status",
    "  check                          Validate versions and private server package source",
    "  test-server [pytest args]       Run Server pytest suite",
    "  test-tools                      Run fast Node tool tests",
    "  test-tools-full                 Run Node tool tests and Python characterization tests",
    "  test-unity [--mode MODE]        Run Unity tests through Server CLI (requires Unity bridge)",
    "  unity-test [--mode MODE]        Alias for test-unity",
    "  preflight                      Run check, tool tests, and Server pytest",
    "  set-private-source <tag>        Update MCPForUnity/package.json server source to private tag",
    "",
    "Options:",
    "  --cwd <path>                    Repository root (default: current directory)",
    "  --mode <EditMode|PlayMode>      Unity test mode (default: EditMode)",
    "  --failed-only                  Show only failed Unity tests",
    "  --pytest-k <expr>               Add pytest -k expression for test-server",
    "  --private-repo <url>            Private fork URL for mcpServerPackageSource",
    "  --dry-run                      Print writes without changing files",
  ].join("\n");
}

function parseArgs(argv) {
  const parsed = {
    command: "status",
    opts: {
      cwd: process.cwd(),
      privateRepo: DEFAULTS.privateRepo,
      dryRun: false,
      mode: "EditMode",
      failedOnly: false,
      tag: "",
    },
    extraArgs: [],
  };

  const args = [...argv];
  if (args[0] && COMMANDS.has(args[0])) {
    parsed.command = args.shift();
  } else if (args[0] && !args[0].startsWith("-")) {
    throw new Error(`Unknown command: ${args[0]}`);
  }

  while (args.length) {
    const arg = args.shift();
    if (arg === "--") {
      parsed.extraArgs.push(...args);
      break;
    }
    if (arg === "--help" || arg === "-h") {
      parsed.help = true;
    } else if (arg === "--cwd") {
      parsed.opts.cwd = args.shift() || parsed.opts.cwd;
    } else if (arg === "--mode") {
      parsed.opts.mode = args.shift() || parsed.opts.mode;
    } else if (arg === "--failed-only") {
      parsed.opts.failedOnly = true;
    } else if (arg === "--pytest-k") {
      parsed.extraArgs.push("-k", args.shift() || "");
    } else if (arg === "--private-repo") {
      parsed.opts.privateRepo = args.shift() || parsed.opts.privateRepo;
    } else if (arg === "--dry-run") {
      parsed.opts.dryRun = true;
    } else if (parsed.command === "set-private-source" && !parsed.opts.tag) {
      parsed.opts.tag = arg;
    } else {
      parsed.extraArgs.push(arg);
    }
  }

  return parsed;
}

function readJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, "utf8"));
}

function readText(filePath) {
  return fs.readFileSync(filePath, "utf8");
}

function relative(cwd, filePath) {
  return path.relative(cwd, filePath).split(path.sep).join("/");
}

function packageJsonPath(opts) {
  return path.join(opts.cwd, "MCPForUnity", "package.json");
}

function manifestJsonPath(opts) {
  return path.join(opts.cwd, "manifest.json");
}

function pyprojectPath(opts) {
  return path.join(opts.cwd, "Server", "pyproject.toml");
}

function readPyprojectVersion(filePath) {
  const content = readText(filePath);
  const match = /^version = "([^"]+)"/m.exec(content);
  if (!match) {
    throw new Error(`Missing project version in ${filePath}`);
  }
  return match[1];
}

function privateSourcePrefix(opts) {
  return `git+${opts.privateRepo}@`;
}

function releaseSourceOptions(opts) {
  return {
    ...RELEASE_DEFAULTS,
    cwd: opts.cwd,
    dryRun: opts.dryRun,
    serverPackageSourcePrefix: privateSourcePrefix(opts),
    serverPackageSourceSuffix: DEFAULTS.serverPackageSourceSuffix,
  };
}

function privateSourceForTag(opts, tag) {
  return serverPackageSourceForTag(tag, releaseSourceOptions(opts));
}

function parsePrivateSource(opts, source) {
  const prefix = privateSourcePrefix(opts);
  if (!source.startsWith(prefix) || !source.endsWith(DEFAULTS.serverPackageSourceSuffix)) {
    return { private: false, tag: "" };
  }
  return {
    private: true,
    tag: source.slice(prefix.length, source.length - DEFAULTS.serverPackageSourceSuffix.length),
  };
}

function collectStatus(opts) {
  const packagePath = packageJsonPath(opts);
  const manifestPath = manifestJsonPath(opts);
  const pyproject = pyprojectPath(opts);

  const packageJson = readJson(packagePath);
  const manifestJson = readJson(manifestPath);
  const source = packageJson.mcpServerPackageSource || "";

  return {
    versions: {
      [relative(opts.cwd, packagePath)]: packageJson.version || "",
      [relative(opts.cwd, pyproject)]: readPyprojectVersion(pyproject),
      [relative(opts.cwd, manifestPath)]: manifestJson.version || "",
    },
    serverPackageSource: {
      value: source,
      expectedPattern: `${privateSourcePrefix(opts)}<tag>${DEFAULTS.serverPackageSourceSuffix}`,
      ...parsePrivateSource(opts, source),
    },
  };
}

function checkVersions(status) {
  const entries = Object.entries(status.versions);
  const unique = new Set(entries.map(([, version]) => version));
  if (unique.size <= 1) return [];

  const detail = entries.map(([file, version]) => `${file}=${version || "<missing>"}`).join(", ");
  return [`Version mismatch: ${detail}`];
}

function checkPrivateSource(status) {
  if (status.serverPackageSource.private && status.serverPackageSource.tag) return [];
  return [
    `MCPForUnity/package.json mcpServerPackageSource must point to ${status.serverPackageSource.expectedPattern}`,
  ];
}

function printStatus(status) {
  console.log("Versions:");
  for (const [file, version] of Object.entries(status.versions)) {
    console.log(`  ${file}: ${version}`);
  }
  console.log("");
  console.log("Private server package source:");
  console.log(`  ${status.serverPackageSource.value || "<missing>"}`);
  console.log(`  private: ${status.serverPackageSource.private ? "yes" : "no"}`);
  if (status.serverPackageSource.tag) {
    console.log(`  tag: ${status.serverPackageSource.tag}`);
  }
}

function run(command, args, options = {}) {
  const result = spawnSync(command, args, {
    cwd: options.cwd,
    encoding: "utf8",
    stdio: options.capture ? ["ignore", "pipe", "pipe"] : "inherit",
    shell: process.platform === "win32",
  });

  if (result.status !== 0) {
    const detail = options.capture ? (result.stderr || result.stdout || "").trim() : "";
    throw new Error(`${command} ${args.join(" ")} failed${detail ? `: ${detail}` : ""}`);
  }

  return options.capture ? result.stdout.trim() : "";
}

function runCheck(opts) {
  const status = collectStatus(opts);
  const errors = [...checkVersions(status), ...checkPrivateSource(status)];
  printStatus(status);
  if (errors.length) {
    console.error("");
    console.error("Check failed:");
    for (const error of errors) {
      console.error(`  - ${error}`);
    }
    return 1;
  }

  console.log("");
  console.log("Check passed.");
  return 0;
}

function runServerTests(opts, extraArgs = []) {
  run("uv", ["run", "--frozen", "pytest", "tests/", "-v", ...extraArgs], {
    cwd: path.join(opts.cwd, "Server"),
  });
  return 0;
}

function runNodeToolTests(opts) {
  run("node", ["tools/tests/test_gameempire_merge_latest.js"], { cwd: opts.cwd });
  run("node", ["tools/tests/test_gameempire_dev.js"], { cwd: opts.cwd });
  return 0;
}

function runPythonToolTests(opts) {
  run("uv", ["run", "--frozen", "pytest", "../tools/tests/", "-v"], { cwd: path.join(opts.cwd, "Server") });
  return 0;
}

function runToolTests(opts) {
  return runNodeToolTests(opts);
}

function runToolTestsFull(opts) {
  runNodeToolTests(opts);
  runPythonToolTests(opts);
  return 0;
}

function runUnityTests(opts, extraArgs = []) {
  const args = ["run", "--frozen", "unity-mcp", "editor", "tests", "--mode", opts.mode, ...extraArgs];
  if (opts.failedOnly) {
    args.push("--failed-only");
  }
  run("uv", args, { cwd: path.join(opts.cwd, "Server") });
  return 0;
}

function runPreflight(opts) {
  const checkCode = runCheck(opts);
  if (checkCode !== 0) return checkCode;
  runNodeToolTests(opts);
  run("uv", ["run", "--frozen", "pytest", "../tools/tests/", "tests/", "-v"], { cwd: path.join(opts.cwd, "Server") });
  return 0;
}

function updatePrivateSource(opts, tag) {
  if (!tag) {
    throw new Error("set-private-source requires a tag, for example gameempire-mcp-v20260506.2");
  }

  return updateServerPackageSource(releaseSourceOptions(opts), tag);
}

function main(argv = process.argv.slice(2)) {
  const parsed = parseArgs(argv);
  if (parsed.help) {
    console.log(usage());
    return 0;
  }

  const opts = { ...DEFAULTS, ...parsed.opts, cwd: path.resolve(parsed.opts.cwd) };

  switch (parsed.command) {
    case "status":
      printStatus(collectStatus(opts));
      return 0;
    case "check":
      return runCheck(opts);
    case "test-server":
      return runServerTests(opts, parsed.extraArgs);
    case "test-tools":
      return runToolTests(opts);
    case "test-tools-full":
      return runToolTestsFull(opts);
    case "test-unity":
    case "unity-test":
      return runUnityTests(opts, parsed.extraArgs);
    case "preflight":
      return runPreflight(opts);
    case "set-private-source": {
      const changed = updatePrivateSource(opts, opts.tag);
      console.log(changed ? "Private source updated." : "Private source already up to date.");
      return 0;
    }
    default:
      throw new Error(`Unknown command: ${parsed.command}`);
  }
}

if (require.main === module) {
  try {
    process.exitCode = main();
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}

module.exports = {
  DEFAULTS,
  checkPrivateSource,
  checkVersions,
  collectStatus,
  main,
  parseArgs,
  parsePrivateSource,
  privateSourceForTag,
  updatePrivateSource,
};
