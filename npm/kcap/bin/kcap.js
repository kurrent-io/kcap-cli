#!/usr/bin/env node

// Resolves and exec's the native kcap binary for the current platform.

const { execFileSync, spawnSync } = require("child_process");
const path = require("path");
const fs = require("fs");

function isMusl() {
  if (process.platform !== "linux") return false;
  try {
    return fs.readFileSync("/usr/bin/ldd", "utf8").includes("musl");
  } catch {
    try {
      return fs.readdirSync("/lib").some((f) => f.startsWith("ld-musl-"));
    } catch {
      return false;
    }
  }
}

const PLATFORM_PACKAGES = {
  "darwin-arm64":      "@kurrent/kcap-darwin-arm64",
  "linux-x64":         "@kurrent/kcap-linux-x64",
  "linux-arm64":       "@kurrent/kcap-linux-arm64",
  "linux-musl-x64":    "@kurrent/kcap-linux-musl-x64",
  "linux-musl-arm64":  "@kurrent/kcap-linux-musl-arm64",
  "win32-x64":         "@kurrent/kcap-win-x64",
};

const musl = process.platform === "linux" && isMusl() ? "-musl" : "";
const platformKey = `${process.platform}${musl}-${process.arch}`;
const packageName = PLATFORM_PACKAGES[platformKey];

if (!packageName) {
  console.error(`Unsupported platform: ${platformKey}`);
  console.error(`Supported: ${Object.keys(PLATFORM_PACKAGES).join(", ")}`);
  process.exit(1);
}

// Resolve the platform package
let binaryDir;
try {
  binaryDir = path.dirname(require.resolve(`${packageName}/package.json`));
} catch {
  console.error(`Platform package ${packageName} is not installed.`);
  console.error(`Try: npm install -g @kurrent/kcap`);
  process.exit(1);
}

const ext = process.platform === "win32" ? ".exe" : "";
const binaryPath = path.join(binaryDir, "bin", `kcap${ext}`);

if (!fs.existsSync(binaryPath)) {
  console.error(`Binary not found at ${binaryPath}`);
  process.exit(1);
}

// Ensure the binary is executable (npm doesn't always preserve permissions)
try {
  fs.accessSync(binaryPath, fs.constants.X_OK);
} catch {
  try { fs.chmodSync(binaryPath, 0o755); } catch {}
}

// `kcap update` for npm-global installs is driven HERE, in the Node launcher,
// not by the native binary. The OS locks an executable image for the whole
// process lifetime but a script only during load — so by driving the upgrade
// from this script (with the native binary not running) npm can overwrite the
// binary even on Windows, with no temp-file/rename/detached-helper dance.
// `--check` (JSON probe) and `--help` fall through to the native binary.
{
  const updArgs   = process.argv.slice(3);
  const checkOnly = updArgs.includes("--check");
  const wantsHelp = updArgs.some((a) => a === "--help" || a === "-h");
  if (process.argv[2] === "update" && !checkOnly && !wantsHelp) {
    runUpdate(binaryPath); // never returns
  }
}

// Exec the native binary, replacing this process
try {
  execFileSync(binaryPath, process.argv.slice(2), {
    stdio: "inherit",
    env: process.env,
  });
  process.exit(0);
} catch (e) {
  if (e.status !== null) {
    process.exit(e.status);
  }
  process.exit(1);
}

// Performs `kcap update`: upgrade the global npm package, then refresh
// user-scope skills/hooks. Always exits the process; never returns.
function runUpdate(binaryPath) {
  // On Windows, `npm` is a .cmd shim; Node refuses to spawn .cmd/.bat directly
  // (2024 command-injection fix), so route npm through a shell there. Our npm
  // args are static, so shell use is safe.
  const npmOpts = process.platform === "win32" ? { shell: true } : {};

  // Resolve npm's global root so we can (a) confirm this is a global install we
  // own and (b) check we can write to it before starting.
  let globalRoot = null;
  try {
    globalRoot = execFileSync("npm", ["root", "-g"], { encoding: "utf8", ...npmOpts }).trim();
  } catch {}

  let isGlobalNpm = false;
  try {
    if (globalRoot) {
      const root = fs.realpathSync(globalRoot) + path.sep;
      isGlobalNpm = fs.realpathSync(__filename).startsWith(root);
    }
  } catch {}

  if (!isGlobalNpm) {
    console.error("kcap was not installed via a global npm install, so it can't");
    console.error("self-update. Update it the way you installed it, e.g.:");
    console.error("  npm install -g @kurrent/kcap@latest   (npm)");
    console.error("  brew upgrade kcap                      (Homebrew)");
    process.exit(1);
  }

  // Ask the native binary whether a newer version exists. This short-lived
  // child fully EXITS before we run npm, so the binary file is unlocked when
  // npm overwrites it (matters on Windows). `--no-update-check` keeps the
  // background nudge from racing onto stderr.
  try {
    const out  = execFileSync(binaryPath, ["update", "--check", "--no-update-check"], { encoding: "utf8" });
    // The probe prints one JSON line; take the last {...} line in case anything
    // else (e.g. a git-remote warning) landed on stdout first.
    const line = out.split(/\r?\n/).reverse().find((l) => l.trim().startsWith("{"));
    const info = JSON.parse(line);
    // Only short-circuit when the probe is CONFIDENT we're up to date: `newer`
    // explicitly false AND a known current version. `newer: null` (version
    // unknown / check failed) falls through to the upgrade rather than stranding
    // the user on a stale CLI.
    if (info && info.newer === false && info.current) {
      console.log(`Already up to date: ${info.current}`);
      process.exit(0);
    }
    if (info && info.newer) {
      console.log(`Updating kcap: ${info.current} → ${info.latest}`);
    }
  } catch {
    // Couldn't determine — fall through and let npm decide (it's idempotent).
  }

  // Fail clearly instead of half-installing under a root-owned prefix.
  try {
    fs.accessSync(globalRoot, fs.constants.W_OK);
  } catch {
    console.error(`Cannot write to the global npm directory (${globalRoot}).`);
    console.error("Re-run with the permissions you installed kcap with, or reinstall");
    console.error("to a user-owned prefix to avoid needing elevated rights on updates.");
    process.exit(1);
  }

  const res = spawnSync("npm", ["install", "-g", "@kurrent/kcap@latest"], {
    stdio: "inherit",
    windowsHide: true,
    ...npmOpts,
  });
  if (res.status !== 0) {
    console.error("npm install failed; kcap was not updated.");
    process.exit(res.status == null ? 1 : res.status);
  }

  // npm has now overwritten kcap.js, refresh.js, and the binary. require()
  // reads the NEW refresh.js, which spawns the NEW launcher -> NEW binary.
  console.log("Refreshing hooks and skills…");
  require("./refresh").runRefreshes(fs.realpathSync(__filename));
  console.log("kcap updated.");
  process.exit(0);
}
