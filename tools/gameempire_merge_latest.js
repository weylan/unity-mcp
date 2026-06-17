#!/usr/bin/env node
"use strict";

const { spawnSync } = require("child_process");
const fs = require("fs");
const path = require("path");

const DEFAULTS = {
  upstreamRemote: "upstream",
  upstreamUrl: "https://github.com/CoplayDev/unity-mcp.git",
  upstreamBranch: "beta",
  targetBranch: "beta",
  originRemote: "origin",
  tagPrefix: "gameempire-mcp-v",
  latestTag: "gameempire-mcp-latest",
  timezone: "Asia/Shanghai",
  serverPackageSourcePrefix: "git+https://github.com/weylan/unity-mcp.git@",
  serverPackageSourceSuffix: "#subdirectory=Server",
};

function parseArgs(argv) {
  const opts = {
    cwd: process.cwd(),
    ...DEFAULTS,
    fetch: true,
    push: true,
    dryRun: false,
    quiet: false,
    mode: "merge",
  };

  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === "--cwd") opts.cwd = argv[++i] || opts.cwd;
    else if (arg === "--upstream-remote") opts.upstreamRemote = argv[++i] || opts.upstreamRemote;
    else if (arg === "--upstream-url") opts.upstreamUrl = argv[++i] || opts.upstreamUrl;
    else if (arg === "--upstream-branch") opts.upstreamBranch = argv[++i] || opts.upstreamBranch;
    else if (arg === "--target-branch") opts.targetBranch = argv[++i] || opts.targetBranch;
    else if (arg === "--origin-remote") opts.originRemote = argv[++i] || opts.originRemote;
    else if (arg === "--tag-prefix") opts.tagPrefix = argv[++i] || opts.tagPrefix;
    else if (arg === "--latest-tag") opts.latestTag = argv[++i] || opts.latestTag;
    else if (arg === "--timezone") opts.timezone = argv[++i] || opts.timezone;
    else if (arg === "--no-fetch") opts.fetch = false;
    else if (arg === "--push") opts.push = true;
    else if (arg === "--no-push") opts.push = false;
    else if (arg === "--merge") opts.mode = "merge";
    else if (arg === "--release-only" || arg === "--no-merge") opts.mode = "release";
    else if (arg === "--dry-run") opts.dryRun = true;
    else if (arg === "--quiet") opts.quiet = true;
    else if (arg === "--help" || arg === "-h") opts.help = true;
    else throw new Error(`Unknown argument: ${arg}`);
  }

  return opts;
}

function usage() {
  return [
    "Usage: node tools/gameempire_merge_latest.js [options]",
    "",
    "By default, fetches upstream, merges upstream beta into beta,",
    "creates a new immutable gameempire-mcp-vYYYYMMDD.N tag if needed,",
    "and moves gameempire-mcp-latest to that commit.",
    "With --release-only, skips the upstream merge and releases the target branch.",
    "",
    "Options:",
    "  --upstream-url <url>       Default: https://github.com/CoplayDev/unity-mcp.git",
    "  --upstream-remote <name>   Default: upstream",
    "  --upstream-branch <name>   Default: beta",
    "  --target-branch <name>     Default: beta",
    "  --origin-remote <name>     Default: origin",
    "  --tag-prefix <prefix>      Default: gameempire-mcp-v",
    "  --latest-tag <name>        Default: gameempire-mcp-latest",
    "  --no-fetch                 Skip git fetch",
    "  --merge                    Merge upstream before release (default)",
    "  --push                     Push branch, immutable tag, and latest tag (default)",
    "  --no-push                  Do not push after the local branch/tag update",
    "  --release-only             Release the target branch without merging upstream",
    "  --dry-run                  Print git commands without changing the repo",
  ].join("\n");
}

function runGit(args, opts, capture = false) {
  if (opts.dryRun && !capture) {
    console.log(`DRY-RUN git ${args.join(" ")}`);
    return "";
  }

  const result = spawnSync("git", args, {
    cwd: opts.cwd,
    encoding: "utf8",
    stdio: capture ? ["ignore", "pipe", "pipe"] : opts.quiet ? "ignore" : "inherit",
  });

  if (result.status !== 0) {
    const detail = capture ? (result.stderr || result.stdout || "").trim() : "";
    throw new Error(`git ${args.join(" ")} failed${detail ? `: ${detail}` : ""}`);
  }

  return capture ? result.stdout.trim() : "";
}

