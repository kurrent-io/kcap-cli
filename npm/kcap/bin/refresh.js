// Refreshes kcap's user-scope coding-agent installs after an install or upgrade, by running
// `kcap refresh` — the native binary owns the refresh list and the Claude plugin's .mcp.json patch,
// so every installer (npm, the install scripts, a script install's `kcap update`) runs the same one.
//
// Shared by:
// - postinstall.js — runs after `npm install -g @kurrent/kcap` (incl. upgrades),
//   when the package manager allows install scripts to run.
// - kcap.js `update` — runs after a user-initiated `kcap update`, which works
//   even when the package manager blocks postinstall scripts.
//
// This file must never require kcap.js: during `kcap update`, runUpdate executes
// before kcap.js's final module.exports assignment, so it would observe partial
// exports.

const { spawnSync } = require("child_process");

// Above the binary's own worst case: eleven steps, each bounded at 60s.
const REFRESH_TIMEOUT_MS = 15 * 60_000;

// Runs `kcap refresh` through the given launcher (an absolute path to kcap.js), so the NEW
// launcher and the NEW binary run it after an upgrade. Never throws and never fails the caller:
// a failed refresh must never break `npm install` or report `kcap update` as failed.
function runRefreshes(launcherPath) {
  try {
    spawnSync(process.execPath, [launcherPath, "refresh"], {
      stdio: "ignore",
      env: process.env,
      timeout: REFRESH_TIMEOUT_MS,
      killSignal: "SIGKILL",
      windowsHide: true,
    });
  } catch {
    // Never fail the caller.
  }
}

module.exports = { runRefreshes };
