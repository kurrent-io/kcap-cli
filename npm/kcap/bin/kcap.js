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

// Resolves the npm dist-tag to install for `kcap update`, based on the
// resolved channel reported by `kcap update --check`'s `install_tag` field.
// Falls back to "latest" when the probe is missing, failed, or has no tag,
// preserving today's default behavior.
function resolveInstallSpec(info) {
  const tag = info && typeof info.install_tag === "string" && info.install_tag
    ? info.install_tag
    : "latest";
  return `@kurrent/kcap@${tag}`;
}

// Builds the arg list for the `kcap update --check` probe, forwarding only
// the channel-switch flags (`--beta`/`--stable`) from the user's `kcap update`
// invocation. Nothing else is forwarded — the probe already supplies `--check`
// and `--no-update-check` itself, so this is a controlled call.
function probeArgs(updArgs) {
  const channelFlags = (updArgs || []).filter((a) => a === "--beta" || a === "--stable");
  return ["update", "--check", "--no-update-check", ...channelFlags];
}

// Everything below actually DOES something (resolves the binary, execs it,
// runs the update flow), so it's guarded to only run when this file is
// executed directly — not when `require()`d (e.g. by the test).
if (require.main === module) {
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
      runUpdate(binaryPath, updArgs); // never returns
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
}

// Performs `kcap update`: upgrade the global npm package, then refresh
// user-scope skills/hooks. Always exits the process; never returns.
function runUpdate(binaryPath, updArgs) {
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
  let info = null;
  try {
    const out  = execFileSync(binaryPath, probeArgs(updArgs), { encoding: "utf8" });
    // The probe prints one JSON line; take the last {...} line in case anything
    // else (e.g. a git-remote warning) landed on stdout first.
    const line = out.split(/\r?\n/).reverse().find((l) => l.trim().startsWith("{"));
    info = JSON.parse(line);
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

  // Windows preflight: a running kcap-daemon.exe locks the binary, so `npm install`
  // would FAIL to overwrite it. Detect a running daemon and abort with instructions
  // BEFORE attempting the (doomed) install. macOS/Linux can replace the file in place,
  // so this guard is Windows-only.
  if (process.platform === "win32") {
    try {
      const status = execFileSync(binaryPath, ["daemon", "status"], { encoding: "utf8" });
      if (/running \(PID/i.test(status)) {
        console.error("A kcap daemon is running and locks the binary, so the update can't");
        console.error("replace it. Stop it first, then re-run `kcap update`:");
        console.error("  kcap daemon service stop   (if installed as a service)");
        console.error("  kcap daemon stop           (otherwise)");
        process.exit(1);
      }
    } catch {
      // status probe failed (no daemon / old binary) — fall through to the normal install.
    }
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

  const res = spawnSync("npm", ["install", "-g", resolveInstallSpec(info)], {
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

  // macOS/Linux: a running daemon self-detects the new binary and restarts when idle.
  // Just inform the user (best-effort; never fail the update for this). On Windows the
  // doomed install was already aborted by the preflight above, so this only runs on Unix.
  if (process.platform !== "win32") {
    try {
      const status = execFileSync(binaryPath, ["daemon", "status"], { encoding: "utf8" });
      if (/running \(PID/i.test(status)) {
        console.log("A kcap daemon is running; it will restart automatically when idle to");
        console.log("pick up the new version. Check with `kcap daemon status`, or apply now");
        console.log("with `kcap daemon restart --force`.");
      }
    } catch {
      // best-effort notice only
    }
  }

  process.exit(0);
}

module.exports = { resolveInstallSpec, probeArgs };