function gitOutput(args, opts) {
  return runGit(args, opts, true);
}

function remoteExists(name, opts) {
  const result = spawnSync("git", ["remote", "get-url", name], {
    cwd: opts.cwd,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "ignore"],
  });
  return result.status === 0;
}

function ensureClean(opts) {
  const status = gitOutput(["status", "--porcelain"], opts);
  if (status) {
    throw new Error("Refusing to continue with a dirty worktree. Commit/stash changes first.");
  }
}

function ensureRemote(opts) {
  if (remoteExists(opts.upstreamRemote, opts)) return;
  runGit(["remote", "add", opts.upstreamRemote, opts.upstreamUrl], opts);
}

function localBranchExists(branch, opts) {
  const result = spawnSync("git", ["show-ref", "--verify", "--quiet", `refs/heads/${branch}`], {
    cwd: opts.cwd,
  });
  return result.status === 0;
}

function remoteBranchExists(remote, branch, opts) {
  const result = spawnSync("git", ["show-ref", "--verify", "--quiet", `refs/remotes/${remote}/${branch}`], {
    cwd: opts.cwd,
  });
  return result.status === 0;
}

function checkoutTargetBranch(opts) {
  const current = gitOutput(["branch", "--show-current"], opts);
  if (current === opts.targetBranch) return;

  if (localBranchExists(opts.targetBranch, opts)) {
    runGit(["checkout", opts.targetBranch], opts);
    return;
  }

  if (remoteBranchExists(opts.originRemote, opts.targetBranch, opts)) {
    runGit(["checkout", "-b", opts.targetBranch, `${opts.originRemote}/${opts.targetBranch}`], opts);
    return;
  }

  throw new Error(`Target branch not found locally or at ${opts.originRemote}/${opts.targetBranch}: ${opts.targetBranch}`);
}

function parseImmutableTag(tag, prefix) {
  if (!tag.startsWith(prefix)) return null;
  const suffix = tag.slice(prefix.length);
  const match = /^(\d{8})\.(\d+)$/.exec(suffix);
  if (!match) return null;
  return { tag, date: match[1], seq: Number(match[2]) };
}

function compareParsedTags(a, b) {
  const dateCmp = a.date.localeCompare(b.date);
  if (dateCmp !== 0) return dateCmp;
  return a.seq - b.seq;
}

function listImmutableTags(opts) {
  const raw = gitOutput(["tag", "--list", `${opts.tagPrefix}*`], opts);
  return raw
    .split(/\r?\n/)
    .filter(Boolean)
    .map((tag) => parseImmutableTag(tag, opts.tagPrefix))
    .filter(Boolean)
    .sort(compareParsedTags);
}

function todayStamp(timezone) {
  const parts = new Intl.DateTimeFormat("en-CA", {
    timeZone: timezone,
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
  }).formatToParts(new Date());

  const values = Object.fromEntries(parts.map((part) => [part.type, part.value]));
  return `${values.year}${values.month}${values.day}`;
}

function nextTagName(opts) {
  const today = todayStamp(opts.timezone);
  const tags = listImmutableTags(opts);
  const latest = tags.length ? tags[tags.length - 1] : null;
  const targetDate = latest && latest.date > today ? latest.date : today;
  const maxSeq = tags
    .filter((entry) => entry.date === targetDate)
    .reduce((max, entry) => Math.max(max, entry.seq), 0);
  return `${opts.tagPrefix}${targetDate}.${maxSeq + 1}`;
}

function immutableTagsPointingAtHead(opts) {
  const raw = gitOutput(["tag", "--points-at", "HEAD"], opts);
  return raw
    .split(/\r?\n/)
    .filter(Boolean)
    .map((tag) => parseImmutableTag(tag, opts.tagPrefix))
    .filter(Boolean)
    .sort(compareParsedTags)
    .map((entry) => entry.tag);
}

function serverPackageSourceForTag(tag, opts = DEFAULTS) {
  return `${opts.serverPackageSourcePrefix}${tag}${opts.serverPackageSourceSuffix}`;
}

function packageJsonPath(opts) {
  return path.join(opts.cwd, "MCPForUnity", "package.json");
}

