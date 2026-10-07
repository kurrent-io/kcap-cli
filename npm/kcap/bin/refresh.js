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
// a failed refresh must never break `npm install` or report `kcap update` as failed. Its stderr
// (which steps failed, a plugin patch that could not be written) is passed on through `warn`.
function runRefreshes(launcherPath, warn = console.warn) {
  try {
    const result = spawnSync(process.execPath, [launcherPath, "refresh"], {
      stdio: ["ignore", "ignore", "pipe"],
      encoding: "utf8",
      env: process.env,
      timeout: REFRESH_TIMEOUT_MS,
      killSignal: "SIGKILL",
      windowsHide: true,
    });
    const stderr = (result.stderr || "").trim();
    if (stderr) warn(stderr);
    if (result.status !== 0)
      warn("kcap: some agent integrations were not refreshed; run `kcap refresh` to retry.");
  } catch {
    // Never fail the caller.
  }
}

module.exports = { runRefreshes };