function readPackageJson(opts) {
  return JSON.parse(fs.readFileSync(packageJsonPath(opts), "utf8"));
}

function readServerPackageSource(opts) {
  return readPackageJson(opts).mcpServerPackageSource || "";
}

function updateServerPackageSource(opts, immutableTag) {
  const expected = serverPackageSourceForTag(immutableTag, opts);
  const packagePath = packageJsonPath(opts);
  const packageJson = readPackageJson(opts);
  if (packageJson.mcpServerPackageSource === expected) {
    return false;
  }

  if (opts.dryRun) {
    console.log(`DRY-RUN update ${path.relative(opts.cwd, packagePath)} mcpServerPackageSource -> ${expected}`);
    return true;
  }

  packageJson.mcpServerPackageSource = expected;
  fs.writeFileSync(packagePath, `${JSON.stringify(packageJson, null, 2)}\n`, "utf8");
  return true;
}

function commitServerPackageSource(opts, immutableTag) {
  if (!updateServerPackageSource(opts, immutableTag)) {
    return false;
  }

  runGit(["add", "MCPForUnity/package.json"], opts);
  runGit(["commit", "-m", `chore: point MCP server package to ${immutableTag}`], opts);
  return true;
}

function finalizeRelease(opts, action) {
  const existingHeadTags = immutableTagsPointingAtHead(opts);
  const taggedHead = existingHeadTags.length ? existingHeadTags[existingHeadTags.length - 1] : "";
  const immutableTag =
    taggedHead && readServerPackageSource(opts) === serverPackageSourceForTag(taggedHead, opts)
      ? taggedHead
      : nextTagName(opts);

  if (!taggedHead || immutableTag !== taggedHead) {
    commitServerPackageSource(opts, immutableTag);
  }

  if (!immutableTagsPointingAtHead(opts).includes(immutableTag)) {
    runGit(["tag", immutableTag, "HEAD"], opts);
  } else {
    console.log(`HEAD already has immutable tag ${immutableTag}`);
  }

  runGit(["tag", "-f", opts.latestTag, "HEAD"], opts);

  const head = opts.dryRun ? "<dry-run>" : gitOutput(["rev-parse", "HEAD"], opts);
  console.log(`Unity MCP ${action} complete: ${immutableTag} -> ${head}`);
  console.log(`Latest pointer updated: ${opts.latestTag} -> ${immutableTag}`);

  if (opts.push) {
    runGit(["push", opts.originRemote, opts.targetBranch], opts);
    runGit(["push", opts.originRemote, `refs/tags/${immutableTag}`], opts);
    runGit(["push", opts.originRemote, "-f", `refs/tags/${opts.latestTag}`], opts);
  } else {
    console.log("Not pushed because --no-push was selected.");
  }

  return { immutableTag, latestTag: opts.latestTag, head };
}

function mergeLatest(opts) {
  if (!opts.dryRun) {
    ensureClean(opts);
  }
  ensureRemote(opts);

  if (opts.fetch) {
    runGit(["fetch", opts.upstreamRemote, opts.upstreamBranch, "--tags"], opts);
    runGit(["fetch", opts.originRemote, "--tags"], opts);
  }

  checkoutTargetBranch(opts);
  runGit(["merge", "--no-edit", `${opts.upstreamRemote}/${opts.upstreamBranch}`], opts);

  return finalizeRelease(opts, "merge");
}

function releaseCurrent(opts) {
  if (!opts.dryRun) {
    ensureClean(opts);
  }

  if (opts.fetch) {
    runGit(["fetch", opts.originRemote, "--tags"], opts);
  }

  checkoutTargetBranch(opts);
  return finalizeRelease(opts, "release");
}

function main(argv = process.argv.slice(2)) {
  const opts = parseArgs(argv);
  if (opts.help) {
    console.log(usage());
    return 0;
  }

  if (opts.mode === "release") {
    releaseCurrent(opts);
  } else {
    mergeLatest(opts);
  }
  return 0;
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
  parseArgs,
  parseImmutableTag,
  compareParsedTags,
  todayStamp,
  nextTagName,
  serverPackageSourceForTag,
  readServerPackageSource,
  updateServerPackageSource,
  releaseCurrent,
  mergeLatest,
  main,
};
